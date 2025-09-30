using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoBooth
{
    public sealed class FfmpegRecorder : IDisposable
    {
        public enum VEncoder { Nvenc, Qsv, Amf, X264, Mjpeg }

        public sealed record Options(
            string FfmpegPath,
            string CamName,
            string MicName,
            string OutputPath,
            int Width,
            int Height,
            int? Fps = null,            // null => let driver choose
            bool ForceMjpegInput = false, // ignored for dshow; left for future backends
            bool DropLateFrames = false,  // deprecated path removed; fps filter handles pacing
            bool ValidateMode = false,    // run -list_options and verify {WxH@fps}
            bool PreferHardware = true,   // try NVENC -> QSV -> AMF before x264
            bool UseMjpegOutput = false   // MJPEG out (huge files, near-zero CPU)
        );

        private Process? _proc;
        private VEncoder _chosen;
        private bool _disposed;

        public bool IsRunning => _proc is { HasExited: false };

        // Let the UI react when ffmpeg exits (success or error)
        public event Action? Exited;

        public void Dispose()
        {
            if (_disposed) return;
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
            _disposed = true;
        }

        // ---- Public API ------------------------------------------------------

        public async Task<VEncoder> DetectBestEncoderAsync(string ffmpegPath, Action<string>? log = null, CancellationToken ct = default)
        {
            // First see which encoders are compiled in
            var list = await GetEncodersAsync(ffmpegPath, log, ct);
            var candidates = new List<VEncoder>();
            if (list.Contains("h264_nvenc")) candidates.Add(VEncoder.Nvenc);
            if (list.Contains("h264_qsv"))   candidates.Add(VEncoder.Qsv);
            if (list.Contains("h264_amf"))   candidates.Add(VEncoder.Amf);

            // Then probe each candidate with a tiny null encode to verify the device is usable
            foreach (var enc in candidates)
            {
                bool ok = await ProbeEncoderAsync(ffmpegPath, enc, log, ct);
                if (ok) return enc;
                log?.Invoke($"[rec] {enc} not usable; trying next…");
            }
            return VEncoder.X264;
        }

        public async Task<bool> CheckSizeFpsSupportedAsync(Options o, Action<string>? log = null, CancellationToken ct = default)
        {
            var psi = new ProcessStartInfo
            {
                FileName = o.FfmpegPath,
                Arguments = $"-hide_banner -f dshow -list_options true -i video=\"{o.CamName}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) sb.AppendLine(ev.Data); };
            p.ErrorDataReceived  += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) sb.AppendLine(ev.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);

            var text = sb.ToString();
            log?.Invoke("[ffmpeg] list_options:");
            foreach (var line in text.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) log?.Invoke(line.TrimEnd());

            var size = $"{o.Width}x{o.Height}";
            if (o.Fps is null) return text.Contains(size, StringComparison.OrdinalIgnoreCase);

            var rx = new Regex($@"\b{Regex.Escape(size)}\b.*?\b({o.Fps})\s*fps\b", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return rx.IsMatch(text);
        }

        public async Task StartAsync(Options o, Action<string> log, CancellationToken ct = default)
        {
            if (IsRunning) throw new InvalidOperationException("ffmpeg already running.");
            if (!File.Exists(o.FfmpegPath)) throw new FileNotFoundException("ffmpeg.exe not found", o.FfmpegPath);

            // Pick encoder (verified)
            if (o.UseMjpegOutput) _chosen = VEncoder.Mjpeg;
            else if (o.PreferHardware) _chosen = await DetectBestEncoderAsync(o.FfmpegPath, log, ct);
            else _chosen = VEncoder.X264;

            // Optional validation
            if (o.ValidateMode)
            {
                var ok = await CheckSizeFpsSupportedAsync(o, log, ct);
                if (!ok) log("[rec] Warning: requested size/fps not in -list_options; continuing anyway.");
            }

            var args = BuildArgs(o, _chosen, log); // (logs once)

            var psi = new ProcessStartInfo
            {
                FileName = o.FfmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) log(ev.Data); };
            _proc.ErrorDataReceived  += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) log(ev.Data); };
            _proc.Exited += (_, __) =>
            {
                log("[ffmpeg] exited");
                try { Exited?.Invoke(); } catch { }
            };

            if (!_proc.Start()) throw new Exception("Failed to start ffmpeg.");
            try { _proc.PriorityClass = ProcessPriorityClass.RealTime; } catch { }
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
        }

        public async Task StopAsync(int politeMs = 1500)
        {
            if (_proc == null) return;
            try
            {
                if (!_proc.HasExited)
                {
                    try
                    {
                        if (_proc.StartInfo.RedirectStandardInput && _proc.StandardInput.BaseStream.CanWrite)
                            _proc.StandardInput.WriteLine("q");
                    }
                    catch { /* stdin closed */ }

                    if (!await Task.Run(() => _proc.WaitForExit(politeMs)))
                        _proc.Kill(true);
                }
            }
            catch { /* ignore */ }
            finally
            {
                _proc.Dispose();
                _proc = null;
            }
        }

        // ---- Internals -------------------------------------------------------

        private static async Task<string> GetEncodersAsync(string ffmpegPath, Action<string>? log, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) sb.AppendLine(ev.Data); };
            p.ErrorDataReceived  += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) sb.AppendLine(ev.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct);
            var list = sb.ToString();
            log?.Invoke("[ffmpeg] encoders:");
            foreach (var line in list.Split('\n'))
                if (!string.IsNullOrWhiteSpace(line)) log?.Invoke(line.TrimEnd());
            return list;
        }

        // Try actually encoding a few frames with a candidate encoder
        private static async Task<bool> ProbeEncoderAsync(string ffmpegPath, VEncoder enc, Action<string>? log, CancellationToken ct)
        {
            string encArg = enc switch
            {
                VEncoder.Nvenc => "-c:v h264_nvenc",
                VEncoder.Qsv   => "-c:v h264_qsv",
                VEncoder.Amf   => "-c:v h264_amf",
                _              => "-c:v libx264"
            };

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-hide_banner -loglevel error -f lavfi -i testsrc2=size=128x128:rate=10 -t 0.2 {encArg} -f null -",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            var ok = false;
            try
            {
                p.Start();
                await p.WaitForExitAsync(ct);
                ok = p.ExitCode == 0;
            }
            catch { ok = false; }
            return ok;
        }

        private static string BuildArgs(Options o, VEncoder _ignored, Action<string>? log = null)
        {
            int fps = Math.Max(1, o.Fps ?? 30);
            int width = Math.Max(16, o.Width);
            int height = Math.Max(16, o.Height);
            int gop = Math.Max(2, fps * 2);

            string cam = string.IsNullOrWhiteSpace(o.CamName) ? "default" : o.CamName!;
            string mic = string.IsNullOrWhiteSpace(o.MicName) ? "default" : o.MicName!;
            bool useAudio = !(string.IsNullOrWhiteSpace(o.MicName) ||
                              string.Equals(o.MicName, "(No audio)", StringComparison.OrdinalIgnoreCase));

            cam = cam.Replace("\"", "\\\"");
            mic = mic.Replace("\"", "\\\"");

            string outPath = o.OutputPath;
            try
            {
                if (!outPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                    outPath = System.IO.Path.ChangeExtension(outPath, ".mp4") ?? (o.OutputPath + ".mp4");
            }
            catch { }

            var sb = new StringBuilder();

            // Single combined DirectShow input (smoothest), but DO NOT quantize timestamps to CFR
            sb.Append("-hide_banner -loglevel warning ");
            sb.Append("-fflags +genpts "); // ensure monotonic PTS for live
            sb.Append("-f dshow -rtbufsize 256M -thread_queue_size 4096 -use_wallclock_as_timestamps 1 ");
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "-video_size {0}x{1} -framerate {2} ", width, height, fps);
            if (useAudio)
                sb.AppendFormat("-i video=\"{0}\":audio=\"{1}\" ", cam, mic);
            else
                sb.AppendFormat("-i video=\"{0}\" ", cam);

            // Keep real (VFR) video timestamps → no long-term drift vs audio
            // Minimal filtering: yuv420p for WMP compatibility
            sb.Append("-fps_mode vfr ");
            sb.Append("-vf \"format=yuv420p\" ");

            // Audio: gentle drift correction but KEEP initial relative offset (no first_pts=0)
            if (useAudio)
                sb.Append("-af \"aresample=async=1:osr=48000\" ");

            // Codecs & container
            sb.Append("-c:v libx264 -preset veryfast -crf 20 ");
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "-g {0} ", gop);
            if (useAudio) sb.Append("-c:a aac -b:a 160k ");
            sb.Append("-movflags +faststart -y ");

            sb.AppendFormat("\"{0}\"", outPath);

            string finalArgs = sb.ToString();
            log?.Invoke($"[ffmpeg] ffmpeg.exe {finalArgs}");
            return finalArgs;
        }

    }
}
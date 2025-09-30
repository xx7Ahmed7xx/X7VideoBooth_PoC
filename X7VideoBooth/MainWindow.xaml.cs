using Accord.Video;
using Accord.Video.DirectShow;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using X7VideoBooth;


// Made with 💖 by MrX7 (Eng\ Ahmed Ayman Mansour)
// All rights reserved. 30th September, 2025.
namespace VideoBooth
{
    sealed class FpsPreset
    {
        public string Label { get; init; } = "";
        public int Value { get; init; }
        public override string ToString() => Label;
    }

    // Fast Bitmap -> BitmapSource
    static class BitmapExtensions
    {
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        public static BitmapSource ToBitmapSource(this Bitmap bmp)
        {
            IntPtr hBitmap = bmp.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
            }
            finally { DeleteObject(hBitmap); }
        }
    }

    public partial class MainWindow : Window
    {
        // ---- Simple UI state machine ----
        private enum UiState { Idle, Previewing, Recording, Busy }
        private UiState _state = UiState.Idle;

        private FilterInfoCollection? _videoDevices;
        private FilterInfoCollection? _audioDevices;
        private VideoCaptureDevice? _videoSource;

        private readonly object _lastFrameLock = new();
        private Bitmap? _lastFrame;

        // ---- Resolution presets (bucket ranges) ----
        private sealed class ResPreset
        {
            public string Label { get; init; } = "";
            public int MinW { get; init; }
            public int MinH { get; init; }
            public int MaxW { get; init; }
            public int MaxH { get; init; }
            public override string ToString() => Label;
        }

        private static readonly ResPreset[] Presets =
        {
            new ResPreset{ Label = "4K (3840×2160)",       MinW=3800, MinH=2100, MaxW=4096, MaxH=2304 },
            new ResPreset{ Label = "2K/QHD (2560×1440)",   MinW=2500, MinH=1400, MaxW=2700, MaxH=1520 },
            new ResPreset{ Label = "Full HD (1920×1080)",  MinW=1880, MinH=1050, MaxW=2000, MaxH=1120 },
            new ResPreset{ Label = "HD (1280×720)",        MinW=1240, MinH=700,  MaxW=1300, MaxH=760  },
            new ResPreset{ Label = "SD (640×480)",         MinW=620,  MinH=460,  MaxW=660,  MaxH=520  },
            new ResPreset{ Label = "Best available",       MinW=0,    MinH=0,    MaxW=int.MaxValue, MaxH=int.MaxValue },
        };

        private static readonly FpsPreset[] FpsPresets =
        [
            new FpsPreset{ Label="60 fps", Value=60 },
            new FpsPreset{ Label="30 fps", Value=30 },
            new FpsPreset{ Label="25 fps", Value=25 },
            new FpsPreset{ Label="15 fps", Value=15 },
        ];

        // Selected capture parameters (mirror preview → used for recording)
        private int _selWidth = 1280, _selHeight = 720;
        private int? _selFps = 30;

        private bool _restorePreviewAfterRec;

        // ffmpeg wrapper
        private readonly FfmpegRecorder _rec = new();
        private string _ffmpegPath = "ffmpeg.exe"; // ensure it's in PATH or next to app

        // Recording timer
        private readonly System.Windows.Threading.DispatcherTimer _recordTimer =
            new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Stopwatch _recordStopwatch = new();

        // Duration fields
        private TimeSpan _maxDuration = TimeSpan.FromSeconds(60);
        private bool _maxAutoStopArmed = false;

        // Output fields
        private string lastOutputPath;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += (_, __) => InitializeUi();
            Closed += async (_, __) => await StopPreviewAsync();

            CmbFps.ItemsSource = FpsPresets;
            CmbFps.SelectedIndex = 1;

            _recordTimer.Tick += (_, __) =>
            {
                var t = _recordStopwatch.Elapsed;
                LblRecTime.Text = (t.TotalHours >= 1) ? t.ToString(@"hh\:mm\:ss") : t.ToString(@"mm\:ss");
                // Auto-stop when over max duration
                if (_maxAutoStopArmed && t >= _maxDuration)
                {
                    _maxAutoStopArmed = false;            // disarm to avoid re-entry
                    BtnRecStop_Click(this, new RoutedEventArgs()); // stop recording
                }
            };

            // NEW: react to ffmpeg process exit (crash or normal stop)
            _rec.Exited += OnRecorderExited;

            UpdateUiState();
        }

        private void OnRecorderExited()
        {
            // We're on a process thread; marshal to UI
            Dispatcher.BeginInvoke(() =>
            {
                // Stop timers & reset labels
                _recordTimer.Stop();
                _recordStopwatch.Reset();
                LblRecTime.Text = "00:00";

                // If we had stopped preview for recording, optionally restore it
                if (_restorePreviewAfterRec)
                {
                    _restorePreviewAfterRec = false;
                    // Best-effort restart preview (ignore errors)
                    try { BtnStartPreview_Click(this, new RoutedEventArgs()); } catch { }
                }

                // Return UI to Idle (only if we still think we're recording)
                SetState(UiState.Idle);
            });
        }

        private void InitializeUi()
        {
            // Cameras
            _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            CmbCams.Items.Clear();
            foreach (FilterInfo d in _videoDevices) CmbCams.Items.Add(d.Name);
            if (CmbCams.Items.Count > 0 && CmbCams.SelectedIndex < 0) CmbCams.SelectedIndex = 0;

            // Mics
            _audioDevices = new FilterInfoCollection(FilterCategory.AudioInputDevice);
            CmbMics.Items.Clear();
            CmbMics.Items.Add("(No audio)"); // <— allow video-only recording
            foreach (FilterInfo d in _audioDevices) CmbMics.Items.Add(d.Name);
            if (CmbMics.Items.Count > 0 && CmbMics.SelectedIndex < 0) CmbMics.SelectedIndex = 0;

            // Resolution presets
            CmbRes.ItemsSource = Presets;
            var hdIndex = Array.FindIndex(Presets, p => p.Label.StartsWith("HD"));
            CmbRes.SelectedIndex = hdIndex >= 0 ? hdIndex : Presets.Length - 1;

            UpdateUiState();
        }

        // ---- Small state machine helpers ----
        private void SetState(UiState s)
        {
            _state = s;
            UpdateUiState();
        }

        private void UpdateUiState()
        {
            bool idle = _state == UiState.Idle;
            bool preview = _state == UiState.Previewing;
            bool rec = _state == UiState.Recording;
            bool busy = _state == UiState.Busy;

            BtnStartPreview.IsEnabled = idle && !busy;
            BtnStopPreview.IsEnabled = preview && !busy;
            BtnSnap.IsEnabled = preview && !busy;

            BtnRecStart.IsEnabled = (idle || preview) && !busy;
            BtnRecStop.IsEnabled = rec && !busy;

            CmbCams.IsEnabled = !rec && !busy;
            CmbMics.IsEnabled = !rec && !busy;
            CmbRes.IsEnabled = !rec && !busy;
            CmbFps.IsEnabled = !rec && !busy;

            SetLamp(LampPreview, preview ? Color.OrangeRed : Color.Gray);
            SetLamp(LampRecord, rec ? Color.Red : Color.Gray);

            LblStatus.Text = _state.ToString();
        }

        private static void SetLamp(System.Windows.Shapes.Ellipse lamp, Color color) =>
            lamp.Fill = new System.Windows.Media.SolidColorBrush(color.ToMediaColor());

        

        // ---- Capability picker ----
        private static VideoCapabilities? PickBestCap(VideoCapabilities[] caps, ResPreset preset)
        {
            var filtered = caps.Where(c =>
                c.FrameSize.Width >= preset.MinW &&
                c.FrameSize.Height >= preset.MinH &&
                c.FrameSize.Width <= preset.MaxW &&
                c.FrameSize.Height <= preset.MaxH).ToList();

            var pool = filtered.Count > 0 ? filtered : caps.ToList();

            return pool
                .OrderByDescending(c => c.FrameSize.Width * c.FrameSize.Height)
                .ThenByDescending(c => c.AverageFrameRate)
                .FirstOrDefault();
        }

        // ---- Preview start/stop ----
        private async void BtnStartPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_videoDevices == null || CmbCams.SelectedIndex < 0) { MessageBox.Show("Select a camera."); return; }
            if (_state is not UiState.Idle) return;

            SetState(UiState.Busy);

            try
            {
                // Ensure clean stop
                await StopPreviewAsync();

                var moniker = _videoDevices[CmbCams.SelectedIndex].MonikerString;
                _videoSource = new VideoCaptureDevice(moniker);

                // set resolution/FPS BEFORE Start()
                var caps = _videoSource.VideoCapabilities ?? Array.Empty<VideoCapabilities>();
                var preset = (ResPreset?)CmbRes.SelectedItem ?? Presets.Last();
                var chosen = PickBestCap(caps, preset);
                if (chosen != null)
                {
                    _videoSource.VideoResolution = chosen;

                    // Mirror to recording defaults
                    _selWidth = chosen.FrameSize.Width;
                    _selHeight = chosen.FrameSize.Height;

                    var camFps = chosen.AverageFrameRate > 0 ? chosen.AverageFrameRate : (CmbFps.SelectedItem as FpsPreset)?.Value;
                    _selFps = camFps > 0 ? camFps : null;

                    if (_selFps is int f)
                    {
                        var idx = Array.FindIndex(FpsPresets, p => p.Value == f);
                        if (idx >= 0) CmbFps.SelectedIndex = idx;
                    }
                }

                _videoSource.NewFrame += OnNewFrame;
                _videoSource.Start();

                SetState(UiState.Previewing);
            }
            catch (Exception ex)
            {
                AppendLog("[preview] start failed: " + ex.Message);
                await StopPreviewAsync();
                SetState(UiState.Idle);
            }
        }

        private async void BtnStopPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_state != UiState.Previewing) return;
            await StopPreviewAsync();
            SetState(UiState.Idle);
        }

        private async Task StopPreviewAsync()
        {
            SetState(UiState.Busy);
            try
            {
                if (_videoSource != null)
                {
                    _videoSource.NewFrame -= OnNewFrame;

                    if (_videoSource.IsRunning)
                    {
                        _videoSource.SignalToStop();
                        // Wait off the UI thread to avoid stuck frame
                        await Task.Run(() => _videoSource.WaitForStop());
                    }
                    _videoSource = null;
                }
            }
            catch { /* ignore */ }
            finally
            {
                // clear preview & cached frame (prevents ghost/sticky frame)
                Dispatcher.Invoke(() =>
                {
                    PreviewImage.Source = null;
                    PreviewImage.InvalidateVisual();
                }, System.Windows.Threading.DispatcherPriority.Render);

                lock (_lastFrameLock)
                {
                    _lastFrame?.Dispose();
                    _lastFrame = null;
                }
            }
        }

        private void OnNewFrame(object? s, NewFrameEventArgs ev)
        {
            using var bmp = (Bitmap)ev.Frame.Clone();

            lock (_lastFrameLock)
            {
                _lastFrame?.Dispose();
                _lastFrame = (Bitmap)bmp.Clone();
            }

            var src = bmp.ToBitmapSource();
            src.Freeze();
            // IMPORTANT: don’t block the capture thread
            Dispatcher.BeginInvoke(() => PreviewImage.Source = src);
        }

        // ---- Snapshot ----
        private void BtnSnap_Click(object sender, RoutedEventArgs e)
        {
            if (_state != UiState.Previewing) return;

            Bitmap? snap = null;
            lock (_lastFrameLock)
                snap = _lastFrame != null ? (Bitmap)_lastFrame.Clone() : null;

            if (snap == null) return;

            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "JPEG|*.jpg|PNG|*.png|BMP|*.bmp",
                    FileName = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.jpg"
                };
                if (dlg.ShowDialog() == true)
                {
                    var fmt = System.Drawing.Imaging.ImageFormat.Jpeg;
                    if (dlg.FilterIndex == 2) fmt = System.Drawing.Imaging.ImageFormat.Png;
                    if (dlg.FilterIndex == 3) fmt = System.Drawing.Imaging.ImageFormat.Bmp;
                    snap.Save(dlg.FileName, fmt);
                }
            }
            finally
            {
                snap.Dispose();
            }
        }

        // ---- Recording via FfmpegRecorder ----
        private async void BtnRecStart_Click(object sender, RoutedEventArgs e)
        {
            if (_videoDevices == null || CmbCams.SelectedIndex < 0) { MessageBox.Show("Select a camera."); return; }
            if (_audioDevices == null || CmbMics.SelectedIndex < 0) { MessageBox.Show("Select a microphone (or No audio)."); return; }
            if (_state is UiState.Recording or UiState.Busy) return;

            SetState(UiState.Busy);

            await StopPreviewAsync();

            var camName = _videoDevices[CmbCams.SelectedIndex].Name;
            var micName = CmbMics.SelectedIndex == 0 ? "" : _audioDevices[CmbMics.SelectedIndex - 1].Name;

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "MP4 (H.264 + AAC)|*.mp4|MKV File|*.mkv",
                FileName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4"
            };
            if (dlg.ShowDialog() != true) { SetState(UiState.Idle); return; }

            // prefer UI fps if set; else camera-reported (_selFps)
            var uiFps = (CmbFps.SelectedItem as FpsPreset)?.Value;
            var fpsToUse = (uiFps > 0) ? uiFps : _selFps;

            var opts = new FfmpegRecorder.Options(
                FfmpegPath: _ffmpegPath,
                CamName: camName,
                MicName: micName,
                OutputPath: dlg.FileName,
                Width: _selWidth,
                Height: _selHeight,
                Fps: fpsToUse,
                ValidateMode: false,
                PreferHardware: true,
                UseMjpegOutput: false
            );

            bool previewWasRunning = _videoSource != null && _videoSource.IsRunning;

            async Task<bool> TryStartAsync(bool keepPreviewVisible)
            {
                // if we keep preview, do NOT stop it; otherwise stop it to free the device
                _restorePreviewAfterRec = !keepPreviewVisible && previewWasRunning;
                if (_restorePreviewAfterRec) await StopPreviewAsync();

                try
                {
                    await RunCountdownAsync(3);
                    await _rec.StartAsync(opts, AppendLog);
                    // give ffmpeg a moment to fail if device is busy
                    await Task.Delay(300);
                    lastOutputPath = dlg.FileName;
                    return _rec.IsRunning;
                }
                catch (Exception ex)
                {
                    AppendLog("[rec] start error: " + ex.Message);
                    return false;
                }
            }

            // 1) Try to record while leaving preview on (confidence monitor)
            bool ok = await TryStartAsync(keepPreviewVisible: true);

            // 2) If the driver/cam doesn’t support dual-open, retry without preview
            if (!ok && previewWasRunning)
            {
                AppendLog("[rec] Device may not support dual-open. Retrying without preview…");
                ok = await TryStartAsync(keepPreviewVisible: false);
            }

            if (!ok)
            {
                SetState(UiState.Idle);
                return;
            }

            // success
            _recordStopwatch.Restart();
            _recordTimer.Start();
            SetState(UiState.Recording);
        }

        private async Task RunCountdownAsync(int seconds = 3)
        {
            CountdownOverlay.Visibility = Visibility.Visible;
            for (int n = seconds; n >= 1; n--)
            {
                Countdown.Text = n.ToString();
                await Task.Delay(1000);
            }
            CountdownOverlay.Visibility = Visibility.Collapsed;
        }

        private async void BtnRecStop_Click(object sender, RoutedEventArgs e)
        {
            if (_state != UiState.Recording) return;

            SetState(UiState.Busy);

            try
            {
                await _rec.StopAsync();
                var dlg = new ReviewWindow(lastOutputPath);
                if (dlg.ShowDialog() == true) 
                {
                    /* Keep: maybe open folder or show "Saved!" */
                    var dir = Directory.GetParent(lastOutputPath);
                    Process.Start(dir.FullName);
                }
                else 
                { 
                    try { File.Delete(lastOutputPath); } 
                    catch { } /* Retake */ 
                }
            }
            catch (Exception ex)
            {
                AppendLog("[rec] stop error: " + ex.Message);
            }
            finally
            {
                _maxAutoStopArmed = false;
                _recordTimer.Stop();
                _recordStopwatch.Reset();
                LblRecTime.Text = "00:00";

                // restore preview if we stopped it for recording
                if (_restorePreviewAfterRec)
                {
                    _restorePreviewAfterRec = false;
                    // best-effort restart preview
                    BtnStartPreview_Click(this, new RoutedEventArgs());
                }

                SetState(UiState.Idle);
            }
        }

        // ---- Logging & ffmpeg probing ----
        private void AppendLog(string line)
        {
            Dispatcher.BeginInvoke(() =>
            {
                LogBox.AppendText(line + Environment.NewLine);
                LogBox.ScrollToEnd();
            });
        }

        private void BtnListDevices_Click(object sender, RoutedEventArgs e) => ProbeDevicesWithFfmpeg();

        private void ProbeDevicesWithFfmpeg()
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = "-hide_banner -f dshow -list_devices true -i dummy",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendLog(ev.Data); };
            p.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendLog(ev.Data); };
            try
            {
                AppendLog("[ffmpeg] listing DirectShow devices…");
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                AppendLog("[ffmpeg] list done");
            }
            catch (Exception ex)
            {
                AppendLog("[ffmpeg] list failed: " + ex.Message);
            }
        }

        private void BtnListModes_Click(object sender, RoutedEventArgs e)
        {
            if (_videoDevices == null || CmbCams.SelectedIndex < 0) return;
            ListCameraModesWithFfmpeg(_videoDevices[CmbCams.SelectedIndex].Name);
        }

        private void CmbRes_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var preset = (ResPreset?)CmbRes.SelectedItem ?? Presets.Last();
            if (preset == null) return;
            var startIndex = preset.Label.IndexOf('(') + 1;
            var endIndex = preset.Label.IndexOf('(') - 1;
            var stringRepresentation = preset.Label.Substring(startIndex, preset.Label.Length - startIndex - 1);
            var values = stringRepresentation.Split('×');
            _selHeight = int.Parse(values[1]);
            _selWidth = int.Parse(values[0]);
        }

        private void CmbFps_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _selFps = int.Parse(CmbFps.SelectedValue.ToString().Split(' ')[0]);
        }

        private void ListCameraModesWithFfmpeg(string camName)
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    Arguments = $"-hide_banner -f dshow -list_options true -i video=\"{camName}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            p.OutputDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendLog(ev.Data); };
            p.ErrorDataReceived += (_, ev) => { if (!string.IsNullOrWhiteSpace(ev.Data)) AppendLog(ev.Data); };
            AppendLog($"[ffmpeg] listing modes for {camName}…");
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            AppendLog("[ffmpeg] list done");
        }
    }
}
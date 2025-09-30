X7VideoBooth (PoC)

A lightweight, Windows-only video booth app written in WPF (.NET).
It previews your webcam, records with FFmpeg, and gives you simple booth UX elements (countdown, status lamps, timer, logs, snapshots).

Status: Proof-of-Concept (PoC). The code is intentionally straightforward (no MVVM yet) so other .NET devs can read and tinker quickly.

<!-- (optional) add a real image later -->

✨ Features (today)

Webcam preview with Accord.Video.DirectShow

Start / Stop preview and recording

“No audio” option (video-only) or pick a microphone

Stable FFmpeg pipeline

Combined DirectShow input (video="…":audio="…"),

CFR (e.g., 29.97/30), yuv420p, +faststart, +genpts → good playback & thumbnails

Countdown overlay (3-2-1), status lamps (Preview/Recording), elapsed timer

Snapshot to JPG/PNG/BMP

Device helpers: list devices, probe valid camera modes via FFmpeg

Log pane showing FFmpeg output & app messages

Max duration (auto-stop) support

🧠 Why a PoC?

This repo shows a minimal, dependable Windows recording flow that balances lip-sync and compatibility without pulling in heavy UI frameworks. It’s a great starting point for kiosk/event “video booths,” and an easy codebase to fork.

🧩 Tech & Dependencies

.NET / WPF (Visual Studio 2022 recommended)

Accord.Video.DirectShow for preview

FFmpeg (portable ffmpeg.exe)

Optional: NAudio (future: simple VU meter)

Place ffmpeg.exe beside the app or add it to your PATH.

🚀 Getting Started

Clone

git clone https://github.com/xx7Ahmed7xx/X7VideoBooth_PoC.git


Open in Visual Studio 2022 (or build via dotnet build if targeting .NET 6/7 WPF).

Put ffmpeg.exe next to your built .exe (or install FFmpeg to PATH).

Run the app:

Pick Camera & Mic (or choose (No audio)).

Click Preview → verify you see video.

Click Start Rec → countdown → recording starts.

Stop Rec → file is saved (MP4 by default).

Use Snapshot, List Devices, Valid Modes, and watch the logs.

If you’re using a Logitech C920/C922, 720p often gives higher real FPS in low light.

⚙️ Configuration Highlights

FFmpeg command (core ideas):

DirectShow graph for video+audio (or video-only)

-filter_complex for fps= gate + aresample=async=1

-pix_fmt yuv420p, -movflags +faststart, -fflags +genpts

“No audio”: cleanly omits the audio chain for video-only files.

CFR vs VFR: the app uses CFR by default for compatibility (Windows players, thumbnails) and good lip-sync in practice. (VFR is possible but trades off metadata and some player behaviors.)

🧪 Known Good / Troubleshooting

NVENC not used? The PoC defaults to x264 for wide compatibility. You can add a hardware path later if your GPU/driver supports it.

Properties shows 14–15 fps even when you asked for 30? That’s the camera’s real output when exposure is long or light is low. Increase lighting, disable auto-exposure (set ~1/60), or use 720p.

Stutter or desync:

Prefer CFR 29.97 and aresample=async=1.

Use a dedicated USB port (avoid hubs) and improve lighting.

Try 720p on C920 if 1080p is unstable in your environment.

No thumbnail / WMP oddities: ensure -pix_fmt yuv420p and -movflags +faststart (already in the default pipeline).

🧭 What’s missing for a full “booth” (and planned)

This is a PoC; here are common event-ready features and where they fit in the roadmap:

Review flow (Retake / Keep) ✅ basic dialog ready to add; minimal code provided in issues/PRs welcomed

Autosave & naming template (Event/Date folders; avoid SaveFileDialog in kiosk)

Kiosk mode (borderless fullscreen, TopMost, cursor auto-hide, hotkey to exit)

Pre-flight checks (ffmpeg present, camera/mic found, free disk space)

Audio VU meter (confidence), beep countdown, end chime

Branding overlay / watermark (FFmpeg overlay or post-step)

Consent screen (for public events), simple JSON log of consent

Settings persistence (JSON in %AppData%\X7VideoBooth)

Auto-cleanup (delete files older than N days)

Exposure/white balance controls (DirectShow/IAM interfaces)

Hotkeys (Start/Stop, Snapshot), device hot-plug refresh

Crash-safe logging (rolling logs to file)

PRs: feel free to pick any of the above and open a PR; keep the code approachable and PoC-friendly.

📁 Project Structure (simple by design)
X7VideoBooth_PoC/
  ├─ VideoBooth/
  │   ├─ MainWindow.xaml / .cs        # UI and code-behind (PoC, no MVVM)
  │   ├─ FfmpegRecorder.cs            # Thin wrapper to build & run ffmpeg
  │   └─ (helpers)
  └─ docs/
      └─ screenshot.png               # (add your image)


Intentional: no MVVM yet. PRs that improve structure are welcome, but please keep the entry path simple for new .NET devs reading the code.

🤝 Contributing

Issues and PRs welcome!

Keep PRs small and focused.

If you propose big changes (MVVM, DI, new recording architecture), open an issue first to discuss direction.

📜 License & Trademark

Free & source-available. Non-commercial use only.
Professional/commercial usage requires permission from the author.

The “X7VideoBooth” name, “Video Booth by MrX7”, and any included branding are trademarks of the author. Do not remove trademark notices in the code or UI when redistributing binaries.

If you fork this for learning or personal projects, please keep attribution intact.

Note: The code is public and contributions are welcome, but because it restricts professional/commercial use, it does not match the OSI definition of “open source.” If you need a commercial license, please open an issue or contact the author.

🙏 Credits

FFmpeg — the workhorse encoder/transcoder

Accord.NET — DirectShow capture made approachable

Everyone filing issues and PRs ❤️

FAQ

Why not keep the live preview running during recording?
For reliability. Many webcams/drivers dislike dual-open, and even piping the preview frames to FFmpeg adds CPU/complexity. The PoC shows a stable path: stop the live feed, keep the last frame visible, show lamps/timer, then restore preview after recording.

Can I add NVENC/QSV/AMF hardware encoding?
Yes—add detection and switch -c:v plus per-encoder options. Be sure to keep yuv420p, +genpts, and CFR/VFR choices consistent.

Is MVVM planned?
Open to it. Since this is a PoC, clarity beat architecture for v0. PRs that incrementally move toward MVVM without making the code harder to follow are very welcome.

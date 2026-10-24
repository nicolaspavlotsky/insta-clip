# InstaClip

A tiny screen recorder for Windows that lives in the system tray, made for grabbing gameplay clips without opening OBS.

Press **Alt+A** to start recording. Press it again and the clip is saved, already compressed, to your clips folder. While it records, a small **● REC** badge shows in a corner of the screen. The badge is hidden from the capture, so it never shows up in the clip.

## Download

Grab `InstaClip.exe` from the [Releases](https://github.com/nicolaspavlotsky/insta-clip/releases) page and double-click it. There's nothing to install, and the .NET runtime and FFmpeg are already inside the exe. It goes straight to the system tray. Click the tray icon for the menu.

The first launch takes a few extra seconds while it unpacks FFmpeg to `%LOCALAPPDATA%\InstaClip`.

Requirements: Windows 10 version 2004 or newer, or Windows 11 (64-bit).

## Using it

- **Record:** press **Alt+A** to start and **Alt+A** again to stop and save. You'll hear a short beep for each. The tray icon turns red while recording.
- **After saving:** a notification shows the file name and size. Click it to open the folder, or use **Copy last clip** in the tray menu to paste the clip into Discord, WhatsApp and similar apps.
- **Tray menu settings:**
  - **Save clips to:** the folder clips go to. The default is `Videos\InstaClip`.
  - **Quality:** **High**, **Good** (the default) or **Small**. These use the same compression levels as [Clipper](https://github.com/nicolaspavlotsky/clipper).
  - **Resolution:** keep the monitor's own resolution, or scale down to 1440p, 1080p or 720p for smaller files.
  - **Frame rate:** 60 or 30 fps.
  - **Monitor:** which screen to record.
  - **Audio:** system audio (everything you hear), your microphone, or both mixed together, plus which microphone to use.
  - **Recording indicator:** which corner the badge shows in, or hide it.
  - **Shortcut:** change Alt+A to any other combo, for example Ctrl+Shift+R or a lone F-key.
  - **Sounds** and **Start with Windows.**

Clips are named `InstaClip_2026-10-24_21-15-03.mp4`. While recording, the video goes to a `.recording.mkv` file in the same folder. If the PC crashes mid-recording, that file still plays.

## How it works

The screen is captured on the GPU with Windows' Desktop Duplication API (FFmpeg's `ddagrab`). It's encoded live by your GPU's hardware encoder (NVIDIA NVENC, AMD AMF or Intel Quick Sync), with x264 on the CPU as the fallback. Audio is captured with WASAPI, the same way OBS does it, and kept in sync with the video's clock. Since the encoding happens while you record, saving only takes the few seconds needed to turn the `.mkv` into an `.mp4`.

## Limitations

- Games running in true *exclusive* fullscreen can record black or stutter, and the badge can't draw over them. Borderless or windowed fullscreen, which most modern games use, works fine.
- If the recorded monitor changes resolution mid-recording, or a UAC prompt appears, the recording stops. Whatever was captured up to that point is saved.
- Only monitors connected to the main GPU can be recorded. This matters on some laptops with two GPUs.
- If you switch audio output devices mid-recording (for example by plugging in headphones), the rest of the recording keeps listening to the old device.

## Building

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

The script downloads FFmpeg once into `build/`, then produces the single-file `dist/InstaClip.exe`.

## Third-party

Release builds bundle [FFmpeg](https://ffmpeg.org) (GPL build from [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)), which is licensed under the GPL. Its license text is unpacked next to the FFmpeg binaries. Audio capture uses [NAudio](https://github.com/naudio/NAudio) (MIT).

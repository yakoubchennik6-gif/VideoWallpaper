# Video Wallpaper (VideoWallpaper)

Make any video your animated Windows desktop wallpaper.

A tiny, single-file Windows app (~34 KB) that renders video behind your desktop icons using only what's already built into Windows — no bundled codecs, no extra runtimes, no background services. Designed to be friendly even to weak / integrated-GPU ("toaster") PCs.

## Features

- **Runs behind desktop icons** — video is parented to the wallpaper `WorkerW` layer that Explorer creates for the desktop.
- **No black flash** — the video window is fully transparent until the first real frame arrives, then revealed instantly (opacity switch, not re-creation).
- **Never reaches "media ended"** — a 250 ms watchdog seeks back to the start before the last ~0.4 s of the video, so Windows Media Player never hits its `ended` state (the source of loop black flickers). Native `loop` mode is the primary path; the watchdog is the safety net.
- **≈ Zero cost while locked or asleep** — playback pauses on session lock / logoff and system sleep via Windows events (no polling, no timers).
- **Below-normal process priority** — never competes with games or browsers for CPU.
- **Start with Windows** — optional, starts minimized and re-applies your last video automatically.
- **Panic button: Ctrl+Alt+W** — removes the wallpaper instantly and restores the desktop.
- **Safe rollback** — if applying fails (corrupt file, unsupported format, desktop layer unavailable), the desktop is fully restored automatically.

## Requirements

- Windows 10 or 11
- [.NET Framework 4.x](https://dotnet.microsoft.com/en-us/download) (preinstalled on Windows 10/11)
- Windows Media Player enabled (default)

## Usage

1. Run `VideoWallpaper.exe`
2. Click **Browse…** and pick a video file
3. Click **Apply** — the video is playing behind your icons within seconds
4. (Optional) enable **Start with Windows**

| Control | Action |
|---|---|
| **Browse…** | Pick the video file |
| **✓ Apply** | Apply it as the animated wallpaper |
| **■ Remove Wallpaper** | Stop the video, restore the normal desktop |
| **Start with Windows** | Launch with Windows, minimized; auto-applies the last video |
| **Enable audio** | Play the video with sound (muted by default) |
| **Ctrl+Alt+W** | Global panic hotkey — remove wallpaper instantly |
| Tray icon | Settings, remove wallpaper, or exit |

Closing the window keeps the app in the system tray; use **Exit** from the tray menu to quit completely (the desktop wallpaper is restored).

## Tips for weak / integrated-GPU PCs

- **Resolution:** 480p–720p, not 4K/1440p — much narrower decode path than Full HD.
- **Codec:** H.264 in **MP4** (hardware-decoded on virtually every GPU, including Intel integrated).
- **Frame rate:** 30 fps is plenty for an animated background (20–24 fps is fine too).
- **Bitrate:** aim for ~800 kbps or less — anime videos usually qualify as-is.

## Build from source

Run `build.ps1` (PowerShell). It uses the .NET Framework C# compiler that ships with Windows — no downloads required. Output: `VideoWallpaper.exe` (~34 KB).

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

## How it works

1. Sends `0x052C` with `wParam = 0xD` to `Progman`, which splits the desktop into two `WorkerW` windows (icons view + wallpaper layer).
2. The video window (a Windows Media Player ActiveX, windowless mode) is parented to the wallpaper `WorkerW` behind the icons — icons stay on top, taskbar stays on top of everything.
3. A watchdog reveals the window on the first decoded frame and keeps the media from reaching its `ended` state; cycle restarts around the video's seamless point.
4. When the screen locks or the machine sleeps, playback pauses via `SessionSwitch`/`PowerModeChanged` events — zero background CPU.
5. Desktop state changes (Explorer restart, wallpaper layer re-created) are detected on a 3 s heartbeat that only re-links the window when something actually changed.

## Safety

- The settings window never blocks the emergency path — `Ctrl+Alt+W` works globally.
- On any failure during apply, everything is rolled back: video stopped, window detached, original wallpaper repainted.
- Config is stored in `HKCU\Software\VideoWallpaper` (last video path); startup entry in `HKCU\...\Run\VideoWallpaper`.

## License

[MIT](LICENSE) — see the LICENSE file.

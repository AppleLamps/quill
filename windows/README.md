# quill for Windows

The Windows port of [quill](../README.md): a minimal, fully local meeting
recorder + transcriber. One tray click records your mic and all system audio
as two separate tracks; when you stop, quill transcribes both on-device and
writes a speaker-tagged transcript. Nothing ever leaves the machine except the
one-time whisper model download.

Same skeleton as the macOS build — single binary, tray icon, no installer,
identical session layout on disk — with the platform pieces swapped:

| | macOS | Windows |
|---|---|---|
| system audio | Core Audio process tap | WASAPI loopback (no virtual cable, no driver) |
| mic | AVAudioEngine | WASAPI shared-mode capture |
| container | CAF | WAV (header rewritten once a second, so a killed process still leaves a playable file) |
| transcription | Parakeet via FluidAudio | whisper.cpp via [Whisper.net](https://github.com/sandrohanea/whisper.net) |
| UI | NSStatusItem | NotifyIcon |
| launch at login | LaunchAgent | HKCU `...\CurrentVersion\Run` |

## Install

```powershell
cd windows\src\Quill
dotnet publish -c Release -r win-x64 --self-contained false -o $env:LOCALAPPDATA\Programs\quill
$env:LOCALAPPDATA\Programs\quill\quill.exe install --launch-at-login   # optional
```

Add `...\Programs\quill` to `PATH` if you want `quill` on the command line.
For a build that runs on machines without .NET installed, publish
self-contained instead:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o out
```

**Requires:** Windows 10 1809+ / Windows 11, x64 or arm64, and the
[.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (unless you
publish self-contained). Transcription additionally needs the
[Visual C++ runtime](https://aka.ms/vs/17/release/vc_redist.x64.exe) —
whisper.cpp is a native library. `quill doctor` tells you if it's missing.

## How to use

1. **Run it** (`quill` in a terminal, or let the login entry start it).
2. **Click the feather in the notification area → Start recording.** The icon
   turns red and the tooltip runs an elapsed counter. Windows shows the
   microphone-in-use indicator; no consent prompt appears for loopback, since
   capturing your own playback needs no permission.
3. **Click → Stop recording** when the meeting ends. Transcription starts
   automatically (the menu shows progress); a notification fires when the
   transcript is ready.

Each session lands in `%USERPROFILE%\Recordings\<yyyy.MM.dd-HHmm>\`:

| File | Contents |
|---|---|
| `mic.wav` | your side (default input device, 16-bit mono PCM) |
| `system.wav` | everything Windows played — the other side of the call |
| `meta.json` | start/end timestamps, duration, per-track start offsets |
| `transcript.json` | canonical transcript — engine provenance + timed, speaker-tagged segments |
| `transcript.md` | the same transcript rendered for reading |
| `transcribe.log` | transcription progress/errors for this session |

Two tracks on purpose: speech models do better on clean single-source audio,
and mic-vs-system is free two-party diarization — `me` vs `them` with no
speaker-identification model.

WASAPI delivers nothing while a device is silent, so an idle track would
otherwise compress minutes of quiet into zero bytes and drift out of sync.
Gaps longer than 120 ms are padded with silence, which keeps file position an
honest clock and both tracks aligned with each other.

## Transcription

Built in, on-device, automatic. The engine is **whisper.cpp** (`base.en` by
default) through Whisper.net. The GGML model downloads once into
`%LOCALAPPDATA%\quill\models` and is reused; `quill doctor` tells you whether
it's already cached, so you're never downloading after an important meeting.

Each track is transcribed separately, shifted by its start offset so both
share one clock, and merged by timestamp. Jobs run in a serial queue — you can
start a new recording while the last one transcribes. Unfinished jobs resume
on next launch (the filesystem is the queue: a session with `meta.json` but no
`transcript.json` is pending). Failures append to the session's
`transcribe.log` and never block later jobs.

Swap models in the config: `tiny.en`, `base.en`, `small.en`, `medium.en`,
`large-v3-turbo`, or the multilingual variants without the `.en` suffix
(language is auto-detected for those). Bigger models are slower and more
accurate; `base.en` transcribes far faster than real time on any modern CPU.

## Config

Optional, at `%APPDATA%\quill\config.json` (`~/.config/quill/config.json` is
also honored, for people sharing a dotfiles repo with a Mac):

```json
{
  "recordings_dir": "~/Recordings",
  "transcription": { "enabled": true, "engine": "whisper", "model": "base.en" },
  "mic_use_communications_device": false,
  "on_stop": "my-hook.cmd"
}
```

- `recordings_dir` — where sessions land. Resolution order: `--out` flag >
  config > `%USERPROFILE%\Recordings`.
- `transcription.enabled` — set `false` to just record.
- `transcription.model` — any whisper.cpp GGML model name (see above).
- `mic_use_communications_device` — capture the default *communications*
  device instead of the default multimedia device. Set `true` when your
  meeting app switches only the communications device.
- `on_stop` — command run through `cmd.exe` with the session directory as its
  argument, **after the transcript is written** (or right after recording if
  transcription is disabled). Wire it to whatever comes next: summarization,
  filing, indexing.

## CLI

```powershell
quill                             # run the tray daemon (^C to quit)
quill run --out <dir>             # custom recordings root
quill run --background            # no console window (used by launch-at-login)
quill doctor                      # check devices, recordings folder, model
quill transcribe <session-dir>    # (re)transcribe one session
quill install --launch-at-login
quill install --uninstall
```

## Stack

- **C# / .NET 8** — single `Exe` project, no installer
- **NAudio** — WASAPI shared-mode capture (mic) and WASAPI loopback (system)
- **Whisper.net + whisper.cpp** — on-device transcription
- **Windows Forms NotifyIcon** — the whole UI (feather drawn at runtime, so it
  scales to any DPI and follows the taskbar theme)

## Gotchas

- Loopback records *everything* Windows plays — notification sounds, music,
  all of it. Mute what you don't want in the transcript.
- Loopback follows the **default playback device**. If you switch headphones
  mid-meeting, the tracks keep recording the device the session started with;
  stop and start again to follow the switch.
- If the mic track is silent, check Settings → Privacy & security →
  Microphone → "Let desktop apps access your microphone". `quill doctor`
  reports that toggle.
- Exclusive-mode apps (some DAWs, some games) can lock the mic; quill takes
  shared mode and will fail to start rather than steal the device.
- The `.en` models are English-only. Drop the suffix (`small`, `large-v3-turbo`)
  for other languages.

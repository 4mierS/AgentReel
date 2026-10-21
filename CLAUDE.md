# CLAUDE.md – AgentReel

Context for Claude Code. Read this first, then `README.md` and `NOTICE.md`.
Full plan (architecture, MCP tools, roadmap, licensing, competitors, feature ideas):
https://claude.ai/code/artifact/103a34eb-f925-4e0b-8460-b2f784c05201

## What this is

AgentReel is a hard fork of ScreenToGif (Ms-PL, WPF on .NET 9). Goal: a Windows screen recorder and GIF/video
editor that AI agents (Claude) drive over MCP – record, review frames, trim, caption, redact, export – plus a
modernized, more ergonomic UI.

Positioning (decided 2026-10-20): Windows-native, GIF- and frame-accurate, privacy-first (redaction), and
human-in-the-loop (agent proposes edits, the user approves). Competitor OpenScreen (MIT, Electron) already has an
MCP server since 2026-10-11, so "agent can record" alone is not a differentiator.

Business model (decided): open core. The ScreenToGif-derived code stays open under Ms-PL; agent/pro features
live in separate, newly written assemblies under our own license.

## Hard rules (license)

- Keep every existing copyright and attribution notice from ScreenToGif (Ms-PL 3C). Never strip headers.
- Never use the name "ScreenToGif" or its logo for branding new UI, installers or store listings (Ms-PL 3A).
  Internal project/namespace names may stay `ScreenToGif.*` until a deliberate rename.
- Source derived from ScreenToGif may only be distributed under Ms-PL (3D).
- Do not bundle Gifski (AGPL-3.0) or a GPL FFmpeg build. Keep both as optional user downloads (or LGPL FFmpeg).
- New pro/agent code goes into new projects/files, not into Ms-PL files, so it can be licensed separately.

## Repo facts

- Remotes: `upstream` = https://github.com/NickeManarin/ScreenToGif (based on v2.43.2, commit a4d0a67).
  `origin` = https://github.com/4mierS/AgentReel (private; not a GitHub fork, because forks of public repos
  cannot be private). Never push to `upstream`.
- Branch: `main`.
- Solution: `GifRecorder.sln`. Projects:
  - `ScreenToGif` (WinExe, WPF + WinForms): windows, capture engines, editor, encoding. ~58k lines.
  - `ScreenToGif.Util`: settings, native helpers, named-pipe IPC (`InterProcessChannel/`), CLI args (`Arguments.cs`).
  - `ScreenToGif.ViewModel`: view models, export presets (FFmpeg, Gifski, …).
  - `ScreenToGif.Model` (assembly `ScreenToGif.Domain`): enums, interfaces, DTOs.
  - `ScreenToGif.Native`: Win32 interop.
  - `ScreenToGif.Test`: xUnit, thin coverage.
- Known pain points:
  - `ScreenToGif/Windows/Editor.xaml.cs` is 6,776 lines of code-behind; editing/undo/export hang off WPF commands.
  - `ScreenToGif/Util/EncodingManager.cs` calls `Application.Current.Dispatcher` 14 times → nothing runs headless.
  - `ProjectInfo` / `FrameInfo` live in the UI project (`ScreenToGif/Model/`), not in Domain.
  - Fast capture uses DXGI Desktop Duplication via SharpDX (unmaintained). No Windows.Graphics.Capture yet.
- Existing CLI: `-region`, `-framerate`, `-limit`, `-capture`, `-open`. Existing IPC: `PipeServer`, `InstanceSwitcherChannel`.
- `ScreenToGif.Util/Secret.cs` holds empty placeholder credentials (Imgur, Yandex, feedback mail) – builds fine as is.

## Build and run

Requires Windows 10 2004+ and the .NET 9 SDK.

```powershell
dotnet build GifRecorder.sln
dotnet test ScreenToGif.Test/ScreenToGif.Test.csproj
dotnet run --project Spikes/AgentReel.CaptureSpike -- --seconds 5
dotnet run --project Spikes/AgentReel.CaptureSpike -- --window "Notepad" --seconds 5
```

CI: `.github/workflows/build.yml` (windows-latest, build + test + spike build).

## Current status (2026-10-20)

Done:
- Fork created, README/NOTICE rewritten, upstream Discord workflow and FUNDING removed (commit 48205aa4).
- CI workflow written.
- Capture spike written: `Spikes/AgentReel.CaptureSpike` (console app, free-threaded `Direct3D11CaptureFramePool`,
  no UI thread, per-monitor-v2 DPI manifest, saves first/last frame as PNG to `spike-output/`, prints JSON summary).

Verified locally (2026-10-20, .NET SDK 9.0.319 installed user-local in `%LOCALAPPDATA%\Microsoft\dotnet`,
not on PATH by default):
- `dotnet build GifRecorder.sln`: 0 errors, 82 warnings (all upstream).
- Spike builds without changes. Monitor capture: exit 0, 1920x1080 PNG correct. `--window "Notepad"`: exit 0,
  7 frames, correct PNG.
- Tests: 32/59 pass. The 27 failures are pre-existing upstream issues: ThemeContrastTests for Dark/VeryDark
  (WCAG contrast), YandexUpload (needs credentials/network), ImageComparison (test data b1.bmp/b2.bmp missing).
  CI excludes them via `--filter` (28 tests run, all green).
- Pushed to `origin`; CI green (run 39474397409).
- Characterization tests (`ScreenToGif.Test/Characterization/ExportCharacterizationTests.cs`): 8 synthetic
  frames are exported through the private `EncodingManager.Encode` (via reflection, task id -1 so `Update()`
  never touches the Dispatcher) with every built-in GIF/APNG default preset (embedded, KGySoft, System, APNG).
  Outputs are compared with SHA-256 hashes in `ScreenToGif.Test/Data/Characterization/export-golden.json`.
  To accept new output on purpose: run with `AGENTREEL_UPDATE_GOLDEN=1` and commit the json.
  MP4 (FFmpeg default preset) runs only when `AGENTREEL_FFMPEG` points to ffmpeg.exe; CI downloads a test-only
  GPL build (needed for libx264, never shipped). Note: the MP4 test initializes `UserSettings`, which reads
  `%AppData%\ScreenToGif\Settings.xaml` (never saved by the test).
- Phase 0 checklist in the plan doc updated.

Phase 1 progress (2026-10-20):
- `ScreenToGif.Core` exists (`net9.0-windows7.0`, `UseWPF`). Deviation from the plan: Domain and Util already use
  WPF types, so "headless" means no Dispatcher, no `Application.Current.Windows`, no UI windows – WPF imaging types
  are fine. `LocalizationHelper` and `UserSettings` still need an `Application` with resources; a headless host must
  create one (see the static constructor in `ExportCharacterizationTests`).
- Moved to Core: `FrameInfo`, `ProjectInfo`, `ActionStack`, `ImageMethods`, `QuantizerDescriptor`,
  `DithererDescriptor`. Namespaces unchanged.
- `ScreenToGif.Core.Export.ExportService.EncodeAsync` = the whole encoder part of export, progress via
  `IEncodingReporter`. `EncodingManager` (app) keeps task list, upload, clipboard, custom commands; forwards progress
  with `EncodingReporter`; raises `EncodingChanged`, which `App` marshals to the Dispatcher.
- Characterization tests call `ExportService` directly; outputs byte-identical to before the move.
- Smoke-tested in the real UI (2026-10-20): record + GIF export from the editor works. It first crashed opening the
  export panel: XAML property paths like `(vm:QuantizerDescriptor.X)` resolve types at runtime, so an xmlns without
  `assembly=` still pointed at the app after the move. Fixed with `xmlns:core=...;assembly=ScreenToGif.Core`.
  Lesson: after moving types, grep all `.xaml` for them – the build will not catch it.
- `ScreenToGif.Core.Capture.ICaptureService` (list targets, start into a folder, stop → frames with delays).
  Implementation `AgentReel.Capture.WgcCaptureService` in the new project `AgentReel.Capture`
  (`net9.0-windows10.0.19041.0`, new code, not Ms-PL-derived), built on the spike. Decided 2026-10-20: WGC directly,
  the app's DXGI/GDI recorders stay untouched. Frames are throttled to the framerate; unchanged content extends the
  previous delay. Tests in `AgentReel.Capture.Tests` (also green on GitHub runners, WGC works there).

Phase 2 progress (2026-10-20):
- `AgentReel.Mcp` (stdio, new code, not Ms-PL-derived): tools `list_targets`, `start_recording`, `stop_recording`,
  `get_preview` (text + PNG `ImageContentBlock`), `export` (gif/apng/webp/mp4/webm/mkv/mov). `WpfResources` creates the
  hidden `Application`; `RecordingSession` holds one active recording; `ExportPresets` uses the user's selected preset
  as a shallow copy with upload/clipboard/commands off. Built in CI via the solution.
- Verified end to end over raw JSON-RPC on stdin: 4 s monitor recording (33 frames) → preview PNG correct →
  GIF export valid (`GIF89a`, 5.8 MB). MP4 returns a clean tool error because FFmpeg is missing locally.
- The exe is framework-dependent. With the user-local SDK, every launcher (Claude Desktop/Code config) must set
  `DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet`, otherwise it fails with "You must install .NET".

- MP4 over stdio verified (2026-10-21) with a local, never-shipped FFmpeg via `AGENTREEL_FFMPEG`. Two fixes on the way:
  - FFmpeg inherited the host's stdin (= the JSON-RPC pipe), so exports hung until the client closed stdin.
    `ExportService` now gives every FFmpeg process its own stdin and closes it right away.
  - With passthrough/vfr, a long-held last frame (idle screen at the end) makes the MP4 header report a shorter
    duration than what plays (x264 B-frames leave the last DTS behind its PTS). The MCP server forces CFR for video
    presets in normal mode. The app's own default MP4 preset (passthrough) still has this bug – not fixed there yet.
- Registered as `agentreel` in Claude Code (user scope) and Claude Desktop (Store build, config under
  `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\`), both with `DOTNET_ROOT` and `AGENTREEL_FFMPEG`.
  A running client locks the Debug exe; stop it before rebuilding `AgentReel.Mcp`.

- **Phase 2 gate passed (2026-10-21)** in a fresh Claude Code session over the registered `agentreel` server, without
  manual steps: Claude opened a scripted demo console window, recorded it by window title for 22 s
  (`maxSeconds` auto-stop, 76 frames), checked a frame with `get_preview`, exported MP4. ffprobe: h264 1114x628 CFR
  25 fps, 22.08 s (recorded 21.98 s). It recorded a window, not the monitor, because the primary monitor showed private
  mail. Odd window widths (1115) are rounded down to even for x264.
- `stop_recording` after an auto-stop (`maxSeconds`) now returns that recording with `autoStopped: true` instead of
  "No recording is running" (once; a new `start_recording` clears it). Verified over stdio with a scratch build.
  The registered Debug exe is not rebuilt yet (locked by the running client).
- Decided 2026-10-21: the passthrough MP4 duration bug in the app's default preset stays for now. The agent path is
  fixed (CFR), changing `FfmpegMp4Preset.Defaults` would not fix presets users already saved, and it touches Ms-PL
  upstream behavior. Revisit in Phase 5 when the export UI is reworked (or report upstream).

Phase 4 progress (2026-10-21):
- Edits never change their input: `RecordingSession.DeriveAsync` saves the result as a new project
  `<original id> edit <n>` (own copy of the images, `sourceId` in the result) and makes it the latest recording.
  This is the base for review-before-apply: the user compares original and edit and keeps one.
- `trim` (frame accurate: frames cut by the range keep only the part inside, so the result lasts exactly
  endMs - startMs) and `get_contact_sheet` (evenly spaced frames as one labeled PNG grid + text list; rendered on an
  STA thread because WPF visuals need one). Logic in `AgentReel.Mcp/Editing/` (`Timeline`, `ContactSheet`), tools in
  `EditTools.cs`. Tests in `AgentReel.Mcp.Tests` (in CI).
- Verified over stdio with a scratch build: window recording → contact sheet → trim 1000–4000 ms (3000 ms) →
  trim of the edit (`edit 2`) → GIF export.
- `optimize`: `Timeline.Fold` folds exact repeats and A-B-A flickers (middle frame ≤ `maxFlickerMs`, e.g. a blinking
  caret) into the earlier frame, then `Timeline.Retime` applies `speed` and caps holds at `maxPauseMs`. Pixel compare in
  `FramePixels` (exact, small decode cache). Rejected: the editor's percent similarity (`ImageMethods.CalculateDifference`)
  – a new console line changes < 0.5 % of the pixels, so 99.5 % folded a whole 44-frame recording into one frame.
  Verified over stdio: a 4 s "thinking" pause with a blinking caret became one 1.5 s hold, all output lines kept.
- `add_caption` (text in a rounded box, top/center/bottom, wraps) and `add_highlight` (rectangle/ellipse outline in
  recording pixels, optional `dimOutside`) for a time range. `Timeline.Isolate` splits frames cut by the range so the
  overlay starts and ends exactly there; `Overlay` draws on the copied images inside `DeriveAsync` (new `change` hook;
  a failed edit deletes its folder). STA helper shared as `Editing/Sta.cs`. Verified over stdio: caption 1000–5500 ms +
  highlight 2000–6000 ms on a window recording, duration unchanged, GIF export.
- `open_in_editor` (`AppTools.cs`) starts the app (`AGENTREEL_APP`, else `ScreenToGif.exe` next to the server, copied by
  the project reference) with the recording's `Project.json`, via shell execute so the app does not inherit the MCP
  stdio pipes. App changes (Ms-PL files, kept small): `Editor.LoadFromArguments` opens a passed `Project.json` like
  "Recent projects" (`LoadProjectFile`); `ApplicationViewModel.Open` case 4 passes the files on, so an already open
  empty editor loads them too (with "single instance" on, args arrive over the existing `InstanceSwitcherChannel` pipe).
  Verified over stdio: caption edit → `open_in_editor` → the editor shows the edit with all 24 frames. Single-instance
  path verified too (local `Settings.xaml` with `SingleInstance=True` next to a scratch build, so the user's AppData
  settings stay untouched): one app process, the second project arrived over the pipe in a second editor window.
  Known gap: edits the user makes in the editor are not seen by the server.
- `list_recordings` (all projects with frames in the recording folder, newest first, `openInEditor` via the app's
  global mutex `MutexList.IsInUse`) and `delete_recording` (`Destructive`, no default id, refused while running or open
  in the editor). `RecordingSession.Get` loads unknown ids from disk (`Project.json`), so recordings of earlier runs and
  of the app work with every tool. Ids must be plain folder names (no `..`, no separators). Verified over stdio across
  two server runs: load from disk → trim → delete all.
- Fixed (found by dogfooding over the registered server, 2026-10-21): `ProjectInfo.CreateProjectFolder` takes the
  project's "open in an editor" mutex, and the server never released it. So every recording of the running server showed
  `openInEditor: true`, `delete_recording` refused it, and the app's "Recent projects" refused to open it. The server now
  holds the mutex only while recording (keeps the app's cache cleanup away) and releases it on stop
  (`RecordingSessionTests`). Dogfood run: window recording 25 s → `optimize` (speed 2, 25.0 → 10.7 s) → `add_caption`
  → `add_highlight` with `dimOutside` → GIF export, all correct.

- `get_status` (running?, elapsed, frames so far, time left until `maxSeconds`, latest and auto-stopped id) and live
  `get_preview` (2026-10-21, decided with Amir): while a recording runs and `recordingId` is empty or its id, it returns
  the newest frame whose PNG is complete (`ICaptureService.GetProgress`; WGC counts written frames, they are written in
  order). `frame`/`atMs` are refused while live. Verified over stdio with a scratch build on a window recording: live
  frames at 3 s and 6 s showed the right console lines, stop/preview/delete afterwards fine.

Next – START HERE:
1. Phase 3 (Vortice instead of SharpDX for the app's capture).

## Roadmap

- Phase 0 – fork and safety net (~1 week): build, CI, capture spike, characterization tests
  (export a sample project to GIF and MP4 and compare outputs) before any refactoring.
- Phase 1 – extract core (~2 weeks): new `ScreenToGif.Core` (`net9.0-windows10.0.19041.0`, no `UseWPF`);
  move `ProjectInfo`, `FrameInfo`, `ActionStack` there; replace Dispatcher calls in `EncodingManager` with
  `IProgress<T>`/events; put capture engines behind `ICaptureService`.
- Phase 2 – MCP server MVP (~1 week): new console project `AgentReel.Mcp` (stdio) using packages
  `ModelContextProtocol` + `Microsoft.Extensions.Hosting`; tools `list_targets`, `start_recording`,
  `stop_recording`, `get_preview` (returns `ImageContentBlock` PNG), `export`. Logs to stderr only.
  Register in Claude Desktop config and test end to end with Computer Use.
  **Gate:** Claude records a 20-second demo on its own and exports MP4.
- Phase 3 – modern capture (~2 weeks): Windows.Graphics.Capture for windows and monitors; replace SharpDX with Vortice.Windows.
- Phase 4 – editing over MCP (1–2 weeks): `trim`, `add_caption`, `add_highlight`, `optimize` (speed, dedupe),
  `get_contact_sheet`, `open_in_editor` (via existing named pipe).
- Phase 5 – UI (4–6 weeks, ongoing): WPF UI (lepoco/wpfui, `ui:ThemesDictionary` + `ui:ControlsDictionary`,
  `ui:FluentWindow`), one window from record to export, command palette, timeline; split `Editor.xaml.cs` into view models.

MCP server skeleton (from the official C# SDK docs):

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace); // keep stdout for MCP
builder.Services.AddSingleton<IRecordingService, RecordingService>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();
```

## Feature priorities (from issues, user reviews, competitors)

MVP: auto-shorten agent "thinking" pauses (reuse duplicate-frame detection), redaction tool + auto-detect
password fields/emails, review-before-apply for agent edits, chapter markers during recording (`add_marker`),
robust FFmpeg download (upstream issues #1469, #1460, #1458, #1451).
Later: auto-zoom on clicks, click/key name overlay (#1476), agent-readable export (Markdown with steps,
timestamps, frames), bug-report mode, HDR + multi-monitor fixes (#1452, #840), TTS narration, scrolling capture.

Idea (2026-10-21, Amir): transcript-based editing + AI voiceover, likely a pro feature. Stages, each useful alone:
1. Record audio (mic + system via WASAPI, shared clock with WGC). Prerequisite: ScreenToGif records no audio today.
2. Transcribe locally (Whisper via whisper.cpp / Whisper.net, MIT) with word timestamps → subtitles
   (SRT/VTT, MP4 track, or burned in for GIF).
3. Edit subtitles as text (changes subtitles only).
4. Remove filler words ("ähm"), slips and long pauses: cut audio + video together via word timestamps
   (same idea as shortening thinking pauses).
5. Claude rewrites the script → original audio no longer matches, so this needs step 6.
6. TTS speaks the rewritten script per segment; each video segment is retimed to the new speech length (hold or
   shorten still frames, speed up motion) – the frame + delay model fits this well.
7. Translate: script per language, TTS per language, video retimed per language.
Every change is proposed as a text diff and approved by the user (review-before-apply). Open: TTS engine (Windows
voices, Piper – check the current version's license, or cloud like ElevenLabs/Azure), voice cloning of the user's own
voice (consent, label AI voice, EU AI Act Art. 50).

Todos (transcript editing + AI voiceover):
- [ ] Decide: audio before or after the MVP, and whether this is a pro feature.
- [ ] Audio capture: mic + system via WASAPI in `AgentReel.Capture`, shared clock with WGC, mux audio into MP4 export.
- [ ] MCP: `start_recording` option `audio` (mic/system/both).
- [ ] Local transcription with Whisper.net, word timestamps; model as optional download (never bundled).
- [ ] Subtitle export: SRT/VTT, MP4 subtitle track, burned in for GIF.
- [ ] MCP tools `transcribe` and `edit_subtitles`.
- [ ] Detect filler words and long pauses, propose cuts as a diff, cut audio + video together (`remove_fillers`).
- [ ] Script rewrite by Claude as a text diff with approval (`rewrite_script`).
- [ ] Pick a TTS engine and check its license (Windows voices, Piper, ElevenLabs/Azure).
- [ ] Retime each video segment to the new speech length (adjust frame delays).
- [ ] Translation: script, TTS and retiming per language, export per language.
- [ ] Voice cloning: consent, label as AI voice (EU AI Act Art. 50).

## Open decisions

- Trademark/domain check for "AgentReel" (DPMA, EUIPO, .com, .app).
- Whether to expose audio/webcam to the agent before or after the MVP.

## Conventions

- Code, comments, commit messages and docs in English. Talk to Amir in German.
- Small commits; never commit `spike-output/`, secrets or downloaded encoders.
- Keep upstream mergeable where cheap: prefer new files/projects over editing large upstream files until Phase 1.

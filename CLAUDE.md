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

Next – START HERE:
1. Report results back into the plan doc (Roadmap → Phase 0 checklist).
2. Characterization tests: export a sample project to GIF and MP4 and compare outputs.

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

## Open decisions

- Trademark/domain check for "AgentReel" (DPMA, EUIPO, .com, .app).
- Whether to expose audio/webcam to the agent before or after the MVP.

## Conventions

- Code, comments, commit messages and docs in English. Talk to Amir in German.
- Small commits; never commit `spike-output/`, secrets or downloaded encoders.
- Keep upstream mergeable where cheap: prefer new files/projects over editing large upstream files until Phase 1.

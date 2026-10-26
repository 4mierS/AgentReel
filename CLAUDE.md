# CLAUDE.md – Glasstake (repo still named AgentReel)

Context for Claude Code. Read this first, then `README.md` and `NOTICE.md`.
Product and business doc (German, Claude Docs): https://claude.ai/artifact/A8M4ciJ2qmzNrz7EgvBQpo
Older technical plan (from the fork days, partly outdated): https://claude.ai/code/artifact/103a34eb-f925-4e0b-8460-b2f784c05201

## What this is

A Windows app that makes **evergreen how-to videos for native Windows software**: a shot list anchored to UI
Automation (picture fallback) plays the app and films it; edits (captions, highlights, cuts, redactions) are anchored
to steps and survive a re-shoot of the next build (CLI/CI); every change an agent proposes is approved by the user in
Studio (review-before-apply). Director mode: one sentence → the agent drafts a shot list → the user approves → film →
edit → review. Claude drives it over MCP; Studio is also a full manual editor (every MCP edit has a manual control).
Privacy Shield (local OCR + secret detectors, redaction) is built-in safety, not the headline.

Web tools already do re-shot demos (DemoBites Retake, Velo, Redemo, Vidocu); none found for native Windows.
Free rivals on Windows have auto-zoom and agent PR demos (Recordly, OpenScreen, Cap), so those are not the selling point.
Pricing (decided): free basics; individual $7/month (or $70/year, AI features); Business ~$49–99 per app/month; Team later.

History: started 2026-10-20 as a hard fork of ScreenToGif (Ms-PL). Decided 2026-10-21 to become an independent app
with no ScreenToGif code. Since package 4a (2026-10-22) the repo contains no ScreenToGif project anymore; all
`AgentReel.*` code is new (clean room). The git history still contains the Ms-PL code. Renamed AgentReel → Glasstake
(working name, attorney clearance pending) on 2026-10-26; the GitHub repo and the checkout folder keep the old name.

## Hard rules

- Clean room: never read, translate or copy ScreenToGif source (also not from `upstream` or the git history).
  New implementations are written from specs/behavior.
- Never use the name "ScreenToGif" or its logo for branding. Never push to `upstream`.
- Keep `LICENSE.txt`/`NOTICE.md` as they are until the fresh repo (step 5 of the independence plan); suggest a
  lawyer check before relicensing.
- Do not bundle Gifski (AGPL) or a GPL FFmpeg build. Export needs neither (own GIF/APNG encoders, MP4 via Media
  Foundation).
- Never buy domains or file trademarks; Amir decides (attorney clearance for "Glasstake" pending).

## Repo facts

- `origin` = https://github.com/4mierS/AgentReel (private). `upstream` = ScreenToGif (never push). Branch `main`.
  Use `gh -R 4mierS/AgentReel`.
- Solution `Glasstake.sln` (Debug/Release, Any CPU only).
  Projects target `net9.0-windows10.0.19041.0` (`Glasstake.Core`: plain `net9.0`). `Directory.Build.props` sets Glasstake metadata
  (Company/Product "Glasstake", "Copyright © 2026 Amir", 0.1.0) and `CETCompat=false` (open question, see below).
- Projects:
  - `Glasstake.Core`: `Recording`/`RecordedFrame`, `recording.json`, `RecordingStore`
    (`%LOCALAPPDATA%\Glasstake\Recordings`, env `GLASSTAKE_RECORDINGS`), `AppSettings`
    (`%APPDATA%\Glasstake\settings.json`), `DataFolders` (moves the old `AgentReel` data folders to `Glasstake` on
    first use, or keeps using them if a file is open; `DataFolders.Variable` falls back to old `AGENTREEL_*` env
    variables), `RecordingLock`, `LegacyImport` (copies old `%TEMP%\ScreenToGif\Recording`
    projects once).
  - `Glasstake.Capture`: Windows.Graphics.Capture recorder (`ICaptureService`, `WgcCaptureService`,
    `ProjectRecording`). Frames throttled to the framerate; unchanged content extends the previous delay.
  - `Glasstake.Export`: own GIF and APNG encoders, MP4 via Media Foundation (Vortice.MediaFoundation).
  - `Glasstake.Editing`: timeline edits (`Timeline`: trim, fold, retime, isolate), overlays, contact sheet, edit chain,
    `Edit.json`/`Review.json`, `RecordingLibrary`, `RecordingExport`, `EditDocument`/`Clips` (Studio editor model),
    `Privacy/` (OCR, detectors, redaction), `Shots/` (`ShotFilm`, `AnchoredEdits`, `TakeClock`, `PreviousTake`).
  - `Glasstake.Direct`: shot lists (`%LOCALAPPDATA%\Glasstake\ShotLists`, env `GLASSTAKE_SHOTLISTS`), locator matcher
    (found/moved/changed/missing), `ShotPlayer` (UIA patterns, guarded SendInput fallback), `TemplateMatch`/`Vision`
    (picture fallback), `InputWatch` (warns about real input during a run), drift report (json/html/png),
    `ShotDraft` (director drafts + live locator check).
  - `Glasstake.Mcp`: stdio MCP server (tools below).
  - `Glasstake.Studio`: WPF UI 4.3.0 app (Mica, system theme). No args → session window + recording bar;
    `review <folder>`; `edit <folder>`; `approve-shots <shot list folder>`. English and German: all texts in
    `Localization/Strings.resx` + `Strings.de.resx` (XAML uses `{x:Static l:Strings.Key}`, code `Strings.Key` and
    `Text.Format`/`Text.Count`); language = env `GLASSTAKE_LANGUAGE`, else `language` in settings.json, else the Windows
    display language, else English. Files the agent reads (`Edit.json` steps, `edits.json`, draft check notes) stay
    English; `StepText.Describe(step, culture)` (texts in `Glasstake.Direct/StepStrings*.resx`) is English unless
    Studio passes a culture.
  - `Glasstake.Cli` (`glasstake.exe`): `baseline|drift|film|reshoot <shot list folder>`; exit code 2 = needs a human.
  - Samples: `Glasstake.Direct.SampleApp` (WPF; `--v2` moves Export, renames Customer→Client, removes Refresh),
    `Glasstake.Direct.SampleApp.WinForms`, `samples/electron`, `samples/shotlists/invoice-export` (used by CI).
  - Tests: `Glasstake.Capture.Tests`, `Glasstake.Export.Tests`, `Glasstake.Mcp.Tests`, `Glasstake.Direct.Tests`
    (235 tests on 2026-10-26; `StudioTextTests` in Mcp.Tests checks that both languages have the same keys and
    placeholders).
- MCP tools: recording (`list_targets`, `start_recording`, `stop_recording`, `get_status`, `get_preview` incl. live,
  `list_recordings`, `delete_recording`), editing (`trim`, `optimize`, `add_caption`, `add_highlight`,
  `get_contact_sheet`, `scan_privacy`, `redact`), review (`request_review`, `get_review`, `open_in_studio`), export
  (`export`: gif/apng/mp4, with `privacyWarning`), direct (`inspect_app`, `save_shot_list`, `list_shot_lists`,
  `run_shot_list`, `check_drift`, `film_shot_list`, `reshoot`), director (`draft_shot_list`, `get_shot_approval`,
  `add_step_captions`, prompt `direct_video`).
- Edits never change their input: each edit is a new recording `<id> edit <n>` joined to its chain (`Edit.json`), so a
  review always compares with the original take.

## Build and run

Requires Windows 10 2004+ and the .NET 9 SDK. The SDK is user-local in `%LOCALAPPDATA%\Microsoft\dotnet` (not on
PATH by default): set PATH and `DOTNET_ROOT` to it. Build with `-c Release` for test runs.

```powershell
dotnet build Glasstake.sln -c Release
dotnet test Glasstake.Mcp.Tests -c Release --no-build   # likewise Capture/Export/Direct.Tests
```

- **Desktop lock**: before any UIA, recording or Direct test run, create `%TEMP%\glasstake-desktop.lock` (owner and
  time inside) and delete it afterwards; if it exists, wait. Parallel desktop runs break tests.
- Never record the primary monitor (private mail). No SendKeys. Don't delete Amir's recordings or the demo takes
  `2026-10-22 16-05-39` and `2026-10-22 16-05-51`. For end-to-end runs, point `GLASSTAKE_RECORDINGS` and
  `GLASSTAKE_SHOTLISTS` to a scratch folder and `GLASSTAKE_STUDIO` to the built Studio.
- End-to-end checks: drive `Glasstake.Mcp.exe` over raw JSON-RPC on stdin (initialize with clientInfo `claude-code`),
  click Studio buttons through UI Automation (`InvokePattern`, buttons are named by `x:Name`).
- The registered server is the Debug build of `Glasstake.Mcp` in this checkout, registered as `glasstake` in Claude
  Code (user scope) and Claude Desktop (Store build, config under
  `%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude\`), both with `DOTNET_ROOT`. A running client locks the
  exe: stop the `Glasstake.Mcp` processes under `Glasstake.Mcp\bin\Debug\`, rebuild, then Amir reconnects (`/mcp`)
  and restarts Claude Desktop.
- CI: `.github/workflows/build.yml` (windows-latest, build + all four test projects; the hosted runner has an
  interactive desktop, so UIA and WGC work). `.github/workflows/reshoot.yml` (workflow_dispatch): baseline v1 →
  drift against `--v2` → reshoot with anchored edits → uploads report and MP4s. First run 39839847094 (2026-10-22)
  green: drift 3 unchanged/1 moved/1 changed/1 missing, 3 of 4 edits carried over, real pixels in the crops.

## Status (2026-10-26)

Done (all merged and pushed):
- MCP server, capture, own project model and storage, own export (no FFmpeg), Studio review window, session window,
  pro editor, Privacy Shield, Direct (shot lists, drift, film, reshoot with anchored edits).
- Package 4a: all ScreenToGif projects, `ScreenToGif.Test`, characterization tests, legacy CI steps and packages
  removed.
- Package 1, step anchors in Studio (`70bde318`): step track in the editor; hand-made captions, highlights and
  redactions (R tool) anchor to steps and save to `edits.json`; "follow element" for highlights/redactions; review shows
  a one-line headline plus a list, hold P for the previous take. Checked via UIA only; real mouse/keyboard gestures are
  untested.
- Package 3, robustness (`befeff9b`): WinForms and Electron samples (no FlaUI needed), picture fallback, input
  warning, `Glasstake.Cli` and the reshoot workflow (green on GitHub). Electron: empty UIA tree in about 1 of 6 runs,
  accepted for now.
- Package 2, director mode (`1e7044a4`): `draft_shot_list` checks the draft against the running app (found,
  ambiguous, in a later dialog, not found) and opens Studio's approval window (goal as headline, steps in plain words,
  editable captions, approve/reject/feedback); `get_shot_approval` saves the approved list once with the user's
  captions; `add_step_captions` burns one caption per step (holds until the next captioned step), anchored to steps.
  Verified over stdio on the WPF sample: draft → approve (UIA) → film (60 frames, 5.5 s) → optimize → step captions →
  request_review showed "CLAUDE SUGGESTS" with 4 caption changes against the take.
- Package 4b, Studio in English and German (2026-10-23): 264 texts in resx, German in the informal "du"; the agent's
  name in German cases (`AgentName`); step texts in German as labels ("Klicken: Schaltfläche „OK“"); dates and
  numbers follow the regional format. Checked by screenshots of editor, review and approval windows in both languages.
  No app icon yet (waits for the Glasstake logo).
- Package 5, rename AgentReel → Glasstake (2026-10-26): projects, namespaces, `Glasstake.sln` (Debug/Release, Any
  CPU), exes `Glasstake.Mcp.exe`/`Glasstake.Studio.exe`/`glasstake.exe`, MCP server name `glasstake`, env
  `GLASSTAKE_*` (old `AGENTREEL_*` still read), data folders `%LOCALAPPDATA%`/`%APPDATA%\Glasstake` (old folders
  moved on first use), exports to `Videos\Glasstake`. README and NOTICE untouched (rewritten with the fresh repo).

Known limits: real keyboard input during a run lands in the app (re-shoot in a VM/CI); a step whose element is in a
dialog shows "not on screen" in the draft unless its target names the window; Studio's mouse gestures and the F8
hotkey were never tested by Amir.

## Next – START HERE

1. Fresh repo with a clean history, own LICENSE, NOTICE only for third-party libraries (WPF UI, Vortice, …);
   README rewritten (today's README/NOTICE still describe the fork).
2. Localization leftovers: a language picker in Studio (today only settings.json/env); German texts for the anchored
   edits list and edit steps would need Studio-side wording (the files stay English).

Open questions for Amir:
- Package 1: anchor new overlays to a step by default or opt-in? Partial-step cuts anchorable or only whole steps?
  "Previous take" = newest kept version or last export (exports are not recorded)? Own label for reshoot versions
  instead of "Claude"?
- `CETCompat`: keep false or switch on (the .NET default)?
- Glasstake: attorney clearance and domains.

## Lessons that still apply

- WPF visuals need an STA thread (`Editing/Sta.cs`); a headless host creates its own hidden `Application`.
- WPF compiles XAML in a temporary project before resources are generated, so a resx accessor made by the usual
  `StronglyTyped*` metadata is missing there. Studio's csproj generates `Strings` in its own target before
  `MarkupCompilePass1`.
- Child processes started by the MCP server must not inherit its stdio (use shell execute or own stdin), or the
  JSON-RPC pipe hangs.
- MP4: odd widths are rounded down to even; a long-held last frame needs CFR to keep the reported duration right.
- Pixel compare for folding must be exact (`FramePixels`): percent similarity folded a whole console recording into
  one frame because a new line changes < 0.5 % of the pixels.
- Border does not clip its content (round buttons in the recording bar).
- `git worktree remove` can hit "permission denied": run `dotnet build-server shutdown`, then `--force`, prune, and
  remove the folder.

## Feature ideas

MVP leftovers: chapter markers during recording (`add_marker`), auto-detect password fields.
Later: auto-zoom on clicks, click/key name overlay, agent-readable export (Markdown with steps, timestamps, frames),
bug-report mode, HDR + multi-monitor, scrolling capture, webcam/board recorders (built new), Microsoft Store.

Transcript editing + AI voiceover (Amir, 2026-10-21; likely pro). Stages, each useful alone:
1. Record audio (mic + system via WASAPI, shared clock with WGC).
2. Transcribe locally (Whisper.net, MIT) with word timestamps → subtitles (SRT/VTT, MP4 track, burned in for GIF).
3. Edit subtitles as text.
4. Remove filler words, slips and long pauses: cut audio + video together via word timestamps.
5. Claude rewrites the script → needs step 6.
6. TTS per segment; each video segment retimed to the new speech length (frame + delay model fits).
7. Translate: script, TTS and retiming per language.
Every change is a text diff approved by the user. Open: TTS engine and license (Windows voices, Piper, ElevenLabs/Azure),
voice cloning (consent, label AI voice, EU AI Act Art. 50), audio before or after the MVP.

## Conventions

- Code, comments, commit messages and docs in English. Talk to Amir in German.
- Small commits; `git add` explicit paths only, never `.claude/`. End commits with
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never commit secrets or downloaded encoders.
- Bigger packages go on a worktree branch, merged with `--no-ff` after all tests pass in Release under the desktop lock.

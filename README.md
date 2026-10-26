# AgentReel

AgentReel is a Windows screen recorder and GIF/video editor that AI agents such as Claude can drive over the
Model Context Protocol (MCP): record a window or monitor, review frames, trim, caption, redact and export.

> **Status:** in development, not released yet. Working today: the MCP server (record, preview, trim, optimize,
> caption, highlight, export, review-before-apply) and AgentReel Studio (record, review).

## Origin and license

AgentReel is a fork of [ScreenToGif](https://github.com/NickeManarin/ScreenToGif) by Nicke Manarin,
licensed under the Microsoft Public License (Ms-PL). See [NOTICE.md](NOTICE.md) and [LICENSE.txt](LICENSE.txt).
AgentReel is not affiliated with or endorsed by the ScreenToGif project. The original README is kept in
[README.upstream.md](README.upstream.md).

## Build

Requirements: Windows 10 2004 (build 19041) or newer, .NET 9 SDK.

```powershell
dotnet build Glasstake.sln -c Debug
dotnet test Glasstake.sln -c Debug
```

## Projects

- `AgentReel.Studio`: the new desktop app (record, edit, review).
- `AgentReel.Mcp`: the MCP server (stdio) that agents use.
- `AgentReel.Editing`: edits, overlays, edit/review files and export presets, shared by both.
- `AgentReel.Capture`: Windows.Graphics.Capture recorder.
- `AgentReel.Core`: recording model, storage folders, settings and locks.
- `AgentReel.Export`: GIF and APNG encoders, MP4 via Media Foundation.
- `AgentReel.Direct`: shot lists driven through UI Automation, drift reports (plus a sample app for tests).

# AgentReel

AgentReel is a Windows screen recorder and GIF/video editor that AI agents such as Claude can drive over the
Model Context Protocol (MCP): record a window or monitor, review frames, trim, caption, redact and export.

> **Status:** early fork, phase 0 (build, CI, capture spike). Not ready for use yet.

## Origin and license

AgentReel is a fork of [ScreenToGif](https://github.com/NickeManarin/ScreenToGif) by Nicke Manarin,
licensed under the Microsoft Public License (Ms-PL). See [NOTICE.md](NOTICE.md) and [LICENSE.txt](LICENSE.txt).
AgentReel is not affiliated with or endorsed by the ScreenToGif project. The original README is kept in
[README.upstream.md](README.upstream.md).

## Build

Requirements: Windows 10 2004 (build 19041) or newer, .NET 9 SDK.

```powershell
dotnet build GifRecorder.sln -c Debug
dotnet test ScreenToGif.Test/ScreenToGif.Test.csproj
```

## Capture spike

`Spikes/AgentReel.CaptureSpike` checks that Windows.Graphics.Capture works headless from a console app,
which is what the MCP server will need.

```powershell
dotnet run --project Spikes/AgentReel.CaptureSpike -- --seconds 5
dotnet run --project Spikes/AgentReel.CaptureSpike -- --window "Notepad" --seconds 5
```

It prints a JSON summary (frames, fps, size) and writes the first and last frame as PNG to `spike-output/`.

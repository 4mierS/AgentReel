using ScreenToGif.Domain.Enums;
using ScreenToGif.Util.Settings;
using ScreenToGif.ViewModel.ExportPresets;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Apng;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Gif;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Webp;
using ScreenToGif.ViewModel.ExportPresets.Video;
using ScreenToGif.ViewModel.ExportPresets.Video.Mkv;
using ScreenToGif.ViewModel.ExportPresets.Video.Mov;
using ScreenToGif.ViewModel.ExportPresets.Video.Mp4;
using ScreenToGif.ViewModel.ExportPresets.Video.Webm;

namespace AgentReel.Editing;

/// <summary>
/// Picks the export preset for a format: the one the user last selected in the app, otherwise the built-in default.
/// </summary>
public static class ExportPresets
{
    public static readonly string[] Formats = ["gif", "apng", "webp", "mp4", "webm", "mkv", "mov"];

    public static ExportFormats Parse(string format)
    {
        var name = format.Trim().TrimStart('.');

        if (!Formats.Contains(name, StringComparer.OrdinalIgnoreCase) || !Enum.TryParse<ExportFormats>(name, true, out var type))
            throw new ArgumentException($"Unsupported format '{format}'. Use one of: {string.Join(", ", Formats)}.");

        return type;
    }

    /// <summary>
    /// Returns a copy, so the user's presets in the settings stay untouched.
    /// </summary>
    public static ExportPreset For(ExportFormats type)
    {
        //Gifski is not bundled (AGPL), so its presets are skipped.
        var selected = UserSettings.All.ExportPresets?.OfType<ExportPreset>()
            .FirstOrDefault(f => f.Type == type && f.IsSelected && f.Encoder != EncoderTypes.Gifski);

        var preset = (selected ?? Default(type)).ShallowCopy();

        preset.ExportPartially = false;
        preset.PickLocation = true;
        preset.ExportAsProjectToo = false;
        preset.UploadFile = false;
        preset.SaveToClipboard = false;
        preset.ExecuteCustomCommands = false;

        //Recordings end on long-held frames (idle screen). With passthrough/vfr, x264 B-frames leave the last DTS far
        //behind its PTS, so the MP4 header claims a shorter duration than what plays. Constant framerate avoids that.
        //Advanced mode uses the user's own command line, which is kept as is.
        if (preset is VideoPreset { SettingsMode: VideoSettingsModes.Normal } video)
            video.Vsync = Vsyncs.Cfr;

        return preset;
    }

    private static ExportPreset Default(ExportFormats type) => type switch
    {
        ExportFormats.Gif => EmbeddedGifPreset.Defaults[0],
        ExportFormats.Apng => EmbeddedApngPreset.Default,
        ExportFormats.Webp => FfmpegWebpPreset.Defaults[0],
        ExportFormats.Mp4 => FfmpegMp4Preset.Defaults[0],
        ExportFormats.Webm => FfmpegWebmPreset.Defaults[0],
        ExportFormats.Mkv => FfmpegMkvPreset.Defaults[0],
        ExportFormats.Mov => FfmpegMovPreset.Defaults[0],
        _ => throw new ArgumentException($"Unsupported format '{type}'.")
    };
}

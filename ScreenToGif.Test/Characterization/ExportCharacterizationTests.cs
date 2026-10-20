using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenToGif.Domain.Models;
using ScreenToGif.Model;
using ScreenToGif.Util.Codification;
using ScreenToGif.Util.Extensions;
using ScreenToGif.ViewModel.ExportPresets;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Apng;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Gif;
using ScreenToGif.ViewModel.ExportPresets.Video.Mp4;
using Xunit;

namespace ScreenToGif.Test.Characterization
{
    /// <summary>
    /// Pins the current export output of the built-in encoders before EncodingManager is refactored (Phase 1).
    /// Exports a fixed synthetic project through the private EncodingManager.Encode and compares the output
    /// with the SHA-256 hashes in Data/Characterization/export-golden.json.
    /// To accept new output on purpose, run the tests with AGENTREEL_UPDATE_GOLDEN=1 and commit the json.
    /// </summary>
    public class ExportCharacterizationTests
    {
        private const int Width = 96;
        private const int Height = 64;

        //Frame 3 repeats frame 2, so the "detect unchanged pixels" paths have something to cut.
        private static readonly int[] Delays = [100, 66, 66, 200, 33, 100, 50, 500];
        private static readonly int[] Patterns = [0, 1, 2, 2, 3, 4, 5, 6];

        private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        private static readonly string GoldenPath = Path.Combine(RepoRoot, "ScreenToGif.Test", "Data", "Characterization", "export-golden.json");
        private static readonly object GoldenLock = new();

        static ExportCharacterizationTests()
        {
            //EncodingManager reads localized strings and UserSettings through Application.Current.
            //UserSettings also expects the default Settings.xaml and a Source on every merged dictionary.
            if (Application.Current == null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            foreach (var resource in new[] { "Resources/Settings.xaml", "Resources/Localization/StringResources.en.xaml" })
                Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenToGif;component/" + resource) });
        }

        public static IEnumerable<object[]> EmbeddedPresets()
        {
            for (var i = 0; i < EmbeddedGifPreset.Defaults.Count; i++)
                yield return ["EmbeddedGif-" + i];

            for (var i = 0; i < KGySoftGifPreset.Defaults.Length; i++)
                yield return ["KGySoftGif-" + i];

            yield return ["SystemGif-0"];
            yield return ["EmbeddedApng-0"];
        }

        [Theory]
        [MemberData(nameof(EmbeddedPresets))]
        public async Task ExportMatchesGolden(string key)
        {
            var preset = CreatePreset(key);
            var output = await Export(preset, key);

            string actual;

            try
            {
                AssertDecodable(output);
                actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output)));
            }
            finally
            {
                DeleteExportRoot(output);
            }

            lock (GoldenLock)
            {
                var golden = LoadGolden();

                if (Environment.GetEnvironmentVariable("AGENTREEL_UPDATE_GOLDEN") == "1")
                {
                    golden[key] = actual;
                    SaveGolden(golden);
                    return;
                }

                Assert.True(golden.TryGetValue(key, out var expected), $"No golden hash for '{key}'. Actual: {actual}. Run with AGENTREEL_UPDATE_GOLDEN=1 to record it.");
                Assert.True(expected == actual, $"Export output for '{key}' changed. Expected {expected}, actual {actual}.");
            }
        }

        /// <summary>
        /// FFmpeg is not bundled (license), so this runs only when AGENTREEL_FFMPEG points to ffmpeg.exe.
        /// The bytes depend on the FFmpeg build, so only the container and the frame size are checked.
        /// </summary>
        [FfmpegFact]
        public async Task ExportsMp4WithFfmpeg()
        {
            ScreenToGif.Util.Settings.UserSettings.All.FfmpegLocation = FfmpegFactAttribute.FfmpegPath;

            var preset = FfmpegMp4Preset.Defaults[0];
            var output = await Export(preset, "FfmpegMp4-0");

            var bytes = File.ReadAllBytes(output);
            DeleteExportRoot(output);

            Assert.True(bytes.Length > 1000, $"MP4 is only {bytes.Length} bytes.");
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(bytes, 4, 4));
            Assert.Contains("avc1", System.Text.Encoding.ASCII.GetString(bytes));
        }

        private static ExportPreset CreatePreset(string key)
        {
            var index = int.Parse(key[(key.LastIndexOf('-') + 1)..]);

            return key[..key.LastIndexOf('-')] switch
            {
                "EmbeddedGif" => EmbeddedGifPreset.Defaults[index],
                "KGySoftGif" => KGySoftGifPreset.Defaults[index],
                "SystemGif" => SystemGifPreset.Default,
                "EmbeddedApng" => EmbeddedApngPreset.Default.ShallowCopy(),
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };
        }

        private static async Task<string> Export(ExportPreset preset, string name)
        {
            var root = Path.Combine(Path.GetTempPath(), "AgentReel.Tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
            var outputFolder = Path.Combine(root, "Output");
            Directory.CreateDirectory(outputFolder);

            preset.PickLocation = true;
            preset.OutputFolder = outputFolder;
            preset.OutputFilename = name;
            preset.ResolvedFilename = name;
            preset.Extension = preset.DefaultExtension;
            preset.UploadFile = false;
            preset.SaveToClipboard = false;
            preset.ExecuteCustomCommands = false;

            //Same split as Editor: only the embedded GIF encoder reads raw pixels from a chunk file.
            var useBytes = preset.Type == Domain.Enums.ExportFormats.Gif && preset.Encoder == Domain.Enums.EncoderTypes.ScreenToGif;
            var project = CreateProject(Path.Combine(root, "Encode"), useBytes);

            var encode = Assembly.Load("ScreenToGif").GetType("ScreenToGif.Util.EncodingManager")!
                .GetMethod("Encode", BindingFlags.NonPublic | BindingFlags.Static)!;

            //Id -1 is not registered in EncodingManager.Encodings, so progress updates return before touching the Dispatcher.
            await (Task)encode.Invoke(null, [project, preset, -1, new CancellationTokenSource()])!;

            var output = Path.Combine(outputFolder, name + preset.Extension);

            //Encode logs and swallows its exceptions, so a missing file is the only failure signal here.
            Assert.True(File.Exists(output), $"Encode produced no file for '{name}'. Check the ScreenToGif log folder for the exception.");

            return output;
        }

        /// <summary>
        /// Mirrors ProjectInfo.CopyToExport: either a raw pixel chunk (embedded GIF) or numbered PNG files.
        /// </summary>
        private static ExportProject CreateProject(string folder, bool useBytes)
        {
            Directory.CreateDirectory(folder);

            var files = new List<FrameInfo>();

            for (var i = 0; i < Delays.Length; i++)
            {
                var path = Path.Combine(folder, i + ".png");
                SavePng(CreateFrame(Patterns[i]), path);
                files.Add(new FrameInfo { Index = i, Path = path, Delay = Delays[i] });
            }

            var export = new ExportProject { Path = folder };

            if (!useBytes)
            {
                export.UsesFiles = true;
                export.FramesFiles = files.Cast<Domain.Interfaces.IFrame>().ToList();
                return export;
            }

            export.ChunkPath = Path.Combine(folder, "Chunk");
            export.NewChunkPath = Path.Combine(folder, "NewChunk");

            using var fileStream = new FileStream(export.ChunkPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var pos = 0L;

            foreach (var info in files)
            {
                var image = new PixelUtil(info.Path.SourceFrom());
                image.LockBits();

                fileStream.Write(image.Pixels);

                export.Frames.Add(new ExportFrame
                {
                    Index = info.Index,
                    DataPosition = pos,
                    DataLength = image.Pixels.LongLength,
                    Delay = info.Delay,
                    Rect = new Int32Rect(0, 0, image.Width, image.Height),
                    ImageDepth = image.Depth
                });

                pos += image.Pixels.LongLength;
                image.UnlockBitsWithoutCommit();
            }

            return export;
        }

        /// <summary>
        /// A gradient background with a block that moves and changes color per pattern.
        /// </summary>
        private static BitmapSource CreateFrame(int pattern)
        {
            var stride = Width * 4;
            var pixels = new byte[stride * Height];

            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    var i = y * stride + x * 4;
                    var inBlock = x >= 8 + pattern * 10 && x < 28 + pattern * 10 && y >= 20 && y < 40;

                    pixels[i] = inBlock ? (byte)(255 - pattern * 30) : (byte)(x * 255 / Width);
                    pixels[i + 1] = inBlock ? (byte)(pattern * 40) : (byte)(y * 255 / Height);
                    pixels[i + 2] = inBlock ? (byte)200 : (byte)((x + y) * 2);
                    pixels[i + 3] = 255;
                }
            }

            var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();
            return source;
        }

        private static void SavePng(BitmapSource source, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        private static void AssertDecodable(string path)
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

            Assert.InRange(decoder.Frames.Count, 1, Delays.Length);

            //Frames after the first can be cropped to the changed area, the first one always has the full size.
            Assert.Equal(Width, decoder.Frames[0].PixelWidth);
            Assert.Equal(Height, decoder.Frames[0].PixelHeight);
        }

        private static void DeleteExportRoot(string output)
        {
            //Encode already deletes the input folder, only <root>/Output is left.
            var root = Path.GetDirectoryName(Path.GetDirectoryName(output));

            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }

        private static SortedDictionary<string, string> LoadGolden()
        {
            if (!File.Exists(GoldenPath))
                return new SortedDictionary<string, string>();

            return JsonSerializer.Deserialize<SortedDictionary<string, string>>(File.ReadAllText(GoldenPath)) ?? new SortedDictionary<string, string>();
        }

        private static void SaveGolden(SortedDictionary<string, string> golden)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            File.WriteAllText(GoldenPath, JsonSerializer.Serialize(golden, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
    }

    /// <summary>
    /// Skips the test unless AGENTREEL_FFMPEG points to an existing ffmpeg.exe.
    /// </summary>
    public sealed class FfmpegFactAttribute : FactAttribute
    {
        public static string FfmpegPath => Environment.GetEnvironmentVariable("AGENTREEL_FFMPEG");

        public FfmpegFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(FfmpegPath) || !File.Exists(FfmpegPath))
                Skip = "Set AGENTREEL_FFMPEG to the path of ffmpeg.exe to run this test.";
        }
    }
}

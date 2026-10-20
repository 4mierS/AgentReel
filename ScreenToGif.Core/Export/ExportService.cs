// Moved from ScreenToGif/Util/EncodingManager.cs (ScreenToGif, Ms-PL).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KGySoft.CoreLibraries;
using KGySoft.Drawing;
using KGySoft.Drawing.Imaging;
using KGySoft.Threading;
using ScreenToGif.Domain.Enums;
using ScreenToGif.Domain.Interfaces;
using ScreenToGif.Domain.Models;
using ScreenToGif.ImageUtil;
using ScreenToGif.Util;
using ScreenToGif.Util.Codification.Apng;
using ScreenToGif.Util.Codification.Gif.Encoder;
using ScreenToGif.Util.Codification.Psd;
using ScreenToGif.Util.Extensions;
using ScreenToGif.Util.Settings;
using ScreenToGif.ViewModel;
using ScreenToGif.ViewModel.ExportPresets;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Apng;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Gif;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Webp;
using ScreenToGif.ViewModel.ExportPresets.Image;
using ScreenToGif.ViewModel.ExportPresets.Other;
using ScreenToGif.ViewModel.ExportPresets.Video;
using Color = System.Windows.Media.Color;
using LegacyGifEncoder = ScreenToGif.Util.Codification.Gif.LegacyEncoder.GifEncoder;
using KGySoftGifEncoder = KGySoft.Drawing.Imaging.GifEncoder;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Avif;

namespace ScreenToGif.Core.Export;

/// <summary>
/// Encodes an export project with a preset. Runs headless: progress goes to an <see cref="IEncodingReporter"/>.
/// </summary>
public static class ExportService
{
    /// <summary>
    /// Writes the output file(s) of the project.
    /// </summary>
    /// <returns>False when the export ended early (canceled, mismatched preset or already reported as completed), so no upload, copy or command should follow.</returns>
    public static async Task<bool> EncodeAsync(ExportProject project, ExportPreset preset, IEncodingReporter reporter, CancellationTokenSource tokenSource)
    {
        var processing = LocalizationHelper.Get("S.Encoder.Processing");
        var watch = new Stopwatch();
        watch.Start();

        #region File naming

        if (preset.PickLocation)
        {
            preset.FullPath = Path.Combine(preset.OutputFolder, preset.ResolvedFilename + (preset.Extension ?? preset.DefaultExtension));
        }
        else
        {
            preset.OutputFolder = Path.GetTempPath(); //Get path where the cache is stored instead.
            preset.OutputFilename = Guid.NewGuid() + preset.Extension;
            preset.FullPath = Path.Combine(preset.OutputFolder, preset.OutputFilename);

            //If somehow this happens, try again. TODO: File should be created on the spot, to properly prevent this issue.
            if (File.Exists(Path.Combine(preset.OutputFilename, preset.OutputFilename)))
            {
                preset.OutputFilename = Guid.NewGuid() + preset.Extension;
                preset.FullPath = Path.Combine(preset.OutputFolder, preset.OutputFilename);
            }
        }

        #endregion

        switch (preset.Type)
        {
            case ExportFormats.Apng:
            {
                #region Apng

                switch (preset.Encoder)
                {
                    case EncoderTypes.ScreenToGif:
                    {
                        if (preset is not EmbeddedApngPreset embApngPreset)
                            return false;

                        #region Cut/Paint Unchanged Pixels

                        if (embApngPreset.DetectUnchanged)
                        {
                            reporter.Update(0, LocalizationHelper.Get("S.Encoder.Analyzing"));

                            if (embApngPreset.PaintTransparent)
                                project.FramesFiles = ImageMethods.PaintTransparentAndCut(project.FramesFiles, Colors.Transparent, reporter, tokenSource);
                            else
                                project.FramesFiles = ImageMethods.CutUnchanged(project.FramesFiles, reporter, tokenSource);

                            reporter.Update(0, watch.Elapsed);
                            watch.Restart();
                        }
                        else
                        {
                            var size = project.FramesFiles[0].Path.ScaledSize();
                            project.FramesFiles.ForEach(x => x.Rect = new Int32Rect(0, 0, (int)size.Width, (int)size.Height));
                        }

                        #endregion

                        #region Encoding

                        using (var stream = new MemoryStream())
                        {
                            var frameCount = project.FramesFiles.Count(x => x.HasArea);

                            using (var encoder = new Apng(stream, frameCount, embApngPreset.Looped && project.FrameCount > 1 ? (embApngPreset.RepeatForever ? 0 : embApngPreset.RepeatCount) : 1))
                            {
                                for (var i = 0; i < project.FramesFiles.Count; i++)
                                {
                                    if (!project.FramesFiles[i].HasArea && embApngPreset.DetectUnchanged)
                                        continue;

                                    if (project.FramesFiles[i].Delay == 0)
                                        project.FramesFiles[i].Delay = 10;

                                    encoder.AddFrame(project.FramesFiles[i].Path, project.FramesFiles[i].Rect, project.FramesFiles[i].Delay);

                                    reporter.Update(i, string.Format(processing, i));

                                    #region Cancellation

                                    if (tokenSource.Token.IsCancellationRequested)
                                    {
                                        reporter.Update(EncodingStatus.Canceled);
                                        break;
                                    }

                                    #endregion
                                }
                            }

                            try
                            {
                                using (var fileStream = new FileStream(embApngPreset.FullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                                    stream.WriteTo(fileStream);
                            }
                            catch (Exception ex)
                            {
                                reporter.Update(EncodingStatus.Error);
                                LogWriter.Log(ex, "Apng Encoding");
                            }
                        }

                        #endregion

                        break;
                    }

                    case EncoderTypes.FFmpeg:
                    {
                        await EncodeWithFfmpeg(preset, project.FramesFiles, reporter, tokenSource, processing);
                        break;
                    }
                }

                reporter.Update(1, watch.Elapsed);
                watch.Restart();

                break;

                #endregion
            }
            case ExportFormats.Gif:
            {
                #region Gif

                switch (preset.Encoder)
                {
                    case EncoderTypes.ScreenToGif:

                        #region Frame analysis

                        reporter.Update(0, LocalizationHelper.Get("S.Encoder.Analyzing"));

                        if (preset is not EmbeddedGifPreset embGifPreset)
                            return false;

                        if (embGifPreset.EnableTransparency)
                        {
                            ImageMethods.PaintAndCutForTransparency(project, embGifPreset.SelectTransparencyColor ? embGifPreset.TransparencyColor : new Color?(), embGifPreset.ChromaKey, reporter, tokenSource);
                        }
                        else if (embGifPreset.DetectUnchanged)
                        {
                            if (embGifPreset.PaintTransparent)
                                ImageMethods.PaintTransparentAndCut(project, embGifPreset.ChromaKey, reporter, tokenSource);
                            else
                                ImageMethods.CutUnchanged(project, reporter, tokenSource);
                        }

                        reporter.Update(0, watch.Elapsed);
                        watch.Restart();

                        if (tokenSource.Token.IsCancellationRequested)
                        {
                            reporter.Update(EncodingStatus.Canceled);
                            return false;
                        }

                        #endregion

                        #region ScreenToGif encoding

                        using (var stream = new MemoryStream())
                        {
                            using (var encoder = new GifFile(stream))
                            {
                                encoder.RepeatCount = embGifPreset.Looped && project.FrameCount > 1 ? (embGifPreset.RepeatForever ? 0 : embGifPreset.RepeatCount) : -1;
                                encoder.UseGlobalColorTable = embGifPreset.UseGlobalColorTable;
                                encoder.TransparentColor = embGifPreset.EnableTransparency && embGifPreset.SelectTransparencyColor ? Color.FromArgb(0, embGifPreset.TransparencyColor.R, embGifPreset.TransparencyColor.G, embGifPreset.TransparencyColor.B) :
                                    (embGifPreset.DetectUnchanged && embGifPreset.PaintTransparent) || embGifPreset.EnableTransparency ? Color.FromArgb(0, embGifPreset.ChromaKey.R, embGifPreset.ChromaKey.G, embGifPreset.ChromaKey.B) :
                                    new Color?();
                                encoder.MaximumNumberColor = embGifPreset.MaximumColorCount;
                                encoder.UseFullTransparency = embGifPreset.EnableTransparency;
                                encoder.QuantizationType = embGifPreset.Quantizer;
                                encoder.SamplingFactor = embGifPreset.SamplingFactor;

                                //Get the last index, in cases where the last frames have no changes.
                                var last = project.FramesFiles.FindLastIndex(f => f.HasArea);

                                //Read the frames pixels from the cache.
                                using (var fileStream = new FileStream(project.ChunkPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                                {
                                    reporter.Update(0, string.Format(processing, 0));

                                    for (var i = 0; i < project.Frames.Count; i++)
                                    {
                                        if (!project.Frames[i].HasArea && embGifPreset.DetectUnchanged)
                                            continue;

                                        if (project.Frames[i].Delay == 0)
                                            project.Frames[i].Delay = 10;

                                        fileStream.Position = project.Frames[i].DataPosition;
                                        encoder.AddFrame(fileStream.ReadBytes((int)project.Frames[i].DataLength), project.Frames[i].Rect, project.Frames[i].Delay, last == i);

                                        reporter.Update(i, string.Format(processing, i));

                                        #region Cancellation

                                        if (tokenSource.Token.IsCancellationRequested)
                                        {
                                            reporter.Update(EncodingStatus.Canceled);
                                            break;
                                        }

                                        #endregion
                                    }
                                }
                            }

                            try
                            {
                                using (var fileStream = new FileStream(preset.FullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                                    stream.WriteTo(fileStream);
                            }
                            catch (Exception ex)
                            {
                                reporter.Update(EncodingStatus.Error);
                                LogWriter.Log(ex, "Improved Encoding");
                            }
                        }

                        #endregion

                        break;

                    case EncoderTypes.KGySoft:
                        if (preset is not KGySoftGifPreset kgySoftGifPreset)
                            return false;

                        await EncodeKGySoftGif(kgySoftGifPreset, project.FramesFiles, reporter, tokenSource.Token);
                        break;

                    case EncoderTypes.System:

                        #region System encoding

                        if (preset is not SystemGifPreset systemGifPreset)
                            return false;

                        using (var stream = new MemoryStream())
                        {
                            using (var encoder = new LegacyGifEncoder(stream, null, null, systemGifPreset.Looped && project.FrameCount > 1 ? (systemGifPreset.RepeatForever ? 0 : systemGifPreset.RepeatCount) : -1))
                            {
                                for (var i = 0; i < project.FramesFiles.Count; i++)
                                {
                                    var bitmapAux = new Bitmap(project.FramesFiles[i].Path);
                                    encoder.AddFrame(bitmapAux, 0, 0, TimeSpan.FromMilliseconds(project.FramesFiles[i].Delay));
                                    bitmapAux.Dispose();

                                    reporter.Update(i, string.Format(processing, i));

                                    #region Cancellation

                                    if (tokenSource.Token.IsCancellationRequested)
                                    {
                                        reporter.Update(EncodingStatus.Canceled);
                                        break;
                                    }

                                    #endregion
                                }
                            }

                            stream.Position = 0;

                            try
                            {
                                using (var fileStream = new FileStream(systemGifPreset.FullPath, FileMode.Create, FileAccess.Write, FileShare.None, Constants.BufferSize, false))
                                    stream.WriteTo(fileStream);
                            }
                            catch (Exception ex)
                            {
                                reporter.Update(EncodingStatus.Error);
                                LogWriter.Log(ex, "Encoding with paint.Net.");
                            }
                        }

                        #endregion

                        break;

                    case EncoderTypes.FFmpeg:
                        await EncodeWithFfmpeg(preset, project.FramesFiles, reporter, tokenSource, processing);
                        break;

                    case EncoderTypes.Gifski:
                    {
                        #region Gifski encoding

                        reporter.Update(EncodingStatus.Processing, null, true);

                        if (preset is not GifskiGifPreset gifskiGifPreset)
                            return false;

                        if (!PathHelper.IsGifskiPresent())
                            throw new ApplicationException("Gifski not present.");

                        if (File.Exists(preset.FullPath))
                            File.Delete(preset.FullPath);

                        var size = project.FramesFiles[0].Path.ScaledSize();

                        try
                        {
                            using var gifski = new GifskiInterop();
                            var handle = gifski.Start((uint)size.Width, (uint)size.Height, gifskiGifPreset.Quality, gifskiGifPreset.RepeatForever, gifskiGifPreset.Fast);

                            if (gifski.IsOlderThan0Dot9)
                            {
                                #region Older

                                ThreadPool.QueueUserWorkItem(delegate
                                {
                                    Thread.Sleep(500);

                                    if (reporter.GetStatus() == EncodingStatus.Error)
                                        return;

                                    reporter.Update(EncodingStatus.Processing, null, false);

                                    GifskiErrorCodes res;

                                    try
                                    {
                                        for (var i = 0; i < project.FramesFiles.Count; i++)
                                        {
                                            #region Cancellation

                                            if (tokenSource.Token.IsCancellationRequested)
                                            {
                                                reporter.Update(EncodingStatus.Canceled);
                                                break;
                                            }

                                            #endregion

                                            reporter.Update(i, string.Format(processing, i));

                                            res = gifski.AddFrame(handle, (uint)i, project.FramesFiles[i].Path, project.FramesFiles[i].Delay);

                                            if (res != GifskiErrorCodes.Ok)
                                                throw new Exception("Error while adding frames with Gifski. " + res, new Win32Exception(res.ToString())) { HelpLink = $"Result:\n\r{Marshal.GetLastWin32Error()}" };
                                        }

                                        res = gifski.EndAdding(handle);

                                        if (res != GifskiErrorCodes.Ok)
                                            throw new Exception("Error while finishing adding frames with Gifski. " + res, new Win32Exception(res.ToString())) { HelpLink = $"Result:\n\r{Marshal.GetLastWin32Error()}" };
                                    }
                                    catch (Exception eee)
                                    {
                                        LogWriter.Log(eee, "encode with gifski");
                                    }

                                }, null);

                                gifski.End(handle, gifskiGifPreset.FullPath);

                                #endregion
                            }
                            else
                            {
                                #region Version 0.9.3 and newer

                                var res = gifski.SetOutput(handle, gifskiGifPreset.FullPath);

                                if (res != GifskiErrorCodes.Ok)
                                    throw new Exception("Error while setting output with Gifski. " + res, new Win32Exception()) { HelpLink = $"Result:\n\r{Marshal.GetLastWin32Error()}" };

                                Thread.Sleep(500);

                                if (reporter.GetStatus() == EncodingStatus.Error)
                                    return false;

                                reporter.Update(EncodingStatus.Processing, null, false);

                                var lastDelay = project.FramesFiles[project.FramesFiles.Count - 1].Delay / 1000d;

                                for (var i = 0; i < project.FramesFiles.Count; i++)
                                {
                                    #region Cancellation

                                    if (tokenSource.Token.IsCancellationRequested)
                                    {
                                        reporter.Update(EncodingStatus.Canceled);
                                        break;
                                    }

                                    #endregion

                                    reporter.Update(i, string.Format(processing, i));

                                    System.Diagnostics.Debug.WriteLine($"Frame: {i}");

                                    res = gifski.AddFrame(handle, (uint)i, project.FramesFiles[i].Path, project.FramesFiles[i].Delay, lastDelay, i + 1 == project.FramesFiles.Count);

                                    System.Diagnostics.Debug.WriteLine($"Ok: {i}");

                                    if (res != GifskiErrorCodes.Ok)
                                        throw new Exception("Error while adding frames with Gifski. " + res, new Win32Exception(res.ToString())) { HelpLink = $"Result:\n\r{Marshal.GetLastWin32Error()}" };
                                }

                                reporter.Update(EncodingStatus.Processing, null, false);

                                gifski.EndAdding(handle);

                                #endregion
                            }
                        }
                        catch (Exception exx)
                        {
                            LogWriter.Log(exx, "gifski");
                        }

                        var fileInfo2 = new FileInfo(gifskiGifPreset.FullPath);

                        if (!fileInfo2.Exists || fileInfo2.Length == 0)
                            throw new Exception("Error while encoding the gif with Gifski. Empty output file.", new Win32Exception()) { HelpLink = $"Result:\n\r{Marshal.GetLastWin32Error()}" };

                        #endregion

                        break;
                    }

                    default:
                        throw new Exception("Undefined Gif encoder type");
                }

                reporter.Update(1, watch.Elapsed);
                watch.Restart();

                break;

                #endregion
            }

            case ExportFormats.Bmp:
            case ExportFormats.Jpeg:
            case ExportFormats.Png:
            {
                if (preset is not ImagePreset imagePreset)
                    return false;

                var padLength = project.FramesFiles.Select(s => s.Index).Max().ToString().Length;

                if (!imagePreset.ZipFiles)
                {
                    var frameList = new List<string>();

                    foreach (var frame in project.FramesFiles)
                    {
                        var path = Path.Combine(preset.OutputFolder, $"{preset.ResolvedFilename}-{frame.Index.ToString().PadLeft(padLength, '0')}{preset.Extension ?? preset.DefaultExtension}");

                        if (File.Exists(path))
                            File.Delete(path);

                        reporter.Update(frame.Index, string.Format(processing, frame.Index));

                        switch (preset.Type)
                        {
                            case ExportFormats.Bmp:
                            {
                                using var fileStream = new FileStream(path, FileMode.Create);
                                var bmpEncoder = new BmpBitmapEncoder();
                                bmpEncoder.Frames.Add(BitmapFrame.Create(frame.Path.SourceFrom()));
                                bmpEncoder.Save(fileStream);

                                break;
                            }
                            case ExportFormats.Jpeg:
                            {
                                using var fileStream = new FileStream(path, FileMode.Create);
                                var jpgEncoder = new JpegBitmapEncoder { QualityLevel = 100 };
                                jpgEncoder.Frames.Add(BitmapFrame.Create(frame.Path.SourceFrom()));
                                jpgEncoder.Save(fileStream);

                                break;
                            }
                            case ExportFormats.Png:
                            {
                                File.Copy(frame.Path, path, true);
                                break;
                            }
                        }

                        frameList.Add(path);
                    }

                    reporter.UpdateMultiple(EncodingStatus.Completed, frameList);
                    return false;
                }
                else
                {
                    var fileName = Path.Combine(preset.OutputFolder, preset.ResolvedFilename + ".zip");

                    if (File.Exists(fileName))
                        File.Delete(fileName);

                    //Temporary folder.
                    var outPath = Path.Combine(project.Path, "Export");

                    if (Directory.Exists(outPath))
                        Directory.Delete(outPath, true);

                    var dir = Directory.CreateDirectory(outPath);

                    //Get files.
                    foreach (var frame in project.FramesFiles)
                    {
                        switch (preset.Type)
                        {
                            case ExportFormats.Bmp:
                            {
                                var path = Path.Combine(dir.FullName, $"{frame.Index.ToString().PadLeft(padLength, '0')}.bmp");

                                using (var fileStream = new FileStream(path, FileMode.Create))
                                {
                                    var bmpEncoder = new BmpBitmapEncoder();
                                    bmpEncoder.Frames.Add(BitmapFrame.Create(frame.Path.SourceFrom()));
                                    bmpEncoder.Save(fileStream);
                                }

                                break;
                            }
                            case ExportFormats.Jpeg:
                            {
                                var path = Path.Combine(dir.FullName, $"{frame.Index.ToString().PadLeft(padLength, '0')}.jpg");

                                using (var fileStream = new FileStream(path, FileMode.Create))
                                {
                                    var jpgEncoder = new JpegBitmapEncoder { QualityLevel = 100 };
                                    jpgEncoder.Frames.Add(BitmapFrame.Create(frame.Path.SourceFrom()));
                                    jpgEncoder.Save(fileStream);
                                }

                                break;
                            }
                            case ExportFormats.Png:
                            {
                                var path = Path.Combine(dir.FullName, $"{frame.Index.ToString().PadLeft(padLength, '0')}.png");

                                File.Copy(frame.Path, path, true);
                                break;
                            }
                        }
                    }

                    //Create Zip and clear temporary folder.
                    ZipFile.CreateFromDirectory(dir.FullName, fileName);
                    Directory.Delete(dir.FullName, true);
                }

                break;
            }

            case ExportFormats.Webp:
            case ExportFormats.Avif:
            case ExportFormats.Avi:
            case ExportFormats.Mov:
            case ExportFormats.Mp4:
            case ExportFormats.Mkv:
            case ExportFormats.Webm:
            {
                #region FFmpeg

                await EncodeWithFfmpeg(preset, project.FramesFiles, reporter, tokenSource, processing);

                reporter.Update(1, watch.Elapsed);
                watch.Restart();

                break;

                #endregion
            }

            case ExportFormats.Psd:
            {
                #region Psd

                if (preset is not PsdPreset psdPreset)
                    return false;

                using (var stream = new MemoryStream())
                {
                    using (var encoder = new Psd(stream, psdPreset.Height, psdPreset.Width, psdPreset.CompressImage, psdPreset.SaveTimeline))
                    {
                        for (var i = 0; i < project.FramesFiles.Count; i++)
                        {
                            if (project.FramesFiles[i].Delay == 0)
                                project.FramesFiles[i].Delay = 10;

                            encoder.AddFrame(i, project.FramesFiles[i].Path, project.FramesFiles[i].Delay);

                            reporter.Update(i, string.Format(processing, i));

                            #region Cancellation

                            if (tokenSource.Token.IsCancellationRequested)
                            {
                                reporter.Update(EncodingStatus.Canceled);
                                break;
                            }

                            #endregion
                        }
                    }

                    using (var fileStream = new FileStream(psdPreset.FullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096))
                        stream.WriteTo(fileStream);
                }

                reporter.Update(1, watch.Elapsed);
                watch.Restart();

                break;

                #endregion
            }
            case ExportFormats.Stg:
            {
                #region Project

                if (preset is not StgPreset stgPreset)
                    return false;

                reporter.Update(EncodingStatus.Processing, null, true);
                reporter.Update(0, LocalizationHelper.Get("S.Encoder.CreatingFile"));

                if (File.Exists(stgPreset.FullPath))
                    File.Delete(stgPreset.FullPath);

                ZipFile.CreateFromDirectory(Path.GetDirectoryName(project.FramesFiles[0].Path), stgPreset.FullPath, stgPreset.CompressionLevel, false);

                reporter.Update(1, watch.Elapsed);
                watch.Restart();

                break;

                #endregion
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(preset));
        }

        //If it was canceled, try deleting the file.
        if (tokenSource.Token.IsCancellationRequested)
        {
            if (File.Exists(preset.FullPath))
                File.Delete(preset.FullPath);

            reporter.Update(EncodingStatus.Canceled);
            return false;
        }

        return true;
    }

    private static async Task EncodeKGySoftGif(KGySoftGifPreset preset, IList<IFrame> frames, IEncodingReporter reporter, CancellationToken cancellationToken)
    {
        #region Local Methods

        IEnumerable<IReadableBitmapData> FramesIterator()
        {
            for (int i = 0; i < frames.Count; i++)
            {
                reporter.Update(i);
                using var bitmap = new Bitmap(frames[i].Path);
                using var bitmapData = bitmap.GetReadableBitmapData();
                yield return bitmapData;
            }
        }

        #endregion

        // A little cheating for PingPong mode: though the encoder could handle it, we convert it explicitly to a simple forward loop so
        // we can update the progress in FramesIterator and don't need to pass a DrawingProgress implementation to TaskConfig.Progress
        var animationMode = (AnimationMode)preset.RepeatCount;

        if (animationMode == AnimationMode.PingPong)
        {
            animationMode = AnimationMode.Repeat;

            for (var i = frames.Count - 2; i > 0; i--)
                frames.Add(frames[i]);
        }

        reporter.Update(EncodingStatus.Processing, maxSteps: frames.Count);
        var config = new AnimatedGifConfiguration(FramesIterator(), frames.Select(f => TimeSpan.FromMilliseconds(f.Delay)))
        {
            AllowDeltaFrames = preset.AllowDeltaFrames,
            AllowClippedFrames = preset.AllowClippedFrames,
            AnimationMode = animationMode,
            DeltaTolerance = preset.DeltaTolerance,
            SizeHandling = AnimationFramesSizeHandling.Center,
            Quantizer = QuantizerDescriptor.Create(preset.QuantizerId, preset),
            Ditherer = DithererDescriptor.Create(preset.DithererId, preset),
        };

        await using var stream = File.Create(preset.FullPath, Constants.BufferSize);
        await KGySoftGifEncoder.EncodeAnimationAsync(config, stream, new TaskConfig
        {
            CancellationToken = cancellationToken,
            ThrowIfCanceled = false
        });

        if (!cancellationToken.IsCancellationRequested)
            await stream.FlushAsync(cancellationToken);
    }

    private static async Task EncodeWithFfmpeg(ExportPreset preset, List<IFrame> listFrames, IEncodingReporter reporter, CancellationTokenSource tokenSource, string processing)
    {
        reporter.Update(EncodingStatus.Processing, null, true);

        if (!await PathHelper.IsFfmpegPresent())
            throw new ApplicationException("FFmpeg not present.");

        if (File.Exists(preset.FullPath))
            File.Delete(preset.FullPath);

        #region Generate concat

        var concat = new StringBuilder();
        foreach (var frame in listFrames)
        {
            concat.AppendLine("file '" + frame.Path + "'");
            concat.AppendLine("duration " + (frame.Delay / 1000d).ToString(CultureInfo.InvariantCulture));
        }

        if (preset.Type is not ExportFormats.Gif or ExportFormats.Apng or ExportFormats.Webp or ExportFormats.Avif)
        {
            //Fix for last frame.
            concat.AppendLine("file '" + listFrames.LastOrDefault()?.Path + "'");
            concat.AppendLine("duration 0");
        }

        var concatPath = Path.GetDirectoryName(listFrames[0].Path) ?? Path.GetTempPath();
        var concatFile = Path.Combine(concatPath, "concat.txt");

        if (!Directory.Exists(concatPath))
            Directory.CreateDirectory(concatPath);

        if (File.Exists(concatFile))
            File.Delete(concatFile);

        File.WriteAllText(concatFile, concat.ToString());

        #endregion

        var firstPass = "";
        var secondPass = "";
        //TODO: Adapt the code to support v4 or v6
        switch (preset.Type)
        {
            case ExportFormats.Gif:
            {
                #region Gif

                if (preset is not FfmpegGifPreset gifPreset)
                    return;

                //ffmpeg -vsync 0 {I} -loop 0 -lavfi palettegen=stats_mode=single[pal],[0:v][pal]paletteuse=new=1:dither=sierra2_4a:diff_mode=rectangle -f gif {O}
                if (gifPreset.SettingsMode == VideoSettingsModes.Advanced)
                    firstPass = gifPreset.Parameters.Replace("\n", " ").Replace("\r", "");
                else
                {
                    //Input and loop.
                    firstPass += "{I} ";
                    firstPass += $"-loop {(gifPreset.Looped ? gifPreset.RepeatForever ? 0 : gifPreset.RepeatCount : -1)} ";

                    //Palette and dither. Does not work properly: (gifPreset.UseGlobalColorTable ? "diff" : "single")
                    firstPass += $"-lavfi palettegen=stats_mode=diff[pal],[0:v][pal]paletteuse={(gifPreset.UseGlobalColorTable ? "" : "new=1:")}";
                    firstPass += $"dither={gifPreset.Dither.GetDescription()}";
                    firstPass += (gifPreset.Dither == DitherMethods.Bayer ? $":bayer_scale={gifPreset.BayerScale}" : "");
                    firstPass += ":diff_mode=rectangle ";

                    //Pixel format.
                    if (gifPreset.PixelFormat != VideoPixelFormats.Auto)
                        firstPass += $"-pix_fmt {gifPreset.PixelFormat.GetLowerDescription()} ";

                    //Framerate.
                    if (gifPreset.Framerate != Framerates.Auto)
                        firstPass += $"-r {(gifPreset.Framerate == Framerates.Custom ? gifPreset.CustomFramerate.ToString(CultureInfo.InvariantCulture) : gifPreset.Framerate.GetLowerDescription())} ";

                    //Format and output.
                    firstPass += "-f gif ";

                    //Vsync
                    if (gifPreset.Vsync != Vsyncs.Off)
                    {
                        if (UserSettings.All.HasOlderFfmpegVersion)
                            firstPass += "-vsync " + gifPreset.Vsync.GetLowerDescription();
                        else
                            firstPass += "-fps_mode " + gifPreset.Vsync.GetLowerDescription();
                    }

                    firstPass += " {O}";
                }

                break;

                #endregion
            }
            case ExportFormats.Apng:
            {
                #region Apng

                if (preset is not FfmpegApngPreset apngPreset)
                    return;

                //ffmpeg -vsync 0 {I} -pred mixed -plays 0 -f apng {O}
                if (apngPreset.SettingsMode == VideoSettingsModes.Advanced)
                    firstPass = apngPreset.Parameters.Replace("\n", " ").Replace("\r", "");
                else
                {
                    //Input and loop.
                    firstPass += "{I} ";
                    firstPass += $"-plays {(apngPreset.Looped ? apngPreset.RepeatForever ? 0 : apngPreset.RepeatCount : -1)} ";

                    //Prediction method.
                    if (apngPreset.PredictionMethod != PredictionMethods.None)
                        firstPass += $"-pred {apngPreset.PredictionMethod.ToString().ToLower()} ";

                    //Pixel format.
                    if (apngPreset.PixelFormat != VideoPixelFormats.Auto)
                        firstPass += $"-pix_fmt {apngPreset.PixelFormat.GetLowerDescription()} ";

                    //Framerate.
                    if (apngPreset.Framerate != Framerates.Auto)
                        firstPass += $"-r {(apngPreset.Framerate == Framerates.Custom ? apngPreset.CustomFramerate.ToString(CultureInfo.InvariantCulture) : apngPreset.Framerate.GetLowerDescription())} ";

                    //Format and output.
                    firstPass += "-f apng ";

                    //Vsync
                    if (apngPreset.Vsync != Vsyncs.Off)
                    {
                        if (UserSettings.All.HasOlderFfmpegVersion)
                            firstPass += "-vsync " + apngPreset.Vsync.GetLowerDescription();
                        else
                            firstPass += "-fps_mode " + apngPreset.Vsync.GetLowerDescription();
                    }

                    firstPass += " {O}";
                }

                break;

                #endregion
            }
            case ExportFormats.Webp:
            {
                #region Webp

                if (preset is not FfmpegWebpPreset webpPreset)
                    return;

                //ffmpeg -vsync 0 {I} -c:v libwebp_anim -lossless 0 -quality 75 -loop 0 -f webp {O}
                if (webpPreset.SettingsMode == VideoSettingsModes.Advanced)
                    firstPass = webpPreset.Parameters.Replace("\n", " ").Replace("\r", "");
                else
                {
                    //Vsync
                    if (webpPreset.Vsync != Vsyncs.Off)
                        firstPass += $"-vsync {webpPreset.Vsync.ToString().ToLower()} ";

                    //Input, encoder and loop.
                    firstPass += "{I} -c:v libwebp_anim ";
                    firstPass += $"-loop {(webpPreset.Looped ? webpPreset.RepeatForever ? 0 : webpPreset.RepeatCount : -1)} ";

                    //Codec preset.
                    if (webpPreset.CodecPreset != VideoCodecPresets.Default)
                        firstPass += $"-preset {webpPreset.CodecPreset.GetLowerDescription()} ";

                    //Lossless.
                    firstPass += $"-lossless {(webpPreset.Lossless ? "1" : "0")} ";

                    //Quality.
                    firstPass += $"-quality {webpPreset.Quality} ";

                    //Pixel format.
                    if (webpPreset.PixelFormat != VideoPixelFormats.Auto)
                        firstPass += $"-pix_fmt {webpPreset.PixelFormat.GetLowerDescription()} ";

                    //Framerate.
                    if (webpPreset.Framerate != Framerates.Auto)
                        firstPass += $"-r {(webpPreset.Framerate == Framerates.Custom ? webpPreset.CustomFramerate.ToString(CultureInfo.InvariantCulture) : webpPreset.Framerate.GetLowerDescription())} ";

                    //Format and output.
                    firstPass += "-f webp ";

                    //Vsync
                    if (webpPreset.Vsync != Vsyncs.Off)
                    {
                        if (UserSettings.All.HasOlderFfmpegVersion)
                            firstPass += "-vsync " + webpPreset.Vsync.GetLowerDescription();
                        else
                            firstPass += "-fps_mode " + webpPreset.Vsync.GetLowerDescription();
                    }

                    firstPass += " {O}";
                }

                break;

                #endregion
            }
            case ExportFormats.Avif:
            {
                #region Avif

                if (preset is not FfmpegAvifPreset avifPreset)
                    return;

                //ffmpeg -vsync 0 {I} -c:v libwebp_anim -lossless 0 -quality 75 -loop 0 -f webp {O}
                if (avifPreset.SettingsMode == VideoSettingsModes.Advanced)
                    firstPass = avifPreset.Parameters.Replace("\n", " ").Replace("\r", "");
                else
                {
                    //Vsync
                    if (avifPreset.Vsync != Vsyncs.Off)
                        firstPass += $"-vsync {avifPreset.Vsync.ToString().ToLower()} ";

                    //Input, encoder and loop.
                    firstPass += $"{{I}} -c:v {avifPreset.VideoCodec.GetLowerDescription()} ";
                    firstPass += $"-loop {(avifPreset.Looped ? avifPreset.RepeatForever ? 0 : avifPreset.RepeatCount : -1)} ";

                    //Codec preset.
                    if (avifPreset.CodecPreset != VideoCodecPresets.Default)
                        firstPass += $"-preset {avifPreset.CodecPreset.GetLowerDescription()} ";

                    //Quality.
                    firstPass += $"-quality {avifPreset.Quality} ";

                    //Pixel format.
                    if (avifPreset.PixelFormat != VideoPixelFormats.Auto)
                        firstPass += $"-pix_fmt {avifPreset.PixelFormat.GetLowerDescription()} ";

                    //Framerate.
                    if (avifPreset.Framerate != Framerates.Auto)
                        firstPass += $"-r {(avifPreset.Framerate == Framerates.Custom ? avifPreset.CustomFramerate.ToString(CultureInfo.InvariantCulture) : avifPreset.Framerate.GetLowerDescription())} ";

                    //Format and output.
                    firstPass += "-f avif ";

                    //Vsync
                    if (avifPreset.Vsync != Vsyncs.Off)
                    {
                        if (UserSettings.All.HasOlderFfmpegVersion)
                            firstPass += "-vsync " + avifPreset.Vsync.GetLowerDescription();
                        else
                            firstPass += "-fps_mode " + avifPreset.Vsync.GetLowerDescription();
                    }

                    firstPass += " {O}";
                }

                break;

                #endregion
            }

            case ExportFormats.Avi:
            case ExportFormats.Mkv:
            case ExportFormats.Mov:
            case ExportFormats.Mp4:
            case ExportFormats.Webm:
            {
                #region Video

                if (preset is not VideoPreset videoPreset)
                    return;

                if (videoPreset.SettingsMode == VideoSettingsModes.Advanced)
                {
                    firstPass = videoPreset.Parameters.Replace("\n", " ").Replace("\r", "");

                    if (firstPass.Contains("-pass 2"))
                    {
                        firstPass = firstPass.Replace("-pass 2", $"-pass 1 -passlogfile \"{preset.FullPath}\" ");
                        secondPass = "-hide_banner " + firstPass.Replace("-pass 1", $"-pass 2 -passlogfile \"{preset.FullPath}\" ");
                    }
                }
                else
                {
                    //Hardware acceleration.
                    if (videoPreset.HardwareAcceleration != HardwareAccelerationModes.Off)
                        firstPass += "-hwaccel auto ";

                    //Input and encoder.
                    firstPass += "{I} ";
                    firstPass += $"-c:v {videoPreset.VideoCodec.GetLowerDescription()} ";

                    //Some codecs require special treatments.
                    if (videoPreset.VideoCodec == VideoCodecs.Mpeg4)
                        firstPass += "-vtag xvid ";
                    else if (videoPreset.VideoCodec == VideoCodecs.Vp9)
                        firstPass += "-tile-columns 6 -frame-parallel 1 -auto-alt-ref 1 -lag-in-frames 25 ";

                    //Codec preset.
                    if (videoPreset.CodecPreset != VideoCodecPresets.Default && videoPreset.CodecPreset != VideoCodecPresets.None && videoPreset.CodecPreset != VideoCodecPresets.NotSelected)
                    {
                        if (videoPreset.VideoCodec is VideoCodecs.SvtAv1 or VideoCodecs.Rav1E)
                            firstPass += $"-preset {(int) videoPreset.CodecPreset} ";
                        else
                            firstPass += $"-preset {videoPreset.CodecPreset.GetLowerDescription()} ";
                    }

                    //Pixel format.
                    if (videoPreset.PixelFormat != VideoPixelFormats.Auto)
                        firstPass += $"-pix_fmt {videoPreset.PixelFormat.GetLowerDescription()} ";

                    //Workaround, makes the size to be divisible by two.
                    firstPass += "-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" "; //"scale=iw+mod(iw,2):ih+mod(ih,2):flags=neighbor" OR "pad=width={W}:height={H}:x=0:y=0:color=black"

                    //CRF.
                    if (videoPreset.ConstantRateFactor > 0)
                        firstPass += $"-crf {videoPreset.ConstantRateFactor.Value.ToString(CultureInfo.InvariantCulture)} ";

                    //Bitrate.
                    if (videoPreset.IsVariableBitRate)
                        firstPass += $"-q:v {videoPreset.QualityLevel.ToString(CultureInfo.InvariantCulture)} ";
                    else
                    {
                        if (videoPreset.BitRate > 0)
                            firstPass += $"-b:v {videoPreset.BitRate.ToString(CultureInfo.InvariantCulture)}{videoPreset.BitRateUnit.GetDescription()} ";
                        else if (videoPreset.BitRate == 0 && (videoPreset.VideoCodec == VideoCodecs.Vp8 || videoPreset.VideoCodec == VideoCodecs.Vp9))
                            firstPass += "-b:v 0 ";
                    }

                    //Minimum bitrate.
                    if (videoPreset.MinimumBitRate > 0)
                        firstPass += $"-minrate {videoPreset.MinimumBitRate.ToString(CultureInfo.InvariantCulture)}{videoPreset.MinimumBitRateUnit.GetDescription()} ";

                    //Maximum bitrate.
                    if (videoPreset.MaximumBitRate > 0)
                        firstPass += $"-maxrate {videoPreset.MaximumBitRate.ToString(CultureInfo.InvariantCulture)}{videoPreset.MaximumBitRateUnit.GetDescription()} ";

                    //Buffer size.
                    if (videoPreset.RateControlBuffer > 0)
                        firstPass += $"-bufsize {videoPreset.RateControlBuffer.ToString(CultureInfo.InvariantCulture)}{videoPreset.RateControlBufferUnit.GetDescription()} ";

                    //First pass adjustments.
                    if (videoPreset.Pass > 1)
                    {
                        if (videoPreset.VideoCodec == VideoCodecs.X265)
                            firstPass += "-x265-params pass=1 ";
                        else
                            firstPass += "-pass 1 ";

                        firstPass += $"-passlogfile \"{preset.FullPath}\" ";
                    }

                    //Framerate.
                    if (videoPreset.Framerate != Framerates.Auto)
                        firstPass += $"-r {(videoPreset.Framerate == Framerates.Custom ? videoPreset.CustomFramerate.ToString(CultureInfo.InvariantCulture) : videoPreset.Framerate.GetLowerDescription())} ";

                    //Format and output.
                    firstPass += $"-f {preset.Type.ToString().ToLower().Replace("mkv", "matroska")} ";

                    //Vsync
                    if (videoPreset.Vsync != Vsyncs.Off)
                    {
                        if (UserSettings.All.HasOlderFfmpegVersion)
                            firstPass += "-vsync " + videoPreset.Vsync.GetLowerDescription();
                        else
                            firstPass += "-fps_mode " + videoPreset.Vsync.GetLowerDescription();
                    }

                    firstPass += " {O}";

                    //Second pass, using a similar command with some adjustments.
                    if (videoPreset.Pass > 1)
                        secondPass = "-hide_banner " + firstPass.Replace("-pass 1", "-pass 2").Replace("pass=1", "pass=2");
                }

                break;

                #endregion
            }
        }

        //Replace special params.
        firstPass = firstPass.Replace("{I}", $"-safe 0 -f concat -i \"file:{concatFile}\"").Replace("{O}", $"-y \"{preset.FullPath}\"")
            .Replace("{H}", preset.Height.DivisibleByTwo().ToString(CultureInfo.InvariantCulture)).Replace("{W}", preset.Width.DivisibleByTwo().ToString(CultureInfo.InvariantCulture));

        secondPass = secondPass.Replace("{I}", $"-safe 0 -f concat -i \"file:{concatFile}\"").Replace("{O}", $"-y \"{preset.FullPath}\"")
            .Replace("{H}", preset.Height.DivisibleByTwo().ToString(CultureInfo.InvariantCulture)).Replace("{W}", preset.Width.DivisibleByTwo().ToString(CultureInfo.InvariantCulture));

        var process = new ProcessStartInfo(UserSettings.All.FfmpegLocation)
        {
            Arguments = firstPass,
            CreateNoWindow = true,
            ErrorDialog = false,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        var log = "";
        using (var pro = Process.Start(process))
        {
            var indeterminate = true;

            reporter.Update(0, LocalizationHelper.Get("S.Encoder.Analyzing"));

            while (!pro.StandardError.EndOfStream)
            {
                if (tokenSource.IsCancellationRequested)
                {
                    pro.Kill();
                    return;
                }

                var line = pro.StandardError.ReadLine() ?? "";
                log += Environment.NewLine + line;

                var split = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                var containsDuplicatedFrames = line.Contains("dup=");

                for (var block = 0; block < split.Length; block++)
                {
                    //frame=  321 fps=170 q=-0.0 Lsize=      57kB time=00:00:14.85 bitrate=  31.2kbits/s speed=7.87x
                    if (!split[block].StartsWith("frame="))
                        continue;

                    if (int.TryParse(split[block + 1], out var frame))
                    {
                        if (containsDuplicatedFrames)
                        {
                            var dupFramesPosition = split.IndexOf(x => x.Contains("dup="));
                            var dupFramesValue = split[dupFramesPosition][4..]; //skip dup=

                            if (int.TryParse(dupFramesValue, out var duplicatedFrames))
                            {
                                // if we've managed to read number of duplicated frames - adjust the frame number
                                frame -= duplicatedFrames;
                            }
                        }

                        if (frame > 0)
                        {
                            if (indeterminate)
                            {
                                reporter.Update(EncodingStatus.Processing, null, false);
                                indeterminate = false;
                            }

                            reporter.Update(frame, string.Format(processing, frame));
                        }
                    }

                    break;
                }
            }
        }

        var fileInfo = new FileInfo(preset.FullPath);

        //Execute the second pass, cleaning up the logs.
        if (!string.IsNullOrWhiteSpace(secondPass))
        {
            //I could try using as a single command.
            //ffmpeg -y -hwaccel auto {I} -c:v h264_nvenc -pix_fmt yuv420p -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" -pass 1 -f avi NUL
            //&&
            //ffmpeg -y -hwaccel auto {I} -c:v h264_nvenc -pix_fmt yuv420p -vf "scale=trunc(iw/2)*2:trunc(ih/2)*2" -pass 2 -f avi {O}

            log += Environment.NewLine + SecondPassFfmpeg(secondPass, reporter, tokenSource, LocalizationHelper.Get("S.Encoder.Processing.Second"));

            EraseSecondPassLogs(preset.FullPath);
        }

        if (!fileInfo.Exists || fileInfo.Length == 0)
            throw new Exception($"Error while encoding the {preset.Type} with FFmpeg.") { HelpLink = $"Command:\n\r{firstPass + Environment.NewLine + secondPass}\n\rResult:\n\r{log}" };
    }

    private static string SecondPassFfmpeg(string command, IEncodingReporter reporter, CancellationTokenSource tokenSource, string processing)
    {
        var log = "";

        var process = new ProcessStartInfo(UserSettings.All.FfmpegLocation)
        {
            Arguments = command,
            CreateNoWindow = true,
            ErrorDialog = false,
            UseShellExecute = false,
            RedirectStandardError = true
        };

        using (var pro = Process.Start(process))
        {
            var indeterminate = true;

            reporter.Update(0, LocalizationHelper.Get("S.Encoder.Analyzing.Second"));

            while (!pro.StandardError.EndOfStream)
            {
                if (tokenSource.IsCancellationRequested)
                {
                    pro.Kill();
                    return log;
                }

                var line = pro.StandardError.ReadLine() ?? "";
                log += Environment.NewLine + line;

                var split = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);

                for (var block = 0; block < split.Length; block++)
                {
                    //frame=  321 fps=170 q=-0.0 Lsize=      57kB time=00:00:14.85 bitrate=  31.2kbits/s speed=7.87x
                    if (!split[block].StartsWith("frame="))
                        continue;

                    if (int.TryParse(split[block + 1], out var frame))
                    {
                        if (frame > 0)
                        {
                            if (indeterminate)
                            {
                                reporter.Update(EncodingStatus.Processing, null, false);
                                indeterminate = false;
                            }

                            reporter.Update(frame, string.Format(processing, frame));
                        }
                    }

                    break;
                }
            }
        }

        return log;
    }

    private static void EraseSecondPassLogs(string filename)
    {
        try
        {
            if (File.Exists(filename + "-0.log.mbtree"))
                File.Delete(filename + "-0.log.mbtree");

            if (File.Exists(filename + "-0.log"))
                File.Delete(filename + "-0.log");
        }
        catch (Exception ex)
        {
            LogWriter.Log(ex, "Impossible to delete the log files created by the second pass.");
        }
    }
}

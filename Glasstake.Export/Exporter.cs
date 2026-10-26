using System.IO;
using Glasstake.Export.Apng;
using Glasstake.Export.Gif;
using Glasstake.Export.Mp4;

namespace Glasstake.Export;

/// <summary>
/// A format writer that takes frames one at a time. New formats (WebM, WebP …) plug in here.
/// </summary>
internal interface IAnimationWriter : IDisposable
{
    /// <summary>
    /// Adds a frame: Width * Height pixels as 0xAARRGGBB, top-down. The array may be reused after the call.
    /// </summary>
    void Add(uint[] pixels, int delayMs);

    void Finish();
}

public static class Exporter
{
    /// <summary>
    /// Exports the frames to <see cref="ExportRequest.OutputPath"/>, reading one frame at a time. A failed or canceled
    /// export leaves no file behind.
    /// </summary>
    public static Task ExportAsync(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Export(request, progress, cancellationToken), cancellationToken);

    public static void Export(ExportRequest request, IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Frames.Count == 0)
            throw new ArgumentException("There are no frames to export.", nameof(request));

        if (request.Width < 1 || request.Height < 1)
            throw new ArgumentException("Width and height must be positive.", nameof(request));

        var folder = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));

        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var reader = new FrameReader(request.Width, request.Height);
        var pixels = new uint[request.Width * request.Height];
        Stream? stream = null;

        try
        {
            IAnimationWriter writer;

            if (request.Format == ExportFormat.Mp4)
            {
                //Media Foundation opens the file itself.
                File.Delete(request.OutputPath);
                writer = new Mp4Writer(request.OutputPath, request.Width, request.Height, request.Mp4);
            }
            else
            {
                stream = new FileStream(request.OutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);

                writer = request.Format switch
                {
                    ExportFormat.Gif => new GifWriter(stream, request.Width, request.Height, request.Gif),
                    ExportFormat.Apng => new ApngWriter(stream, request.Width, request.Height, request.Apng),
                    _ => throw new NotSupportedException($"Format {request.Format} is not supported.")
                };
            }

            using (writer)
            {
                for (var i = 0; i < request.Frames.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var frame = request.Frames[i];
                    reader.Read(frame.Path, pixels);
                    writer.Add(pixels, frame.DelayMs);
                    progress?.Report(new ExportProgress(i + 1, request.Frames.Count));
                }

                writer.Finish();
            }

            stream?.Dispose();
        }
        catch
        {
            stream?.Dispose();

            try
            {
                File.Delete(request.OutputPath);
            }
            catch (IOException)
            {
            }

            throw;
        }
    }
}

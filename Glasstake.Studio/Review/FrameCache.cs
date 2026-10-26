using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace Glasstake.Studio.Review;

/// <summary>
/// Decodes frames on the thread pool and keeps the most recently used ones. A full HD frame takes about 8 MB decoded,
/// so only a window around the playhead is kept.
/// </summary>
public sealed class FrameCache(int capacity, int decodeWidth)
{
    private readonly ConcurrentDictionary<string, BitmapSource> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    public BitmapSource? TryGet(string path)
    {
        if (!_frames.TryGetValue(path, out var frame))
            return null;

        Touch(path);
        return frame;
    }

    public Task<BitmapSource?> GetAsync(string path)
    {
        if (TryGet(path) is { } frame)
            return Task.FromResult<BitmapSource?>(frame);

        return _pending.GetOrAdd(path, key => Task.Run(() =>
        {
            var decoded = Decode(key);

            if (decoded != null)
            {
                _frames[key] = decoded;
                Touch(key);
            }

            _pending.TryRemove(key, out _);
            return decoded;
        }));
    }

    /// <summary>
    /// Starts decoding the frames that come next, so playback finds them ready.
    /// </summary>
    public void Prefetch(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            _ = GetAsync(path);
    }

    private void Touch(string path)
    {
        lock (_order)
        {
            _order.Remove(path);
            _order.AddFirst(path);

            while (_order.Count > capacity)
            {
                _frames.TryRemove(_order.Last!.Value, out _);
                _order.RemoveLast();
            }
        }
    }

    private BitmapSource? Decode(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path);

            if (decodeWidth > 0)
                image.DecodePixelWidth = decodeWidth;

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}

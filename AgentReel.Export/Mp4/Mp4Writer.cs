using System.IO;
using Vortice.MediaFoundation;

namespace AgentReel.Export.Mp4;

/// <summary>
/// Writes an H.264 MP4 with the encoder that ships with Windows (Media Foundation sink writer), so no FFmpeg is needed.
/// The video has a constant frame rate: each frame is repeated for as many video frames as its delay covers (rounded
/// on the running end time, so nothing drifts), which keeps holds exact and the container duration equal to the
/// recording. Width and height are rounded down to even numbers, as H.264 with 4:2:0 chroma needs.
/// </summary>
internal sealed class Mp4Writer : IAnimationWriter
{
    //Attribute and type GUIDs from mfapi.h / mfreadwrite.h / wmcodecdsp.h.
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    private static readonly Guid ContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
    private static readonly Guid ContainerMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");

    private const uint Progressive = 2;

    private readonly int _sourceWidth;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly long _tick;
    private readonly IMFSinkWriter _writer;
    private readonly int _stream;

    private long _endMs;
    private long _written;
    private bool _started;

    public Mp4Writer(string path, int width, int height, Mp4Options options)
    {
        _sourceWidth = width;
        _width = width & ~1;
        _height = height & ~1;
        _fps = Math.Clamp(options.Fps, 1, 120);
        _tick = 10_000_000L / _fps;

        if (_width < 2 || _height < 2)
            throw new ArgumentException("An MP4 needs at least 2x2 pixels.");

        var bitrate = options.BitsPerSecond ?? (int)Math.Clamp(_width * (long)_height * _fps / 10, 1_000_000, 16_000_000);

        MediaFactory.MFStartup().CheckError();
        _started = true;

        using var attributes = MediaFactory.MFCreateAttributes(2);
        attributes.Set(EnableHardwareTransforms, options.HardwareEncoder ? 1u : 0u);
        attributes.Set(ContainerType, ContainerMpeg4);

        _writer = MediaFactory.MFCreateSinkWriterFromURL(path, null, attributes);

        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MajorType, VideoMajor);
            output.Set(Subtype, H264);
            output.Set(AvgBitrate, (uint)bitrate);
            output.Set(InterlaceMode, Progressive);
            output.Set(FrameSize, Pack(_width, _height));
            output.Set(FrameRate, Pack(_fps, 1));
            output.Set(PixelAspectRatio, Pack(1, 1));
            _stream = _writer.AddStream(output);
        }

        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MajorType, VideoMajor);
            input.Set(Subtype, Rgb32);
            input.Set(InterlaceMode, Progressive);
            input.Set(FrameSize, Pack(_width, _height));
            input.Set(FrameRate, Pack(_fps, 1));
            input.Set(PixelAspectRatio, Pack(1, 1));

            //RGB in Media Foundation is bottom-up unless the stride says otherwise; the frames are top-down.
            input.Set(DefaultStride, (uint)(_width * 4));
            _writer.SetInputMediaType(_stream, input, null);
        }

        _writer.BeginWriting();
    }

    public void Add(uint[] pixels, int delayMs)
    {
        _endMs += Math.Max(0, delayMs);
        var end = (long)Math.Round(_endMs * _fps / 1000.0, MidpointRounding.AwayFromZero);
        var count = end - _written;

        //Shorter than one video frame: it falls between two samples of a constant frame rate.
        if (count <= 0)
            return;

        using var buffer = MediaFactory.MFCreateMemoryBuffer(_width * _height * 4);
        Fill(buffer, pixels);

        for (var i = 0; i < count; i++)
        {
            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            sample.SampleTime = _written * _tick;
            sample.SampleDuration = _tick;
            _writer.WriteSample(_stream, sample);
            _written++;
        }
    }

    public void Finish()
    {
        //A recording made only of frames shorter than one video frame still needs a picture.
        if (_written == 0)
            throw new InvalidOperationException("The recording is shorter than one video frame.");

        _writer.Finalize();
    }

    public void Dispose()
    {
        _writer.Dispose();

        if (_started)
            MediaFactory.MFShutdown();
    }

    private unsafe void Fill(IMFMediaBuffer buffer, uint[] pixels)
    {
        buffer.Lock(out var data, out _, out _);

        try
        {
            var target = new Span<uint>((void*)data, _width * _height);

            for (var y = 0; y < _height; y++)
                pixels.AsSpan(y * _sourceWidth, _width).CopyTo(target.Slice(y * _width, _width));
        }
        finally
        {
            buffer.Unlock();
        }

        buffer.CurrentLength = _width * _height * 4;
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;
}

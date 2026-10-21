namespace AgentReel.Export;

/// <summary>
/// Turns frame delays in milliseconds into a coarser unit (GIF: 10 ms) without losing time: each delay is the rounded
/// end time minus what was written so far, so rounding errors do not add up over the recording.
/// </summary>
internal sealed class DelayClock(int unitMs, int minUnits = 2, int maxUnits = 65535)
{
    private long _endMs;
    private long _written;

    /// <summary>
    /// The delay in units for a frame that lasts <paramref name="delayMs"/>. Browsers show GIF delays below 2/100 s as
    /// 1/10 s, so a shorter frame gets the minimum and later frames give the time back.
    /// </summary>
    public int Next(int delayMs)
    {
        _endMs += Math.Max(0, delayMs);
        var end = (long)Math.Round(_endMs / (double)unitMs, MidpointRounding.AwayFromZero);
        var units = (int)Math.Clamp(end - _written, minUnits, maxUnits);
        _written += units;
        return units;
    }
}

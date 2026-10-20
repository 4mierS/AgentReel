using Microsoft.Extensions.Logging;
using ScreenToGif.Core.Export;
using ScreenToGif.Domain.Enums;

namespace AgentReel.Mcp;

/// <summary>
/// Logs export progress to stderr and remembers the final status and error.
/// </summary>
internal sealed class LogEncodingReporter(ILogger logger) : IEncodingReporter
{
    private EncodingStatus? _status;

    public Exception? Exception { get; private set; }

    public void Update(int currentFrame, string text) => logger.LogDebug("Export: frame {Frame}: {Text}", currentFrame, text);

    public void Update(int currentFrame) => logger.LogTrace("Export: frame {Frame}", currentFrame);

    public void Update(int type, TimeSpan elapsed) => logger.LogDebug("Export: stage {Type} took {Elapsed}", type, elapsed);

    public void Update(string text, bool isIndeterminate = false, bool findText = false) => logger.LogDebug("Export: {Text}", text);

    public void Update(EncodingStatus status, string? fileName = null, bool isIndeterminate = false, Exception? exception = null, int? maxSteps = null)
    {
        _status = status;
        Exception = exception ?? Exception;
        logger.LogDebug("Export: {Status} {File}", status, fileName);
    }

    public void UpdateMultiple(EncodingStatus status, List<string> fileNames, bool isIndeterminate = false) => Update(status);

    public EncodingStatus? GetStatus() => _status;
}

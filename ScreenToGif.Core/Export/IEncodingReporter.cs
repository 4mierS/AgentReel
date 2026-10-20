using ScreenToGif.Domain.Enums;

namespace ScreenToGif.Core.Export;

/// <summary>
/// Receives the progress of one export. Calls come from the encoding thread.
/// Mirrors the per-task updates of the app's EncodingManager.
/// </summary>
public interface IEncodingReporter
{
    void Update(int currentFrame, string text);

    void Update(int currentFrame);

    /// <summary>
    /// Reports how long a stage took. Type: 0 analysis, 1 encoding, 2 upload, 3 copy, 4 commands.
    /// </summary>
    void Update(int type, TimeSpan elapsed);

    void Update(string text, bool isIndeterminate = false, bool findText = false);

    void Update(EncodingStatus status, string fileName = null, bool isIndeterminate = false, Exception exception = null, int? maxSteps = null);

    void UpdateMultiple(EncodingStatus status, List<string> fileNames, bool isIndeterminate = false);

    EncodingStatus? GetStatus();
}

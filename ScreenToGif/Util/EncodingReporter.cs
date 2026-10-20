using System;
using System.Collections.Generic;
using ScreenToGif.Core.Export;
using ScreenToGif.Domain.Enums;

namespace ScreenToGif.Util;

/// <summary>
/// Forwards the progress of one export to the EncodingManager entry with the given id.
/// </summary>
internal class EncodingReporter(int id) : IEncodingReporter
{
    public void Update(int currentFrame, string text) => EncodingManager.Update(id, currentFrame, text);

    public void Update(int currentFrame) => EncodingManager.Update(id, currentFrame);

    public void Update(int type, TimeSpan elapsed) => EncodingManager.Update(id, type, elapsed);

    public void Update(string text, bool isIndeterminate = false, bool findText = false) => EncodingManager.Update(id, text, isIndeterminate, findText);

    public void Update(EncodingStatus status, string fileName = null, bool isIndeterminate = false, Exception exception = null, int? maxSteps = null) => EncodingManager.Update(id, status, fileName, isIndeterminate, exception, maxSteps);

    public void UpdateMultiple(EncodingStatus status, List<string> fileNames, bool isIndeterminate = false) => EncodingManager.UpdateMultiple(id, status, fileNames, isIndeterminate);

    public EncodingStatus? GetStatus() => EncodingManager.GetStatus(id);
}

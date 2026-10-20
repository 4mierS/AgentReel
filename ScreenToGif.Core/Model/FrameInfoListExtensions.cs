using ScreenToGif.Domain.Interfaces;
using ScreenToGif.Model;

namespace ScreenToGif.Util;

public static class FrameInfoListExtensions
{
    public static List<FrameInfo> CopyList(this List<FrameInfo> target)
    {
        return new List<FrameInfo>(target.Select(s => new FrameInfo(s.Path, s.Delay, s.CursorX, s.CursorY, s.ButtonClicked,
            s.KeyList != null ? new List<IKeyGesture>(s.KeyList.Select(y => new SimpleKeyGesture(y.Key, y.Modifiers, y.IsUppercase, y.IsInjected))) : null, s.Index)));
    }
}

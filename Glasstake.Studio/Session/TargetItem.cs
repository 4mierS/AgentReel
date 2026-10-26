using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Glasstake.Capture;
using Glasstake.Studio.Localization;

namespace Glasstake.Studio.Session;

/// <summary>
/// One card in the target list. The snapshot and the icon arrive later, from a background thread.
/// </summary>
public sealed class TargetItem(CaptureTarget target, int number) : INotifyPropertyChanged
{
    private ImageSource? _snapshot;
    private ImageSource? _icon;
    private bool _isSelected;

    public CaptureTarget Target { get; } = target;

    public bool IsWindow => Target.Kind == CaptureTargetKind.Window;

    public string Title => IsWindow ? Target.Name : Text.Format(Target.IsPrimary ? Strings.SessionMainScreen : Strings.SessionScreen, number);

    public string Detail { get; } = target.Kind == CaptureTargetKind.Window
        ? $"{ProcessName(target.Handle)} · {target.Bounds.Width} × {target.Bounds.Height}"
        : $"{target.Bounds.Width} × {target.Bounds.Height}";

    /// <summary>
    /// Screens show no snapshot (it would show everything on them, private things included), only their shape.
    /// </summary>
    public double ShapeWidth => Math.Min(ShapeBox.Width, ShapeBox.Height * Ratio);

    public double ShapeHeight => Math.Min(ShapeBox.Height, ShapeBox.Width / Ratio);

    private static readonly Size ShapeBox = new(150, 84);

    private double Ratio => Target.Bounds.Height > 0 ? (double)Target.Bounds.Width / Target.Bounds.Height : 16 / 9d;

    public Visibility PrivacyHint => Target.IsPrimary ? Visibility.Visible : Visibility.Collapsed;

    public ImageSource? Snapshot
    {
        get => _snapshot;
        set => Set(ref _snapshot, value, nameof(Snapshot));
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value, nameof(Icon));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value, nameof(IsSelected));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            using var process = Process.GetProcessById(Native.ProcessIdOf(hwnd));
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Strings.SessionWindow;
        }
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

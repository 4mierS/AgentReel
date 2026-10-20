using System.Windows;

namespace AgentReel.Mcp;

/// <summary>
/// UserSettings and LocalizationHelper read their defaults and strings through Application.Current,
/// so the server needs an Application with the app's resource dictionaries. It never shows a window
/// and never runs a Dispatcher loop.
/// </summary>
internal static class WpfResources
{
    private static readonly string[] Dictionaries =
    [
        "Resources/Settings.xaml",
        "Resources/Localization/StringResources.en.xaml"
    ];

    public static void Initialize()
    {
        if (Application.Current == null)
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        //UserSettings expects a Source on every merged dictionary.
        foreach (var resource in Dictionaries)
            Application.Current!.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenToGif;component/" + resource) });
    }
}

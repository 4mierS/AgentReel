using System.IO;
using System.Windows;
using AgentReel.Studio.Review;
using AgentReel.Studio.Session;

namespace AgentReel.Studio;

/// <summary>
/// Command line: no arguments opens the session window (pick a target, record).
/// <c>AgentReel.Studio review &lt;edit folder&gt;</c> shows an edit next to its source.
/// </summary>
public partial class App
{
    private static readonly string[] SharedDictionaries =
    [
        "Resources/Settings.xaml",
        "Resources/Localization/StringResources.en.xaml"
    ];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["review", var folder] && Directory.Exists(folder))
        {
            try
            {
                new ReviewWindow(ReviewData.Load(folder)).Show();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, "AgentReel Studio", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        if (e.Args.Length == 0)
        {
            LoadSharedResources();
            new SessionWindow().Show();
            return;
        }

        MessageBox.Show("Usage: AgentReel.Studio [review <edit folder>]", "AgentReel Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(2);
    }

    /// <summary>
    /// UserSettings (the recording folder) reads its defaults through Application.Current. These dictionaries hold
    /// only values and strings, no styles, so they do not change how Studio looks.
    /// </summary>
    private void LoadSharedResources()
    {
        foreach (var resource in SharedDictionaries)
            Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/ScreenToGif;component/" + resource) });
    }
}

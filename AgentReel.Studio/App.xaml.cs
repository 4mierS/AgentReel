using System.IO;
using System.Windows;
using AgentReel.Studio.Review;

namespace AgentReel.Studio;

/// <summary>
/// Command line: <c>AgentReel.Studio review &lt;edit folder&gt;</c> shows an edit next to its source.
/// </summary>
public partial class App
{
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

        MessageBox.Show("Usage: AgentReel.Studio review <edit folder>", "AgentReel Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(2);
    }
}

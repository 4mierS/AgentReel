using System.IO;
using System.Windows;
using AgentReel.Editing;
using AgentReel.Studio.Editor;
using AgentReel.Studio.Review;
using AgentReel.Studio.Session;

namespace AgentReel.Studio;

/// <summary>
/// Command line: no arguments opens the studio (library and editor), or the session window (pick a target, record)
/// when there is no recording yet. <c>AgentReel.Studio edit &lt;recording folder&gt;</c> opens that recording in the studio.
/// <c>AgentReel.Studio review &lt;edit folder&gt;</c> shows an edit next to its source.
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

        //Recordings of earlier versions, copied once.
        LegacyImport.RunOnce();

        if (e.Args is ["edit", var recording])
        {
            if (RecordingLibrary.Load(recording) is { } project)
            {
                EditorWindow.Open(project);
                return;
            }

            MessageBox.Show($"There is no recording with frames in {recording}.", "AgentReel Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        if (e.Args.Length == 0)
        {
            if (RecordingLibrary.Scan().Any())
                EditorWindow.Open();
            else
                new SessionWindow().Show();

            return;
        }

        MessageBox.Show("Usage: AgentReel.Studio [edit <recording folder> | review <edit folder>]", "AgentReel Studio", MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(2);
    }
}

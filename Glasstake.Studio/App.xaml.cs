using System.IO;
using System.Windows;
using Glasstake.Core;
using Glasstake.Direct;
using Glasstake.Editing;
using Glasstake.Studio.Director;
using Glasstake.Studio.Editor;
using Glasstake.Studio.Localization;
using Glasstake.Studio.Review;
using Glasstake.Studio.Session;

namespace Glasstake.Studio;

/// <summary>
/// Command line: no arguments opens the studio (library and editor), or the session window (pick a target, record)
/// when there is no recording yet. <c>Glasstake.Studio edit &lt;recording folder&gt;</c> opens that recording in the studio.
/// <c>Glasstake.Studio review &lt;edit folder&gt;</c> shows an edit next to its source.
/// <c>Glasstake.Studio approve-shots &lt;shot list folder&gt;</c> shows an agent's draft of a shot list for approval.
/// </summary>
public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Text.Apply(DataFolders.Variable("GLASSTAKE_LANGUAGE") is { Length: > 0 } language ? language : AppSettings.Current.Language);

        if (e.Args is ["review", var folder] && Directory.Exists(folder))
        {
            try
            {
                new ReviewWindow(ReviewData.Load(folder)).Show();
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                MessageBox.Show(ex.Message, Strings.AppName,MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
        }

        if (e.Args is ["approve-shots", var shots] && ShotDraft.Load(shots) is { } draft)
        {
            new ShotApprovalWindow(shots, draft).Show();
            return;
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

            MessageBox.Show(Text.Format(Strings.AppNoRecording, recording), Strings.AppName,MessageBoxButton.OK, MessageBoxImage.Error);
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

        MessageBox.Show(Strings.AppUsage, Strings.AppName,MessageBoxButton.OK, MessageBoxImage.Information);
        Shutdown(2);
    }
}

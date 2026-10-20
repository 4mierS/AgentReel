using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using KGySoft.CoreLibraries;
using KGySoft.Drawing;
using KGySoft.Drawing.Imaging;
using KGySoft.Threading;

using ScreenToGif.Cloud;
using ScreenToGif.Controls;
using ScreenToGif.Domain.Enums;
using ScreenToGif.Domain.Interfaces;
using ScreenToGif.Core.Export;
using ScreenToGif.Domain.Models;
using ScreenToGif.ImageUtil;
using ScreenToGif.Util.Codification.Apng;
using ScreenToGif.Util.Codification.Gif.Encoder;
using ScreenToGif.Util.Codification.Psd;
using ScreenToGif.Util.Extensions;
using ScreenToGif.Util.Settings;
using ScreenToGif.ViewModel;
using ScreenToGif.ViewModel.ExportPresets;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Apng;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Gif;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Webp;
using ScreenToGif.ViewModel.ExportPresets.Image;
using ScreenToGif.ViewModel.ExportPresets.Other;
using ScreenToGif.ViewModel.ExportPresets.Video;
using ScreenToGif.ViewModel.UploadPresets;
using ScreenToGif.Windows.Other;

using Color = System.Windows.Media.Color;
using Encoder = ScreenToGif.Windows.Other.Encoder;
using LegacyGifEncoder = ScreenToGif.Util.Codification.Gif.LegacyEncoder.GifEncoder;
using KGySoftGifEncoder = KGySoft.Drawing.Imaging.GifEncoder;
using ScreenToGif.ViewModel.ExportPresets.AnimatedImage.Avif;

namespace ScreenToGif.Util;

internal class EncodingManager
{
    #region Variables

    public static List<EncodingItem> Encodings { get; set; } = new List<EncodingItem>();

    /// <summary>
    /// List of CancellationTokenSource, used to cancel each task.
    /// </summary>
    private static readonly List<CancellationTokenSource> CancellationTokenList = new List<CancellationTokenSource>();

    /// <summary>
    /// List of Tasks, each task executes the encoding process for one recording.
    /// </summary>
    private static readonly List<Task> TaskList = new List<Task>();

    /// <summary>
    /// List of encoding views, used to update the data without lagging the UI.
    /// </summary>
    internal static readonly List<EncoderListViewItem> ViewList = new List<EncoderListViewItem>();

    /// <summary>
    /// Raised on the encoding thread whenever an encoding item changes. The flag tells if its status changed.
    /// The app marshals it to the UI thread, so the encoding itself never touches the Dispatcher.
    /// </summary>
    public static event Action<EncodingItem, bool> EncodingChanged;

    /// <summary>
    /// Runs work that needs the UI thread, such as copying to the clipboard. The app sets it to Dispatcher.Invoke.
    /// </summary>
    public static Action<Action> InvokeOnUiThread { get; set; } = action => action();

    #endregion

    private static void OnEncodingChanged(EncodingItem item, bool statusUpdated = false)
    {
        EncodingChanged?.Invoke(item, statusUpdated);
    }

    internal static void StartEncoding(ExportProject project, ExportPreset preset)
    {
        //If the user still wants an encoder window, here's when it should be opened.
        if (UserSettings.All.DisplayEncoder)
            Application.Current.Dispatcher?.Invoke(() => Encoder.Start(preset.Scale));

        //Creates the Cancellation Token
        var cancellationTokenSource = new CancellationTokenSource();
        CancellationTokenList.Add(cancellationTokenSource);

        var context = Application.Current.Dispatcher?.Invoke(TaskScheduler.FromCurrentSynchronizationContext);

        //Creates Task and send the Task Id.
        var taskId = -1;
        var task = new Task(async () =>
        {
            //ReSharper disable once AccessToModifiedClosure
            await Encode(project, preset, taskId, cancellationTokenSource);

        }, cancellationTokenSource.Token, TaskCreationOptions.LongRunning);
        taskId = task.Id;

        #region Error handling

        task.ContinueWith(t =>
        {
            var aggregateException = t.Exception;
            aggregateException?.Handle(exception => true);

            Update(taskId, EncodingStatus.Error, null, false, t.Exception);
            LogWriter.Log(t.Exception, "Encoding error.");

        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, context);

        #endregion

        //Adds the encoding to the list.
        Encodings.Add(new EncodingItem
        {
            Id = taskId,
            OutputType = preset.Type,
            Text = LocalizationHelper.Get("S.Encoder.Starting"),
            FrameCount = project.FrameCount,
            TokenSource = cancellationTokenSource
        });

        try
        {
            TaskList.Add(task);
            task.Start();
        }
        catch (Exception ex)
        {
            ErrorDialog.Ok("Task Error", "Unable to start the encoding task", "A generic error occurred while trying to start the encoding task.", ex);
            LogWriter.Log(ex, "Error while starting the task.");
        }

        //Application.Current.Dispatcher.Invoke(() => Refresh(taskId));
        Application.Current.Dispatcher?.Invoke(() => EncodingAdded(taskId));
    }

    #region Encoding manipulation

    internal static void Update(int id, int currentFrame, string text)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        item.CurrentFrame = currentFrame;
        item.Text = text;
        item.IsIndeterminate = false;

        OnEncodingChanged(item);
    }

    internal static void Update(int id, int currentFrame)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        item.CurrentFrame = currentFrame;

        OnEncodingChanged(item);
    }

    internal static void Update(int id, int type, TimeSpan elapsed)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        switch (type)
        {
            case 0: //Analysis.
                item.TimeToAnalyze = elapsed;
                break;
            case 1: //Encoding.
                item.TimeToEncode = elapsed;
                break;
            case 2: //Upload.
                item.TimeToUpload = elapsed;
                break;
            case 3: //Copy.
                item.TimeToCopy = elapsed;
                break;
            case 4: //Execute commands.
                item.TimeToExecute = elapsed;
                break;
        }

        OnEncodingChanged(item);
    }

    internal static void Update(int id, string text, bool isIndeterminate = false, bool findText = false)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        item.Text = !findText ? text : LocalizationHelper.Get(text);
        item.IsIndeterminate = isIndeterminate;

        OnEncodingChanged(item);
    }

    /// <summary>
    /// Updates the status of the encoding of a current item.
    /// </summary>
    /// <param name="status">The current status.</param>
    /// <param name="id">The unique ID of the item.</param>
    /// <param name="fileName">The name of the output file.</param>
    /// <param name="isIndeterminate">The state of the progress bar.</param>
    /// <param name="exception">The exception details of the error.</param>
    /// <param name="maxSteps">Optionally sets the maximum steps of the processing if (<paramref name="status"/> is <see cref="EncodingStatus.Processing"/>).</param>
    internal static void Update(int id, EncodingStatus status, string fileName = null, bool isIndeterminate = false, Exception exception = null, int? maxSteps = null)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        var wasStatusUpdated = item.Status != status;

        item.Status = status;
        item.IsIndeterminate = isIndeterminate;

        switch (status)
        {
            case EncodingStatus.Processing:
                item.Text = String.Format(LocalizationHelper.Get("S.Encoder.Processing"), fileName == null ? String.Empty : Path.GetFileName(fileName));
                if (maxSteps.HasValue)
                    item.FrameCount = maxSteps.Value;
                break;
            case EncodingStatus.Completed:
            {
                if (fileName == null)
                    break;

                if (File.Exists(fileName))
                {
                    var fileInfo = new FileInfo(fileName);
                    fileInfo.Refresh();

                    item.SizeInBytes = fileInfo.Length;
                    item.OutputFilename = fileName;
                    item.SavedToDisk = true;
                }

                break;
            }
            case EncodingStatus.Error:
            {
                item.Exception = exception;
                break;
            }
        }

        OnEncodingChanged(item, wasStatusUpdated);
    }

    internal static void UpdateMultiple(int id, EncodingStatus status, List<string> fileNames, bool isIndeterminate = false)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        var wasStatusUpdated = item.Status != status;

        item.Status = status;
        item.IsIndeterminate = isIndeterminate;
        item.AreMultipleFiles = true;

        if (status == EncodingStatus.Completed && fileNames?.Any() == true)
        {
            foreach (var fileInfo in fileNames.Where(File.Exists).Select(fileName => new FileInfo(fileName)))
            {
                fileInfo.Refresh();

                item.SizeInBytes += fileInfo.Length;
            }

            if (item.SizeInBytes > 0)
            {
                item.OutputFilename = fileNames.FirstOrDefault();
                item.SavedToDisk = true;
            }
        }

        OnEncodingChanged(item, wasStatusUpdated);
    }


    private static void SetUpload(int id, bool uploaded, string link, string deleteLink = null, Exception exception = null)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        item.Uploaded = uploaded;
        item.UploadLink = link;
        item.UploadLinkDisplay = !string.IsNullOrWhiteSpace(link) ? link.Replace("https:/", "").Replace("http:/", "").Trim('/') : link;
        item.DeletionLink = deleteLink;
        item.UploadTaskException = exception;

        OnEncodingChanged(item);
    }

    private static void SetCopy(int id, bool copied, Exception exception = null)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        item.CopiedToClipboard = copied;
        item.CopyTaskException = exception;

        OnEncodingChanged(item);
    }

    private static void SetCommand(int id, bool executed, string command, string output, Exception exception = null)
    {
        var item = Encodings.FirstOrDefault(x => x.Id == id);

        if (item == null)
            return;

        item.CommandExecuted = executed;
        item.Command = command;
        item.CommandOutput = output;
        item.CommandTaskException = exception;

        OnEncodingChanged(item);
    }


    /// <summary>
    /// Gets the current status of the encoding of a current item.
    /// </summary>
    /// <param name="id">The unique ID of the item.</param>
    internal static EncodingStatus? GetStatus(int id)
    {
        return Encodings.FirstOrDefault(f => f.Id == id)?.Status;
    }

    private static string GetUploadLink(int id)
    {
        return Encodings.FirstOrDefault(f => f.Id == id)?.UploadLink;
    }


    internal static void RemoveEncodings(int id)
    {
        RemoveEncodings(e => e.Id == id);
    }

    internal static void RemoveFinishedEncodings()
    {
        RemoveEncodings(p => p.Status != EncodingStatus.Processing);
    }

    internal static void RemoveEncodings(Predicate<EncodingItem> match)
    {
        var list = Encodings.Where(match.Invoke).ToList();

        foreach (var encoding in list)
        {
            Encodings.Remove(encoding);

            TaskList.FirstOrDefault(f => f.Id == encoding.Id)?.Dispose();
            encoding.TokenSource.Dispose();

            TaskList.RemoveAll(r => r.Id == encoding.Id);
            CancellationTokenList.Remove(encoding.TokenSource);

            EncodingRemoved(encoding.Id);
        }
    }

    internal static void StopAllEncodings()
    {
        foreach (var tokenSource in CancellationTokenList)
            tokenSource.Cancel();
    }

    #endregion

    #region Progress report

    internal static void EncodingAdded(int id)
    {
        foreach (var window in Application.Current.Windows.OfType<IEncoding>())
        {
            //When set to display the encodings on a separated window, ignore the others (vice-versa).
            if (!UserSettings.All.DisplayEncoder == window.IsEncoderWindow && Encoder.IsAvailable)
                continue;

            var view = window.EncodingAdded(id);

            if (view != null)
                ViewList.Add(view);
        }
    }

    internal static void EncodingUpdated(EncodingItem current, bool statusUpdated = false)
    {
        foreach (var item in ViewList.Where(w => w.Id == current.Id))
        {
            item.Status = current.Status;
            item.FrameCount = current.FrameCount;
            item.CurrentFrame = current.CurrentFrame;
            item.Text = current.Text;
            item.IsIndeterminate = current.IsIndeterminate;
            item.SizeInBytes = current.SizeInBytes;
            item.OutputFilename = current.OutputFilename;
            item.SavedToDisk = current.SavedToDisk;
            item.AreMultipleFiles = current.AreMultipleFiles;
            item.Exception = current.Exception;

            item.Uploaded = current.Uploaded;
            item.UploadLink = current.UploadLink;
            item.UploadLinkDisplay = current.UploadLinkDisplay;
            item.DeletionLink = current.DeletionLink;
            item.UploadTaskException = current.UploadTaskException;

            item.CopiedToClipboard = current.CopiedToClipboard;
            item.CopyTaskException = current.CopyTaskException;

            item.CommandExecuted = current.CommandExecuted;
            item.Command = current.Command;
            item.CommandOutput = current.CommandOutput;
            item.CommandTaskException = current.CommandTaskException;

            item.TimeToAnalyze = current.TimeToAnalyze;
            item.TimeToEncode = current.TimeToEncode;
            item.TimeToUpload = current.TimeToUpload;
            item.TimeToCopy = current.TimeToCopy;
            item.TimeToExecute = current.TimeToExecute;
        }

        if (!statusUpdated)
            return;

        foreach (var window in Application.Current.Windows.OfType<IEncoding>())
        {
            //When set to display the encodings on a separated window, ignore the others (vice-versa).
            if (!UserSettings.All.DisplayEncoder == window.IsEncoderWindow && Encoder.IsAvailable)
                continue;

            window.EncodingUpdated(current.Id, true);
        }
    }

    internal static void EncodingRemoved(int id)
    {
        foreach (var window in Application.Current.Windows.OfType<IEncoding>())
        {
            var view = window.EncodingRemoved(id);

            if (view != null)
                ViewList.Remove(view);
        }
    }

    internal static void MoveEncodingsToPopups()
    {
        foreach (var window in Application.Current.Windows.OfType<IEncoding>())
        {
            //Only send this message to editors.
            if (window.IsEncoderWindow)
                continue;

            window.EncodingUpdated(null, false);
        }
    }

    #endregion

    #region Encoding

    private static async Task Encode(ExportProject project, ExportPreset preset, int id, CancellationTokenSource tokenSource)
    {
        var watch = new Stopwatch();
        watch.Start();

        try
        {
            if (!await ExportService.EncodeAsync(project, preset, new EncodingReporter(id), tokenSource))
                return;

            watch.Restart();

            #region Upload

            if (preset.UploadFile && File.Exists(preset.FullPath))
            {
                Update(id, "S.Encoder.Uploading", true, true);

                try
                {
                    //Get selected preset.
                    var presetType = preset.Extension == ".zip" ? ExportFormats.Zip : preset.Type;
                    var uploadPreset = UserSettings.All.UploadPresets.OfType<UploadPreset>().FirstOrDefault(f => (f.AllowedTypes.Count == 0 || f.AllowedTypes.Contains(presetType)) && f.Title == preset.UploadService);

                    if (uploadPreset == null)
                        throw new Exception($"Missing upload preset called {preset.UploadService}");

                    //TODO: Limit upload by imposed service limits.

                    //Try uploading to the selected service.
                    var cloud = CloudFactory.CreateCloud(uploadPreset.Type);
                    var history = await cloud.UploadFileAsync(uploadPreset, preset.FullPath, CancellationToken.None);

                    uploadPreset.History.Add(history);
                    UserSettings.Save();

                    if (history.Result != 200)
                        throw new Exception(history.Message);

                    SetUpload(id, true, history.GetLink(uploadPreset), history.DeletionLink);
                }
                catch (Exception e)
                {
                    LogWriter.Log(e, "It was not possible to upload.");
                    SetUpload(id, false, null, null, e);
                }
                finally
                {
                    Update(id, 2, watch.Elapsed);
                    watch.Restart();
                }
            }

            #endregion

            #region Copy to clipboard

            if (preset.SaveToClipboard && File.Exists(preset.FullPath))
            {
                InvokeOnUiThread(() =>
                {
                    try
                    {
                        var data = new DataObject();

                        switch (preset.CopyType)
                        {
                            case CopyModes.File:
                                //data.SetData(DataFormats.GetDataFormat("GIF").Name, System.Drawing.Image.FromFile(param.Filename));
                                data.SetFileDropList(new StringCollection { preset.FullPath });
                                break;
                            case CopyModes.FolderPath:
                                data.SetText(Path.GetDirectoryName(preset.FullPath) ?? preset.FullPath, TextDataFormat.UnicodeText);
                                break;
                            case CopyModes.Link:
                                var link = GetUploadLink(id);

                                data.SetText(string.IsNullOrEmpty(link) ? preset.FullPath : link, TextDataFormat.UnicodeText);
                                break;
                            default:
                                data.SetText(preset.FullPath, TextDataFormat.UnicodeText);
                                break;
                        }

                        //It tries to set the data to the clipboard 10 times before failing it to do so.
                        //This issue may happen if the clipboard is opened by any clipboard manager.
                        for (var i = 0; i < 10; i++)
                        {
                            try
                            {
                                System.Windows.Clipboard.SetDataObject(data, true);
                                break;
                            }
                            catch (COMException ex)
                            {
                                if ((uint)ex.ErrorCode != 0x800401D0) //CLIPBRD_E_CANT_OPEN
                                    throw;
                            }

                            Thread.Sleep(100);
                        }

                        SetCopy(id, true);
                    }
                    catch (Exception e)
                    {
                        LogWriter.Log(e, "It was not possible to copy the file.");
                        SetCopy(id, false, e);
                    }
                });

                Update(id, 3, watch.Elapsed);
                watch.Restart();
            }

            #endregion

            #region Execute commands

#if !FULL_MULTI_MSIX_STORE

            if (preset.ExecuteCustomCommands && !string.IsNullOrWhiteSpace(preset.CustomCommands))
            {
                Update(id, "S.Encoder.Executing", true, true);

                var command = preset.CustomCommands.Replace("{p}", "\"" + preset.FullPath + "\"").Replace("{f}", "\"" + Path.GetDirectoryName(preset.OutputFolder) + "\"").Replace("{u}", "\"" + GetUploadLink(id) + "\"");
                var output = "";

                try
                {
                    foreach (var com in command.Split(['\n'], StringSplitOptions.RemoveEmptyEntries))
                    {
                        var procStartInfo = new ProcessStartInfo("cmd", "/c " + com)
                        {
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using (var process = new Process())
                        {
                            process.StartInfo = procStartInfo;
                            process.Start();

                            var message = await process.StandardOutput.ReadToEndAsync();
                            var error = await process.StandardError.ReadToEndAsync();

                            if (!string.IsNullOrWhiteSpace(message))
                                output += message + Environment.NewLine;

                            if (!string.IsNullOrWhiteSpace(error))
                            {
                                output += error + Environment.NewLine;
                                throw new Exception(output);
                            }

                            process.WaitForExit(1000);
                        }
                    }

                    SetCommand(id, true, command, output);
                }
                catch (Exception e)
                {
                    LogWriter.Log(e, "It was not possible to run the post encoding command.");
                    SetCommand(id, false, command, output, e);
                }
                finally
                {
                    Update(id, 4, watch.Elapsed);
                    watch.Restart();
                }
            }

#endif

            #endregion

            if (!tokenSource.Token.IsCancellationRequested)
                Update(id, EncodingStatus.Completed, preset.FullPath);
        }
        catch (Exception ex)
        {
            LogWriter.Log(ex, "Encode");

            Update(id, EncodingStatus.Error, null, false, ex);
        }
        finally
        {
            watch.Stop();

            #region Delete the encoder folder

            try
            {
                var folder = Path.GetDirectoryName(project.UsesFiles ? project.FramesFiles[0].Path : project.ChunkPath);

                if (!string.IsNullOrEmpty(folder))
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
            }
            catch (Exception ex)
            {
                LogWriter.Log(ex, "Cleaning the encoder folder");
            }

            #endregion

            GC.Collect();
        }
    }

    #endregion
}


interface IEncoding
{
    bool IsEncoderWindow { get; }

    EncoderListViewItem EncodingAdded(int id);
    void EncodingUpdated(int? id = null, bool onlyStatus = false);
    EncoderListViewItem EncodingRemoved(int id);
}

internal class EncodingItem
{
    public int Id { get; set; }

    public ExportFormats OutputType { get; set; }

    public EncodingStatus Status { get; set;}

    public string Text { get; set; }

    public int FrameCount { get; set; }

    public int CurrentFrame { get; set; }

    public bool IsIndeterminate { get; set; }

    public long SizeInBytes { get; set; }

    public string OutputFilename { get; set; }

    public bool SavedToDisk { get; set; }

    public bool AreMultipleFiles { get; set; }


    public bool CopiedToClipboard { get; set; }

    public Exception CopyTaskException { get; set; }


    public bool CommandExecuted { get; set; }

    public string Command { get; set; }

    public string CommandOutput { get; set; }

    public Exception CommandTaskException { get; set; }


    public bool Uploaded { get; set; }

    public string UploadLink { get; set; }

    public string UploadLinkDisplay { get; set; }

    public string DeletionLink { get; set; }

    public Exception UploadTaskException { get; set; }

    public Exception Exception { get; set; }

    public CancellationTokenSource TokenSource { get; set; }


    public TimeSpan TimeToAnalyze { get; set; }

    public TimeSpan TimeToEncode { get; set; }

    public TimeSpan TimeToUpload { get; set; }

    public TimeSpan TimeToCopy { get; set; }

    public TimeSpan TimeToExecute { get; set; }
}
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Typedown.Core.Controls;
using Typedown.Core.Interfaces;
using Typedown.Core.Models;
using Typedown.Core.Services;
using Typedown.Core.Utilities;
using Windows.ApplicationModel.Core;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Typedown.Core.ViewModels
{
    public sealed partial class FileViewModel : INotifyPropertyChanged, IDisposable
    {
        public IServiceProvider ServiceProvider { get; }

        public AppViewModel AppViewModel => ServiceProvider.GetService<AppViewModel>();

        public SettingsViewModel SettingsViewModel => ServiceProvider.GetService<SettingsViewModel>();

        public EditorViewModel EditorViewModel => ServiceProvider.GetService<EditorViewModel>();

        public EventCenter EventCenter => ServiceProvider.GetService<EventCenter>();

        public RemoteInvoke RemoteInvoke => ServiceProvider.GetService<RemoteInvoke>();

        public AccessHistory AccessHistory => ServiceProvider.GetService<AccessHistory>();

        public string WorkFolder { get; private set; } = null;

        public string FilePath { get; private set; } = null;

        public string ImageBasePath => string.IsNullOrEmpty(FilePath) ? SettingsViewModel.DefaultImageBasePath : Path.GetDirectoryName(FilePath);

        public string FileName => Path.GetFileName(FilePath);

        public Command<Unit> NewFileCommand { get; } = new();
        public Command<string> NewWindowCommand { get; } = new();
        public Command<string> OpenFileCommand { get; } = new();
        public Command<string> OpenFolderCommand { get; } = new();
        public Command<Unit> NewFolderCommand { get; } = new();
        public Command<Unit> ClearHistoryCommand { get; } = new();
        public Command<Unit> SaveCommand { get; } = new();
        public Command<Unit> SaveAsCommand { get; } = new();
        public Command<Unit> ImportCommand { get; } = new();
        public Command<ExportConfig> ExportCommand { get; } = new();
        public Command<Unit> PrintCommand { get; } = new();
        public Command<Unit> ExitCommand { get; } = new();

        private readonly DispatcherTimer saveFileTimer = new();

        public AutoBackup AutoBackup => ServiceProvider.GetService<AutoBackup>();

        public IMarkdownEditor MarkdownEditor => ServiceProvider.GetService<IMarkdownEditor>();

        private readonly CompositeDisposable disposables = new();

        private FileChangeWatcher fileChangeWatcher;

        // Hash of the file content as last read from or written to disk. Differs from
        // EditorViewModel.FileHash, which is based on the (normalized) editor content.
        private ulong diskHash;

        // Disk content hash for which the conflict dialog was already shown, to ask only once per external change
        private ulong promptedDiskHash;

        private bool checkingExternalChange;

        private bool conflictDialogOpened;

        private bool IsWindowActive => AppViewModel.MainWindow != default && PInvoke.GetForegroundWindow() == AppViewModel.MainWindow;

        public FileViewModel(IServiceProvider serviceProvider)
        {
            ServiceProvider = serviceProvider;
            NewFileCommand.OnExecute.Subscribe(async _ => await NewFileFun());
            OpenFileCommand.OnExecute.Subscribe(async x => await OpenFile(x));
            OpenFolderCommand.OnExecute.Subscribe(async x => await OpenFolder(x));
            SaveAsCommand.OnExecute.Subscribe(async _ => await SaveAs());
            SaveCommand.OnExecute.Subscribe(async _ => await Save());
            ExitCommand.OnExecute.Subscribe(_ => Exit());
            ClearHistoryCommand.OnExecute.Subscribe(x => { _ = AccessHistory.ClearHistory(); });
            ExportCommand.OnExecute.Subscribe(Export);
            PrintCommand.OnExecute.Subscribe(_ => Print());
            ImportCommand.OnExecute.Subscribe(_ => Import());
            RemoteInvoke.Handle<JToken, bool>("ExportCallback", ExportCallback);
            RemoteInvoke.Handle<JToken, bool>("PrintHTML", PrintHTML);
            saveFileTimer.Interval = TimeSpan.FromSeconds(5);
            saveFileTimer.Tick += SaveFileTimerTick;
            saveFileTimer.Start();
            this.WhenPropertyChanged(nameof(FilePath)).Subscribe(_ => RestartFileChangeWatcher());
            disposables.Add(ServiceProvider.GetService<IWindowService>().WindowIsActivedChanged.Subscribe(OnWindowIsActivedChanged));
            _ = CoreApplication.GetCurrentView().CoreWindow.Dispatcher.RunIdleAsync(() => OnStartup());
        }

        private async void SaveFileTimerTick(object sender, object e)
        {
            if (disposables.IsDisposed)
            {
                return;
            }
            if (SettingsViewModel.AutoSave)
            {
                EditorViewModel.AutoSavedSucc = await AutoSaveFile();
                if (!EditorViewModel.AutoSavedSucc)
                    await AutoBackupFile();
            }
            else
            {
                await AutoBackupFile();
            }
        }

        public async Task<bool> AutoSaveFile()
        {
            try
            {
                if (SettingsViewModel.AutoSave && EditorViewModel.FileLoaded && (EditorViewModel.FileHash != EditorViewModel.CurrentHash) && FilePath != null)
                    return await Save(false);
                return FilePath != null;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> AutoBackupFile()
        {
            if (EditorViewModel.FileHash != EditorViewModel.CurrentHash && !string.IsNullOrWhiteSpace(EditorViewModel.Markdown))
                return await AutoBackup.Backup(FilePath, EditorViewModel.Markdown);
            else
                AutoBackup.DeleteBackup(FilePath);
            return true;
        }

        private async Task NewFileFun(bool postMessage = true)
        {
            if (!await AskToSave()) return;
            FilePath = null;
            EditorViewModel.ExternalChangePending = false;
            EditorViewModel.FileHash = Common.SimpleHash(Common.DefaultMarkdwn);
            string backup = null;
            if (AppViewModel.GetInstances().Where(x => x != AppViewModel).All(x => !string.IsNullOrEmpty(x.FileViewModel.FilePath)))
            {
                backup = await CheckBackup(FilePath, EditorViewModel.FileHash);
            }
            if (backup == null)
            {
                EditorViewModel.Markdown = Common.DefaultMarkdwn;
                EditorViewModel.CurrentHash = EditorViewModel.FileHash;
                EditorViewModel.Saved = true;
                EditorViewModel.AutoSavedSucc = true;
                EditorViewModel.FileLoaded = false;
            }
            else
            {
                EditorViewModel.Markdown = backup;
                EditorViewModel.CurrentHash = Common.SimpleHash(backup);
                EditorViewModel.Saved = false;
                EditorViewModel.AutoSavedSucc = false;
                EditorViewModel.FileLoaded = true;
            }
            EditorViewModel.History.InitHistory(Common.DefaultMarkdwn);
            if (postMessage)
            {
                MarkdownEditor?.PostMessage("LoadFile", EditorViewModel.Markdown);
            }
        }

        public async Task<bool> OpenFile(string filePath = null)
        {
            if (!await AskToSave())
                return false;
            filePath ??= await AppViewModel.MainWindow.PickMarkdownFileAsync();
            if (filePath == null)
                return false;
            return await LoadFile(filePath, true);
        }

        public async Task<bool> OpenFolder(string folderPath = null)
        {
            folderPath ??= await AppViewModel.MainWindow.PickMarkdownFolderAsync();
            if (folderPath == null)
                return false;
            if (!await LoadFolder(folderPath))
                return false;
            SettingsViewModel.SidePaneOpen = true;
            SettingsViewModel.SidePaneIndex = 0;
            return true;
        }

        private async Task<bool> LoadFile(string path, bool skipSavedCheck = false, bool postMessage = true)
        {
            try
            {
                if (TryGetOpenedWindow(path, out var window) && window != AppViewModel.MainWindow)
                {
                    _ = AppViewModel.XamlRoot?.Content?.Dispatcher?.RunIdleAsync(() => PInvoke.SetForegroundWindow(window));
                    return false;
                }
                if (!File.Exists(path))
                {
                    _ = AccessHistory.RemoveFileHistory(path);
                    throw new FileNotFoundException("File does not exist.");
                }
                else if (!skipSavedCheck && !await AskToSave())
                {
                    return false;
                }
                var text = await File.ReadAllTextAsync(path);
                EditorViewModel.FirstStart = false;
                EditorViewModel.FileHash = Common.SimpleHash(text);
                EditorViewModel.ExternalChangePending = false;
                diskHash = EditorViewModel.FileHash;
                FilePath = path;
                _ = AccessHistory.RecordFileHistory(FilePath);
                var backup = await CheckBackup(path, EditorViewModel.CurrentHash);
                if (backup == null)
                {
                    EditorViewModel.Markdown = text;
                    EditorViewModel.CurrentHash = EditorViewModel.FileHash;
                    EditorViewModel.Saved = true;
                    EditorViewModel.FileLoaded = false;
                }
                else
                {
                    EditorViewModel.Markdown = backup;
                    EditorViewModel.CurrentHash = Common.SimpleHash(backup);
                    EditorViewModel.Saved = false;
                    EditorViewModel.FileLoaded = true;
                }
                EditorViewModel.AutoSavedSucc = true;
                EditorViewModel.History.InitHistory(EditorViewModel.Markdown);
                if (postMessage)
                {
                    MarkdownEditor?.PostMessage("LoadFile", new { text = EditorViewModel.Markdown, basePath = ImageBasePath });
                }
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(Locale.GetDialogString("ReadErrorTitle"), ex.Message, Locale.GetDialogString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private async Task<bool> LoadFolder(string folderPath)
        {
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    _ = AccessHistory.RemoveFolderHistory(folderPath);
                    throw new FileNotFoundException("Folder does not exist.");
                }
                WorkFolder = folderPath;
                _ = AccessHistory.RecordFolderHistory(folderPath);
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(Locale.GetString("Error"), ex.Message, Locale.GetDialogString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private async Task<string> CheckBackup(string path, ulong fileHash)
        {
            string text = await AutoBackup.GetBackup(path);
            if (text == null || Common.SimpleHash(text) == fileHash) return null;
            var dialog = AppContentDialog.Create();
            dialog.Title = Locale.GetDialogString("RecoverTitle");
            dialog.Content = Locale.GetDialogString("RecoverContent");
            dialog.PrimaryButtonText = Locale.GetDialogString("Recover");
            dialog.SecondaryButtonText = Locale.GetDialogString("Delete");
            dialog.DefaultButton = ContentDialogButton.Primary;
            var result = await dialog.ShowAsync(AppViewModel.XamlRoot);
            if (result == ContentDialogResult.Primary)
            {
                return text;
            }
            else
            {
                AutoBackup.DeleteBackup(path);
                return null;
            }
        }

        private async Task<bool> WriteAllText(string path, string text, bool alert = true)
        {
            try
            {
                await File.WriteAllTextAsync(path, text);
                return true;
            }
            catch (Exception ex)
            {
                if (alert)
                {
                    await AppContentDialog.Create(Locale.GetDialogString("SaveErrorTitle"), ex.Message, Locale.GetDialogString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                }
                return false;
            }
        }

        private async Task<bool> Save(bool alert = true, bool force = false)
        {
            if (FilePath == null)
            {
                var result = await SaveAs();
                return result != null;
            }
            else
            {
                // Never silently overwrite a file that was changed by another program
                if (!force && await ReadDiskHash(FilePath) is ulong currentDiskHash && currentDiskHash != diskHash)
                {
                    EditorViewModel.ExternalChangePending = true;
                    if (alert)
                        return await ShowConflictDialog();
                    if (currentDiskHash != promptedDiskHash && IsWindowActive)
                    {
                        promptedDiskHash = currentDiskHash;
                        _ = ShowConflictDialog();
                    }
                    return false;
                }
                var markdown = EditorViewModel.Markdown;
                var result = await WriteAllText(FilePath, markdown, alert);
                if (result)
                {
                    diskHash = Common.SimpleHash(markdown);
                    EditorViewModel.ExternalChangePending = false;
                    EditorViewModel.FileHash = EditorViewModel.CurrentHash;
                    EditorViewModel.Saved = true;
                    AutoBackup.DeleteBackup(FilePath);
                    _ = AccessHistory.RecordFileHistory(FilePath);
                }
                return result;
            }
        }

        private async Task<string> SaveAs()
        {
            try
            {
                var filePicker = new FileSavePicker();
                filePicker.SetOwnerWindow(AppViewModel.MainWindow);
                filePicker.FileTypeChoices.Add("Markdown Files", FileTypeHelper.Markdown.ToList());
                filePicker.SuggestedFileName = FileName ?? "untitled";
                var file = await filePicker.PickSaveFileAsync();
                if (file != null)
                {
                    var markdown = EditorViewModel.Markdown;
                    var result = await WriteAllText(file.Path, markdown);
                    if (result)
                    {
                        AutoBackup.DeleteBackup(FilePath);
                        diskHash = Common.SimpleHash(markdown);
                        EditorViewModel.ExternalChangePending = false;
                        FilePath = file.Path;
                        EditorViewModel.FileHash = EditorViewModel.CurrentHash;
                        EditorViewModel.Saved = true;
                        _ = AccessHistory.RecordFileHistory(FilePath);
                        return file.Path;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(Locale.GetString("Error"), ex.Message, Locale.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return null;
            }
        }

        private void RestartFileChangeWatcher()
        {
            fileChangeWatcher?.Dispose();
            fileChangeWatcher = null;
            if (FilePath == null || disposables.IsDisposed)
                return;
            try
            {
                fileChangeWatcher = new FileChangeWatcher(FilePath, () => _ = CheckExternalChange());
            }
            catch
            {
                // Watching not possible (e.g. some network paths), the check on window activation still works
            }
        }

        private void OnWindowIsActivedChanged(nint window)
        {
            if (window == AppViewModel.MainWindow && IsWindowActive)
                _ = CheckExternalChange();
        }

        private async Task CheckExternalChange()
        {
            var path = FilePath;
            if (path == null || checkingExternalChange || disposables.IsDisposed)
                return;
            checkingExternalChange = true;
            try
            {
                if (!File.Exists(path))
                {
                    // Other editors may save via delete + rename, so wait before treating the file as deleted
                    await Task.Delay(500);
                    if (!File.Exists(path) && path == FilePath && diskHash != 0)
                    {
                        // Keep the content in the editor and mark it unsaved, saving recreates the file
                        diskHash = 0;
                        EditorViewModel.FileHash = 0;
                        EditorViewModel.Saved = false;
                    }
                    return;
                }
                var text = await ReadDiskText(path);
                if (text == null || path != FilePath)
                    return;
                var hash = Common.SimpleHash(text);
                if (hash == diskHash)
                {
                    EditorViewModel.ExternalChangePending = false;
                    return;
                }
                if (EditorViewModel.Saved)
                {
                    ApplyDiskContent(text, hash);
                }
                else
                {
                    EditorViewModel.ExternalChangePending = true;
                    // Ask once per external change and only in the active window, otherwise on next activation
                    if (hash != promptedDiskHash && IsWindowActive)
                    {
                        promptedDiskHash = hash;
                        _ = ShowConflictDialog();
                    }
                }
            }
            finally
            {
                checkingExternalChange = false;
            }
        }

        private void ApplyDiskContent(string text, ulong hash)
        {
            diskHash = hash;
            promptedDiskHash = 0;
            EditorViewModel.ExternalChangePending = false;
            EditorViewModel.ReloadContent(text);
            AutoBackup.DeleteBackup(FilePath);
        }

        private async Task<bool> ReloadFromDisk()
        {
            var text = FilePath == null ? null : await ReadDiskText(FilePath);
            if (text == null)
                return false;
            ApplyDiskContent(text, Common.SimpleHash(text));
            return true;
        }

        /// <returns>True if editor and file are in sync afterwards</returns>
        private async Task<bool> ShowConflictDialog()
        {
            if (conflictDialogOpened)
                return false;
            conflictDialogOpened = true;
            ContentDialogResult result;
            try
            {
                result = await AppContentDialog.Create(
                    Locale.GetDialogString("ExternalChangeTitle"),
                    string.Format(Locale.GetDialogString("ExternalChangeContent") ?? "{0}", FileName),
                    Locale.GetDialogString("DecideLater"),
                    Locale.GetDialogString("Reload"),
                    Locale.GetDialogString("KeepMine")).ShowAsync(AppViewModel.XamlRoot);
            }
            finally
            {
                conflictDialogOpened = false;
            }
            switch (result)
            {
                case ContentDialogResult.Primary:
                    return await ReloadFromDisk();
                case ContentDialogResult.Secondary:
                    return await Save(alert: true, force: true);
                default:
                    return false;
            }
        }

        private static async Task<string> ReadDiskText(string path)
        {
            // The other program may still hold the file open, so retry a few times
            for (var i = 0; i < 5; i++)
            {
                try
                {
                    return await File.ReadAllTextAsync(path);
                }
                catch (FileNotFoundException)
                {
                    return null;
                }
                catch (DirectoryNotFoundException)
                {
                    return null;
                }
                catch (IOException)
                {
                    await Task.Delay(100);
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private static async Task<ulong?> ReadDiskHash(string path)
        {
            return await ReadDiskText(path) is string text ? Common.SimpleHash(text) : null;
        }

        private async Task<bool> PrintHTML(JToken args)
        {
            try
            {
                var html = args["html"].ToString();
                var fileExport = ServiceProvider.GetService<IFileExport>();
                await fileExport.Print(Path.GetDirectoryName(FilePath), html, FileName);
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(Locale.GetString("Error"), ex.Message, Locale.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private async Task<bool> ExportCallback(JToken args)
        {
            try
            {
                var html = args["html"].ToString();
                var filePath = args["context"]["filePath"].ToString();
                var configId = args["context"]["configId"].ToObject<int>();
                var config = await ServiceProvider.GetService<IFileExport>().GetExportConfig(configId);
                await config.LoadExportConfig().Export(ServiceProvider, html, filePath);
                if (SettingsViewModel.OpenFolderAfterExport)
                    Common.OpenFileLocation(filePath);
                return true;
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(Locale.GetString("Error"), ex.Message, Locale.GetString("Ok")).ShowAsync(AppViewModel.XamlRoot);
                return false;
            }
        }

        private bool askToSaveOpened;

        public async Task<bool> AskToSave()
        {
            if (EditorViewModel.Saved || (SettingsViewModel.AutoSave && await AutoSaveFile()))
            {
                return true;
            }
            if (askToSaveOpened)
            {
                return false;
            }
            askToSaveOpened = true;
            var result = await AppContentDialog.Create(
                Locale.GetDialogString("AsKToSaveTitle"),
                Locale.GetDialogString("AsKToSaveContent"),
                Locale.GetDialogString("Cancel"),
                Locale.GetDialogString("Save"),
                Locale.GetDialogString("Don'tSave")).ShowAsync(AppViewModel.XamlRoot);
            askToSaveOpened = false;
            switch (result)
            {
                case ContentDialogResult.Primary:
                    var saveResult = await Save();
                    return saveResult;
                case ContentDialogResult.Secondary:
                    AutoBackup.DeleteBackup(FilePath);
                    return true;
                case ContentDialogResult.None:
                    return false;
            }
            return false;
        }

        public async Task LoadStartUpMarkdown()
        {
            var path = CommandLine.GetOpenFilePath(AppViewModel.CommandLineArgs);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    await LoadFile(path, true, false);
                }
                catch (Exception)
                {
                    await NewFileFun(false);
                }
            }
            else
            {
                switch (SettingsViewModel.FileStartupAction)
                {
                    case Enums.FileStartupAction.OpenLast:
                        await AccessHistory.EnsureInitialized();
                        if (AccessHistory.FileRecentlyOpened.FirstOrDefault() is string lastFile && !TryGetOpenedWindow(lastFile, out _) && File.Exists(lastFile))
                            await LoadFile(lastFile, true);
                        break;
                    default:
                        await NewFileFun(false);
                        break;
                }
            }
        }

        private async void Export(ExportConfig config)
        {
            var filePicker = new FileSavePicker();
            filePicker.SetOwnerWindow(AppViewModel.MainWindow);
            config.FileExtensions.ForEach(x => filePicker.FileTypeChoices.Add(x.name, new List<string> { x.extension }));
            var file = await filePicker.PickSaveFileAsync();
            if (file == null) return;
            string basePath = null;
            if (config.Type == Enums.ExportType.PDF || config.Type == Enums.ExportType.Image)
                basePath = ImageBasePath;
            MarkdownEditor?.PostMessage("Export", new
            {
                type = "export",
                title = file.DisplayName,
                context = new { configId = config.Id, filePath = file.Path },
                basePath,
                options = config.LoadExportConfig()
            });
        }

        private void Print()
        {
            MarkdownEditor?.PostMessage("Export", new
            {
                type = "print",
                basePath = ImageBasePath,
                title = FileName ?? "untitled"
            });
        }

        private async void Import()
        {
            try
            {
                var filePicker = new FileOpenPicker() { FileTypeFilter = { ".html" } };
                filePicker.SetOwnerWindow(AppViewModel.MainWindow);
                var file = await filePicker.PickSingleFileAsync();
                if (file != null)
                {
                    var text = await File.ReadAllTextAsync(file.Path);
                    MarkdownEditor?.PostMessage("ImportFile", new { type = Path.GetExtension(file.Path).Substring(1), text });
                }
            }
            catch (Exception ex)
            {
                await AppContentDialog.Create(Locale.GetDialogString("ImportErrorTitle"), ex.Message, Locale.GetDialogString("Ok")).ShowAsync(AppViewModel.XamlRoot);
            }
        }

        private async void OnStartup()
        {
            try
            {
                if (string.IsNullOrEmpty(WorkFolder))
                {
                    switch (SettingsViewModel.FolderStartupAction)
                    {
                        case Enums.FolderStartupAction.OpenLast:
                            await AccessHistory.EnsureInitialized();
                            if (AccessHistory.FolderRecentlyOpened.FirstOrDefault() is string lastFolder && Directory.Exists(lastFolder))
                                await LoadFolder(lastFolder);
                            break;
                        case Enums.FolderStartupAction.OpenFolder:
                            if (Directory.Exists(SettingsViewModel.StartupOpenFolder))
                                await LoadFolder(SettingsViewModel.StartupOpenFolder);
                            break;
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        public static bool TryGetOpenedWindow(string filePath, out IntPtr window)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                window = default;
                return false;
            }
            window = AppViewModel.GetInstances().Where(x => x.FileViewModel.FilePath?.ToLower() == filePath.ToLower()).FirstOrDefault()?.MainWindow ?? default;
            return window != default;
        }

        public bool RenameFile(string to)
        {
            if (!File.Exists(FilePath))
                return false;
            var fileOperation = ServiceProvider.GetService<IFileOperation>();
            if (fileOperation.Rename(FilePath, to))
            {
                FilePath = to;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            saveFileTimer.Stop();
            fileChangeWatcher?.Dispose();
            disposables.Dispose();
        }

        private void Exit()
        {
            var SC_CLOSE = 0xF060;
            PInvoke.PostMessage(AppViewModel.MainWindow, (uint)PInvoke.WindowMessage.WM_SYSCOMMAND, (nint)SC_CLOSE, IntPtr.Zero);
        }
    }
}

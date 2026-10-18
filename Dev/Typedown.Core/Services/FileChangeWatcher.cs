using System;
using System.IO;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Windows.System;

namespace Typedown.Core.Services
{
    /// <summary>
    /// Watches a single file and reports changes on the UI thread, debounced.
    /// Covers direct writes as well as "write temp file, then rename" saves of other editors.
    /// </summary>
    public sealed class FileChangeWatcher : IDisposable
    {
        private readonly FileSystemWatcher watcher;

        private readonly Subject<Unit> changed = new();

        private readonly IDisposable subscription;

        public FileChangeWatcher(string filePath, Action onChanged)
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            subscription = changed
                .Throttle(TimeSpan.FromMilliseconds(300))
                .Subscribe(_ => dispatcherQueue.TryEnqueue(() => onChanged()));
            watcher = new(Path.GetDirectoryName(filePath), Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.EnableRaisingEvents = true;
        }

        private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => changed.OnNext(Unit.Default);

        public void Dispose()
        {
            watcher.Dispose();
            subscription.Dispose();
            changed.Dispose();
        }
    }
}

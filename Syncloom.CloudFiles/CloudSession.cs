using Syncloom.Sync;

namespace Syncloom.CloudFiles;

/// <summary>On-demand files for one signed-in account: connects the sync root, mirrors the server as placeholders and keeps both sides in step.</summary>
public sealed class CloudSession : IDisposable
{
    private readonly SyncRoot _root;
    private readonly SyncLoop _loop;
    private readonly FileSystemWatcher _watcher;

    private CloudSession(SyncRoot root, SyncLoop loop, FileSystemWatcher watcher) => (_root, _loop, _watcher) = (root, loop, watcher);

    /// <summary>The folder must already be registered as a sync root (StorageProviderSyncRootManager).</summary>
    public static CloudSession Start(string folder, IRemote remote, string device, Action<string> status)
    {
        Directory.CreateDirectory(folder);
        var fs = new CloudFs(folder);
        var engine = new SyncEngine(remote, fs, device);
        var root = new SyncRoot(folder, remote, engine, status);
        root.Connect();
        var loop = new SyncLoop(engine, remote, status);

        // a change in the folder (new file, edit, delete) is worth a push soon, not only at the next timer tick
        var watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
        };
        void Changed(object? _, FileSystemEventArgs e) { if (!SyncEngine.Ignored(Path.GetFileName(e.FullPath))) loop.Poke(); }
        watcher.Created += Changed;
        watcher.Changed += Changed;
        watcher.Renamed += (_, e) => Changed(null, e);
        watcher.EnableRaisingEvents = true;

        loop.Start();
        return new CloudSession(root, loop, watcher);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _loop.Dispose();
        _root.Dispose();
    }
}

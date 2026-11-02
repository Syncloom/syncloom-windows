namespace Syncloom.Sync;

/// <summary>
/// Keeps a sync root of placeholders in step with the server. <see cref="Pull"/> mirrors the server tree as placeholders
/// (content is fetched when a file is opened, see the Windows adapter); <see cref="Push"/> uploads what the user created
/// or changed. Deletes and renames in Explorer arrive through <see cref="LocalDeleted"/> and <see cref="LocalRenamed"/>.
/// </summary>
public sealed class SyncEngine(IRemote remote, ILocalFs fs, string device, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.Now);
    private readonly object _ownLock = new();
    private readonly HashSet<string> _own = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _stale = new(StringComparer.OrdinalIgnoreCase);

    // ---------- helpers ----------

    public static string Join(string rel, string name) => rel.Length == 0 ? name : rel + "\\" + name;

    public static (string Parent, string Name) Split(string rel)
    {
        var i = rel.LastIndexOf('\\');
        return i < 0 ? ("", rel) : (rel[..i], rel[(i + 1)..]);
    }

    /// <summary>Editor and system files that must never be uploaded.</summary>
    public static bool Ignored(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".syncloom-part", StringComparison.Ordinal)
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>`report.txt` becomes `report (conflict – laptop – 2026-10-29).txt`, the same pattern as the other clients.</summary>
    public string ConflictName(string name)
    {
        var dot = name.LastIndexOf('.');
        var (stem, ext) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        return $"{stem} (conflict – {device} – {_now():yyyy-MM-dd}){ext}";
    }

    /// <summary>True while the engine itself changes <paramref name="rel"/> (or a folder above it), so callbacks can ignore it.</summary>
    public bool IsOwn(string rel)
    {
        lock (_ownLock) return _own.Any(p => rel.Equals(p, StringComparison.OrdinalIgnoreCase) || rel.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase));
    }

    private void Own(string rel, Action work)
    {
        lock (_ownLock) _own.Add(rel);
        try { work(); } finally { lock (_ownLock) _own.Remove(rel); }
    }

    private static string Show(string rel) => rel.Length == 0 ? "(root)" : rel;

    // ---------- server -> disk ----------

    public SyncReport Pull()
    {
        var r = new SyncReport();
        lock (_stale) _stale.Clear();
        PullDir(null, "", r);
        return r;
    }

    private void PullDir(string? parentId, string rel, SyncReport r)
    {
        List<RemoteNode> nodes;
        try { nodes = remote.List(parentId).ToList(); }
        catch (Exception e) { r.Errors.Add($"{Show(rel)}: {e.Message}"); return; } // a failed listing must never look like "everything was deleted"

        var local = fs.List(rel).ToList();
        var ids = nodes.Select(n => n.Id).ToHashSet();
        var byId = local.Where(l => l.Id is not null).GroupBy(l => l.Id!).ToDictionary(g => g.Key, g => g.First());

        // gone on the server: remove the local copy, unless the user has unsynced work in it
        foreach (var l in local.Where(l => l.Id is not null && !ids.Contains(l.Id)))
        {
            var path = Join(rel, l.Name);
            try
            {
                if (l.State == LocalState.InSync && (!l.IsDirectory || OnlySynced(path))) { Own(path, () => fs.Delete(path)); r.DeletedLocal++; }
                else lock (_stale) _stale.Add(path); // edit beats delete: the push step uploads it again as a new file
            }
            catch (Exception e) { r.Errors.Add($"{path}: {e.Message}"); }
        }
        local = local.Where(l => l.Id is null || ids.Contains(l.Id) || IsStale(Join(rel, l.Name))).ToList();

        foreach (var n in nodes)
        {
            try { PullNode(n, rel, local, byId, r); }
            catch (Exception e) { r.Errors.Add($"{Join(rel, n.Name)}: {e.Message}"); }
        }
    }

    private bool IsStale(string path) { lock (_stale) return _stale.Contains(path); }

    private bool OnlySynced(string rel) =>
        fs.List(rel).All(e => e.State == LocalState.InSync && (!e.IsDirectory || OnlySynced(Join(rel, e.Name))));

    private void PullNode(RemoteNode n, string rel, List<LocalEntry> local, Dictionary<string, LocalEntry> byId, SyncReport r)
    {
        var path = Join(rel, n.Name);
        if (byId.TryGetValue(n.Id, out var m))
        {
            var cur = m.Name;
            if (!string.Equals(m.Name, n.Name, StringComparison.Ordinal) && m.State == LocalState.InSync)
            {
                var from = Join(rel, m.Name);
                Own(from, () => Own(path, () => fs.Rename(from, n.Name)));
                r.Renamed++;
                cur = n.Name;
            }
            var curPath = Join(rel, cur);
            if (n.IsFolder) { PullDir(n.Id, curPath, r); return; }
            if (m.State == LocalState.InSync && (m.Version != n.Version || m.Size != n.Size))
            {
                Own(curPath, () => fs.UpdatePlaceholder(curPath, n));
                r.Updated++;
            }
            return; // Modified: the push step decides
        }

        var same = local.FirstOrDefault(l => l.Id is null && l.Name.Equals(n.Name, StringComparison.OrdinalIgnoreCase));
        if (same is not null)
        {
            var samePath = Join(rel, same.Name);
            if (same.IsDirectory && n.IsFolder) { Own(samePath, () => fs.Bind(samePath, n)); r.Adopted++; PullDir(n.Id, samePath, r); }
            else if (!same.IsDirectory && !n.IsFolder && same.Size == n.Size) { Own(samePath, () => fs.Bind(samePath, n)); r.Adopted++; } // a file the older plain-folder sync already put here
            // anything else differs from the server: the push step turns the local one into a conflict copy
            return;
        }

        Own(path, () => fs.CreatePlaceholder(rel, n));
        r.Created++;
        if (n.IsFolder) PullDir(n.Id, path, r);
    }

    // ---------- disk -> server ----------

    public SyncReport Push()
    {
        var r = new SyncReport();
        PushDir(null, "", r);
        return r;
    }

    private void PushDir(string? parentId, string rel, SyncReport r)
    {
        List<LocalEntry> entries;
        try { entries = fs.List(rel).ToList(); }
        catch (Exception e) { r.Errors.Add($"{Show(rel)}: {e.Message}"); return; }

        foreach (var l in entries)
        {
            if (Ignored(l.Name)) continue;
            var path = Join(rel, l.Name);
            try
            {
                bool stale; lock (_stale) stale = _stale.Remove(path);
                if (l.Id is null || stale) { PushNew(parentId, l, path, r); continue; }
                if (l.IsDirectory)
                {
                    PushDir(l.Id, path, r);
                    if (l.State == LocalState.Modified) fs.Bind(path, new RemoteNode(l.Id, parentId, l.Name, true, 0, l.MtimeMs, null));
                }
                else if (l.State == LocalState.Modified) PushVersion(parentId, l, path, r);
            }
            catch (Exception e) { r.Errors.Add($"{path}: {e.Message}"); }
        }
    }

    /// <summary>Create on the server; if the name is taken by something else, create it as a conflict copy.</summary>
    private RemoteNode CreateUnique(string name, Func<string, RemoteNode> create)
    {
        try { return create(name); }
        catch (RemoteException e) when (e.Kind == RemoteError.NameTaken) { return create(ConflictName(name)); }
    }

    private void PushNew(string? parentId, LocalEntry l, string path, SyncReport r)
    {
        if (l.IsDirectory)
        {
            var folder = CreateUnique(l.Name, name => remote.CreateFolder(parentId, name));
            fs.Bind(path, folder);
            r.Uploaded++;
            PushDir(folder.Id, path, r);
            return;
        }
        var file = CreateUnique(l.Name, name => remote.UploadNew(parentId, name, fs.FullPath(path), l.MtimeMs));
        fs.Bind(path, file);
        if (!string.Equals(file.Name, l.Name, StringComparison.Ordinal)) r.Conflicts++;
        r.Uploaded++;
    }

    private void PushVersion(string? parentId, LocalEntry l, string path, SyncReport r)
    {
        try
        {
            var node = remote.UploadVersion(l.Id!, l.Version, fs.FullPath(path), l.MtimeMs);
            fs.Bind(path, node);
            r.Versioned++;
        }
        catch (RemoteException e) when (e.Kind == RemoteError.Conflict)
        {
            // changed on both sides: keep the local edit as a conflict copy (uploaded on the next pass) and take the server's version
            var copy = ConflictName(l.Name);
            fs.CopyAside(path, copy);
            fs.Bind(path, new RemoteNode(l.Id!, parentId, l.Name, false, l.Size, l.MtimeMs, l.Version)); // clears "modified" so the content can be replaced
            Own(path, () => fs.UpdatePlaceholder(path, remote.Get(l.Id!)));
            r.Conflicts++;
        }
        catch (RemoteException e) when (e.Kind == RemoteError.NotFound)
        {
            // deleted on the server while edited here: edit beats delete
            var node = CreateUnique(l.Name, name => remote.UploadNew(parentId, name, fs.FullPath(path), l.MtimeMs));
            fs.Bind(path, node);
            r.Uploaded++;
        }
    }

    // ---------- Explorer actions, from the filter driver callbacks ----------

    /// <summary>The user deleted a placeholder in Explorer: move it to the server's trash. Throwing makes Windows keep the file.</summary>
    public void LocalDeleted(string rel, string id)
    {
        if (IsOwn(rel)) return;
        try { remote.Trash(id); }
        catch (RemoteException e) when (e.Kind == RemoteError.NotFound) { }
    }

    /// <summary>The user renamed or moved a placeholder in Explorer. Throwing makes Windows refuse the rename.</summary>
    public void LocalRenamed(string oldRel, string newRel, string id)
    {
        if (IsOwn(oldRel) || IsOwn(newRel)) return;
        var (oldParent, oldName) = Split(oldRel);
        var (newParent, newName) = Split(newRel);
        if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
        {
            string? parentId = null;
            if (newParent.Length > 0)
            {
                parentId = fs.IdentityOf(newParent);
                if (parentId is null) throw new RemoteException(RemoteError.Other, $"{newParent} is not synced yet");
            }
            remote.Move(id, parentId);
        }
        if (!string.Equals(oldName, newName, StringComparison.Ordinal)) remote.Rename(id, newName);
    }
}

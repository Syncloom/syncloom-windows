namespace Syncloom.Sync;

/// <summary>One file or folder on the server (decrypted metadata).</summary>
public sealed record RemoteNode(string Id, string? ParentId, string Name, bool IsFolder, long Size, long MtimeMs, string? Version);

public enum LocalState
{
    /// <summary>A plain file or folder the user created; the server does not know it yet.</summary>
    NotPlaceholder,
    /// <summary>A placeholder (hydrated or not) that matches the server.</summary>
    InSync,
    /// <summary>A placeholder the user changed locally.</summary>
    Modified,
}

/// <summary>One entry of a local folder. <c>Id</c> and <c>Version</c> come from the identity stored in the placeholder.</summary>
public sealed record LocalEntry(string Name, bool IsDirectory, LocalState State, string? Id, string? Version, long Size, long MtimeMs);

public enum RemoteError { Conflict, NotFound, NameTaken, Other }

public sealed class RemoteException(RemoteError kind, string message) : Exception(message)
{
    public RemoteError Kind { get; } = kind;
}

/// <summary>The server, through the shared Rust core.</summary>
public interface IRemote
{
    long ChangesHead();
    /// <summary>Children of a folder (null is the root), without deleted ones.</summary>
    IReadOnlyList<RemoteNode> List(string? parentId);
    RemoteNode Get(string id);
    RemoteNode CreateFolder(string? parentId, string name);
    RemoteNode UploadNew(string? parentId, string name, string path, long mtimeMs);
    /// <summary>Throws <see cref="RemoteError.Conflict"/> when <paramref name="baseVersion"/> is not the head any more.</summary>
    RemoteNode UploadVersion(string id, string? baseVersion, string path, long mtimeMs);
    RemoteNode Rename(string id, string name);
    RemoteNode Move(string id, string? newParentId);
    void Trash(string id);
    /// <summary>Decrypts the head version into <paramref name="destPath"/>.</summary>
    void Download(string id, string? version, string destPath);
}

/// <summary>The sync root on disk. Paths are relative to the root, separated by backslashes; "" is the root itself.</summary>
public interface ILocalFs
{
    IReadOnlyList<LocalEntry> List(string relDir);
    string FullPath(string rel);
    /// <summary>The server id of a folder, or null when it is the root or not a placeholder.</summary>
    string? IdentityOf(string relDir);
    /// <summary>A new, not hydrated placeholder (a folder is created empty).</summary>
    void CreatePlaceholder(string relDir, RemoteNode node);
    /// <summary>The server has a newer version: take its metadata and drop the local content (it is fetched again when opened).</summary>
    void UpdatePlaceholder(string rel, RemoteNode node);
    /// <summary>Make a plain file or folder, or a placeholder, represent <paramref name="node"/> and mark it in sync, keeping its content.</summary>
    void Bind(string rel, RemoteNode node);
    void Delete(string rel);
    void Rename(string rel, string newName);
    /// <summary>A plain copy of the file next to it, for conflicts.</summary>
    void CopyAside(string rel, string newName);
}

public sealed class SyncReport
{
    public int Created, Updated, Renamed, DeletedLocal, Adopted, Uploaded, Versioned, Conflicts, Trashed;
    public List<string> Errors { get; } = [];
    public bool Changed => Created + Updated + Renamed + DeletedLocal + Adopted + Uploaded + Versioned + Conflicts + Trashed > 0;
    public override string ToString() =>
        $"{Created} new, {Updated} updated, {DeletedLocal} removed, {Uploaded + Versioned} uploaded, {Conflicts} conflict copies" + (Errors.Count > 0 ? $", {Errors.Count} errors" : "");
}

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Syncloom.Sync;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace Syncloom.CloudFiles;

/// <summary>The sync root on disk, seen through the Cloud Files API: placeholders carry the server id and version as their identity.</summary>
public sealed unsafe class CloudFs(string root) : ILocalFs
{
    private const int ErrorNotACloudFile = unchecked((int)0x80070178); // HRESULT_FROM_WIN32(ERROR_NOT_A_CLOUD_FILE)
    private const uint FileReadAttributes = 0x80;
    private const uint GenericWrite = 0x40000000;
    private const long UnixEpochAsFileTime = 116_444_736_000_000_000;

    private string Abs(string rel) => rel.Length == 0 ? root : Path.Combine(root, rel);
    public string FullPath(string rel) => Abs(rel);

    // ---------- reading ----------

    private sealed record Info(bool IsPlaceholder, bool InSync, string? Id, string? Version);

    private static SafeFileHandle OpenForRead(string path) =>
        PInvoke.CreateFile(path, FileReadAttributes, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, null);

    /// <summary>Does not touch the file's data, so a not hydrated placeholder stays that way.</summary>
    private static Info? ReadInfo(string path)
    {
        using var h = OpenForRead(path);
        if (h.IsInvalid) return null;
        var buffer = new byte[4096];
        uint returned;
        HRESULT hr;
        fixed (byte* p = buffer) hr = PInvoke.CfGetPlaceholderInfo(h, CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_STANDARD, p, (uint)buffer.Length, &returned);
        if (hr.Value == ErrorNotACloudFile) return new Info(false, false, null, null);
        if (hr.Failed) return null;
        // CF_PLACEHOLDER_STANDARD_INFO: four sizes (32 bytes), PinState (4), InSyncState (4), two file ids (16), identity length (4), identity
        var inSync = BitConverter.ToInt32(buffer, 36) == (int)CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC;
        var idLength = BitConverter.ToUInt32(buffer, 56);
        if (idLength == 0 || 60 + idLength > buffer.Length) return new Info(true, inSync, null, null);
        var (id, version) = Identity.Decode(buffer.AsSpan(60, (int)idLength));
        return new Info(true, inSync, id, version);
    }

    public IReadOnlyList<LocalEntry> List(string relDir)
    {
        var result = new List<LocalEntry>();
        foreach (var fi in new DirectoryInfo(Abs(relDir)).EnumerateFileSystemInfos())
        {
            var info = ReadInfo(fi.FullName);
            if (info is null) continue; // locked or vanished: leave it alone this time
            var isDir = fi is DirectoryInfo;
            var state = !info.IsPlaceholder ? LocalState.NotPlaceholder : info.InSync ? LocalState.InSync : LocalState.Modified;
            result.Add(new LocalEntry(fi.Name, isDir, state, info.Id, info.Version, isDir ? 0 : ((FileInfo)fi).Length, new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        return result;
    }

    public string? IdentityOf(string relDir) => relDir.Length == 0 ? null : ReadInfo(Abs(relDir))?.Id;

    // ---------- writing ----------

    private static CF_FS_METADATA Metadata(RemoteNode n)
    {
        var time = n.MtimeMs * 10_000 + UnixEpochAsFileTime;
        return new CF_FS_METADATA
        {
            BasicInfo = new FILE_BASIC_INFO
            {
                CreationTime = time, LastAccessTime = time, LastWriteTime = time, ChangeTime = time,
                FileAttributes = n.IsFolder ? 0x10u /* DIRECTORY */ : 0x80u /* NORMAL */,
            },
            FileSize = n.IsFolder ? 0 : n.Size,
        };
    }

    public void CreatePlaceholder(string relDir, RemoteNode node)
    {
        var identity = Identity.Encode(node.Id, node.Version);
        fixed (byte* pid = identity)
        fixed (char* pname = node.Name)
        {
            var info = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = new PCWSTR(pname),
                FsMetadata = Metadata(node),
                FileIdentity = pid,
                FileIdentityLength = (uint)identity.Length,
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC
                        | (node.IsFolder ? CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION : 0),
            };
            uint done;
            PInvoke.CfCreatePlaceholders(Abs(relDir), MemoryMarshal.CreateSpan(ref info, 1), CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &done).ThrowOnFailure();
            info.Result.ThrowOnFailure();
        }
    }

    /// <summary>A handle that Cloud Files calls accept, for a file or a folder.</summary>
    private static SafeFileHandle OpenProtected(string path)
    {
        if (PInvoke.CfOpenFileWithOplock(path, CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_WRITE_ACCESS | CF_OPEN_FILE_FLAGS.CF_OPEN_FILE_FLAG_EXCLUSIVE, out var h).Succeeded) return h;
        var plain = PInvoke.CreateFile(path, GenericWrite, FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS, null);
        if (plain.IsInvalid) throw new IOException($"cannot open {path}: {Marshal.GetLastWin32Error()}");
        return plain;
    }

    public void UpdatePlaceholder(string rel, RemoteNode node)
    {
        var path = Abs(rel);
        var identity = Identity.Encode(node.Id, node.Version);
        var meta = Metadata(node);
        using var h = OpenProtected(path);
        PInvoke.CfSetInSyncState(h, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC, CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null); // a modified file can only be dehydrated when in sync
        fixed (byte* pid = identity)
            PInvoke.CfUpdatePlaceholder(h, meta, pid, (uint)identity.Length, default,
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE, null, null).ThrowOnFailure();
    }

    public void Bind(string rel, RemoteNode node)
    {
        var path = Abs(rel);
        var identity = Identity.Encode(node.Id, node.Version);
        var already = ReadInfo(path)?.IsPlaceholder == true;
        using var h = OpenProtected(path);
        fixed (byte* pid = identity)
        {
            if (already)
                PInvoke.CfUpdatePlaceholder(h, null, pid, (uint)identity.Length, default, CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC, null, null).ThrowOnFailure();
            else
                PInvoke.CfConvertToPlaceholder(h, pid, (uint)identity.Length, CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, null, null).ThrowOnFailure();
        }
    }

    public void Delete(string rel)
    {
        var path = Abs(rel);
        if (Directory.Exists(path)) Directory.Delete(path, true);
        else File.Delete(path);
    }

    public void Rename(string rel, string newName)
    {
        var from = Abs(rel);
        var to = Path.Combine(Path.GetDirectoryName(from)!, newName);
        if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
    }

    public void CopyAside(string rel, string newName) =>
        File.Copy(Abs(rel), Path.Combine(Path.GetDirectoryName(Abs(rel))!, newName), overwrite: false);
}

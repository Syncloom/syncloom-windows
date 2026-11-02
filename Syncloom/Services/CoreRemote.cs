using System.Text.Json;
using System.Text.Json.Nodes;
using Syncloom.Sync;

namespace Syncloom.Services;

/// <summary>The server for the sync engine: every call goes through the C ABI of the Rust core, on the signed-in session.</summary>
public sealed class CoreRemote : IRemote
{
    private static JsonNode? Call(string op, object? args = null)
    {
        var req = JsonSerializer.SerializeToNode(args ?? new { })!.AsObject();
        req["handle"] = AppState.Handle;
        try { return Native.Invoke(op, req); }
        catch (Native.CoreException e) { throw Map(e.Message); }
    }

    private static RemoteException Map(string message)
    {
        var kind = message.Contains("version conflict") ? RemoteError.Conflict
            : message.Contains("not found") || message.Contains("returned 404") ? RemoteError.NotFound
            : message.Contains("returned 409") ? RemoteError.NameTaken
            : RemoteError.Other;
        return new RemoteException(kind, message);
    }

    private static RemoteNode ToNode(JsonNode n) => new(
        n["id"]!.GetValue<string>(), n["parent_id"]?.GetValue<string>(), n["name"]!.GetValue<string>(), n["kind"]!.GetValue<string>() == "folder",
        n["size"]?.GetValue<long>() ?? 0, n["mtime_ms"]?.GetValue<long>() ?? 0, n["head_version"]?.GetValue<string>());

    public long ChangesHead() => Call("changes_head")!.GetValue<long>();

    public IReadOnlyList<RemoteNode> List(string? parentId) => Call("list", new { parent = parentId })!.AsArray()
        .Select(n => n!)
        .Where(n => n["deleted"]?.GetValue<bool>() != true)
        .Where(n => n["kind"]!.GetValue<string>() is "folder" || (n["kind"]!.GetValue<string>() is "file" && n["head_version"] is not null)) // not key/value spaces, not uploads still in progress
        .Select(ToNode).ToList();

    public RemoteNode Get(string id) => ToNode(Call("get", new { id })!);
    public RemoteNode CreateFolder(string? parentId, string name) => ToNode(Call("create_folder", new { parent = parentId, name })!);
    public RemoteNode UploadNew(string? parentId, string name, string path, long mtimeMs) => ToNode(Call("upload_new_file", new { parent = parentId, name, path, mtime_ms = mtimeMs })!);
    public RemoteNode UploadVersion(string id, string? baseVersion, string path, long mtimeMs) => ToNode(Call("upload_version", new { id, base_version = baseVersion, path, mtime_ms = mtimeMs })!);
    public RemoteNode Rename(string id, string name) => ToNode(Call("rename", new { id, name })!);
    public RemoteNode Move(string id, string? newParentId) => ToNode(Call("move_to", new { id, new_parent = newParentId })!);
    public void Trash(string id) => Call("trash", new { id });
    public void Download(string id, string? version, string destPath) => Call("download_file", new { id, version, dest = destPath });
}

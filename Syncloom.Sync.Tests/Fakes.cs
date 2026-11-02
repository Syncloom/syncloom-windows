using Syncloom.Sync;

namespace Syncloom.Sync.Tests;

/// <summary>An in-memory server with the rules the real one has: unique names per folder, version checks, trash.</summary>
public sealed class FakeRemote : IRemote
{
    private int _n;
    public long Head;
    public readonly Dictionary<string, RemoteNode> Nodes = new();
    public readonly Dictionary<string, string> Content = new();
    public readonly List<string> Calls = new();
    public bool FailList;

    public RemoteNode AddFolder(string? parent, string name) => Add(new RemoteNode(NewId(), parent, name, true, 0, 0, null));
    public RemoteNode AddFile(string? parent, string name, string content)
    {
        var n = Add(new RemoteNode(NewId(), parent, name, false, content.Length, 1000, Version()));
        Content[n.Id] = content;
        return n;
    }
    private string NewId() => "n" + ++_n;
    private string Version() => "v" + ++_n;
    private RemoteNode Add(RemoteNode n) { Nodes[n.Id] = n; Head++; return n; }

    public IEnumerable<RemoteNode> Children(string? parent) => Nodes.Values.Where(n => n.ParentId == parent);

    public void Edit(string id, string content)
    {
        Nodes[id] = Nodes[id] with { Size = content.Length, Version = Version() };
        Content[id] = content;
        Head++;
    }

    private void CheckFree(string? parent, string name)
    {
        if (Children(parent).Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new RemoteException(RemoteError.NameTaken, "name exists");
    }
    private RemoteNode Find(string id) => Nodes.TryGetValue(id, out var n) ? n : throw new RemoteException(RemoteError.NotFound, "not found");

    public long ChangesHead() => Head;
    public IReadOnlyList<RemoteNode> List(string? parentId) => FailList ? throw new IOException("offline") : Children(parentId).ToList();
    public RemoteNode Get(string id) => Find(id);
    public RemoteNode CreateFolder(string? parentId, string name) { Calls.Add($"folder {name}"); CheckFree(parentId, name); return AddFolder(parentId, name); }
    public RemoteNode UploadNew(string? parentId, string name, string path, long mtimeMs)
    {
        Calls.Add($"new {name}");
        CheckFree(parentId, name);
        return AddFile(parentId, name, File.ReadAllText(path));
    }
    public RemoteNode UploadVersion(string id, string? baseVersion, string path, long mtimeMs)
    {
        Calls.Add($"version {Find(id).Name}");
        if (Find(id).Version != baseVersion) throw new RemoteException(RemoteError.Conflict, "version conflict");
        Edit(id, File.ReadAllText(path));
        return Nodes[id];
    }
    public RemoteNode Rename(string id, string name) { Calls.Add($"rename {Find(id).Name}->{name}"); Nodes[id] = Find(id) with { Name = name }; Head++; return Nodes[id]; }
    public RemoteNode Move(string id, string? newParentId) { Calls.Add($"move {Find(id).Name}"); Nodes[id] = Find(id) with { ParentId = newParentId }; Head++; return Nodes[id]; }
    public void Trash(string id) { Calls.Add($"trash {id}"); Find(id); Nodes.Remove(id); Head++; }
    public void Download(string id, string? version, string destPath) => File.WriteAllText(destPath, Content[id]);
}

/// <summary>An in-memory sync root. "Content" is only present for hydrated files; placeholders have none.</summary>
public sealed class FakeFs : ILocalFs
{
    public sealed class Item
    {
        public bool IsDir;
        public LocalState State;
        public string? Id, Version;
        public long Size;
        public string? Content; // null for a placeholder that was never opened
    }
    public readonly Dictionary<string, Item> Items = new(StringComparer.OrdinalIgnoreCase);
    public Action<string, string?>? OnDelete;
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "syncloom-fake-" + Guid.NewGuid().ToString("N"));
    public FakeFs() => Directory.CreateDirectory(_tmp);

    public Item Plain(string rel, string content) => Items[rel] = new Item { State = LocalState.NotPlaceholder, Size = content.Length, Content = content };
    public Item PlainDir(string rel) => Items[rel] = new Item { IsDir = true, State = LocalState.NotPlaceholder };
    public void Edit(string rel, string content) { var i = Items[rel]; i.Content = content; i.Size = content.Length; if (i.Id != null) i.State = LocalState.Modified; }

    private static string Parent(string rel) => SyncEngine.Split(rel).Parent;

    public IReadOnlyList<LocalEntry> List(string relDir) =>
        Items.Where(kv => Parent(kv.Key).Equals(relDir, StringComparison.OrdinalIgnoreCase))
            .Select(kv => new LocalEntry(SyncEngine.Split(kv.Key).Name, kv.Value.IsDir, kv.Value.State, kv.Value.Id, kv.Value.Version, kv.Value.Size, 5)).ToList();

    public string FullPath(string rel)
    {
        var p = Path.Combine(_tmp, Guid.NewGuid().ToString("N"));
        File.WriteAllText(p, Items[rel].Content ?? throw new InvalidOperationException("reading a placeholder would hydrate it"));
        return p;
    }
    public string? IdentityOf(string relDir) => relDir.Length == 0 ? null : Items.GetValueOrDefault(relDir)?.Id;

    public void CreatePlaceholder(string relDir, RemoteNode n)
    {
        var rel = SyncEngine.Join(relDir, n.Name);
        if (Items.ContainsKey(rel)) throw new IOException("exists");
        Items[rel] = new Item { IsDir = n.IsFolder, State = LocalState.InSync, Id = n.Id, Version = n.Version, Size = n.Size };
    }
    public void UpdatePlaceholder(string rel, RemoteNode n) { var i = Items[rel]; i.Version = n.Version; i.Size = n.Size; i.Content = null; i.State = LocalState.InSync; }
    public void Bind(string rel, RemoteNode n) { var i = Items[rel]; i.Id = n.Id; i.Version = n.Version; i.State = LocalState.InSync; }
    public void Delete(string rel) { OnDelete?.Invoke(rel, Items.GetValueOrDefault(rel)?.Id); foreach (var k in Items.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase)).ToList()) Items.Remove(k); }
    public void Rename(string rel, string newName)
    {
        var (parent, _) = SyncEngine.Split(rel);
        foreach (var k in Items.Keys.Where(k => k.Equals(rel, StringComparison.OrdinalIgnoreCase) || k.StartsWith(rel + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            var item = Items[k]; Items.Remove(k);
            Items[SyncEngine.Join(parent, newName) + k[rel.Length..]] = item;
        }
    }
    public void CopyAside(string rel, string newName)
    {
        var i = Items[rel];
        Items[SyncEngine.Join(Parent(rel), newName)] = new Item { State = LocalState.NotPlaceholder, Size = i.Size, Content = i.Content };
    }
}

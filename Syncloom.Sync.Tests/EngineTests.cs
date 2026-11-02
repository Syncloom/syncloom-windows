using Syncloom.Sync;
using Xunit;

namespace Syncloom.Sync.Tests;

public class EngineTests
{
    private readonly FakeRemote _remote = new();
    private readonly FakeFs _fs = new();
    private readonly SyncEngine _engine;

    public EngineTests() => _engine = new SyncEngine(_remote, _fs, "laptop", () => new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

    private void Sync() { Assert.Empty(_engine.Pull().Errors); Assert.Empty(_engine.Push().Errors); }

    [Fact]
    public void Pull_creates_placeholders_for_the_whole_tree_without_content()
    {
        var docs = _remote.AddFolder(null, "Docs");
        _remote.AddFile(docs.Id, "a.txt", "hello");
        _remote.AddFile(null, "b.pdf", "pdf");
        var r = _engine.Pull();
        Assert.Equal(3, r.Created);
        Assert.Equal(LocalState.InSync, _fs.Items["Docs\\a.txt"].State);
        Assert.Null(_fs.Items["Docs\\a.txt"].Content); // not hydrated
        Assert.Equal(5, _fs.Items["Docs\\a.txt"].Size);
        Assert.Equal(0, _engine.Pull().Created); // nothing to do the second time
    }

    [Fact]
    public void Pull_updates_in_sync_files_and_leaves_modified_ones_alone()
    {
        var a = _remote.AddFile(null, "a.txt", "1");
        var b = _remote.AddFile(null, "b.txt", "1");
        _engine.Pull();
        _fs.Items["a.txt"].Content = "1"; // hydrated
        _fs.Edit("b.txt", "my edit");
        _remote.Edit(a.Id, "22");
        _remote.Edit(b.Id, "333");
        var r = _engine.Pull();
        Assert.Equal(1, r.Updated); // b.txt has an unsynced edit and is left to the push step
        Assert.Equal(2, _fs.Items["a.txt"].Size);
        Assert.Null(_fs.Items["a.txt"].Content); // dropped, fetched again on open
        Assert.Equal("my edit", _fs.Items["b.txt"].Content);
        Assert.Equal(LocalState.Modified, _fs.Items["b.txt"].State);
    }

    [Fact]
    public void Pull_removes_files_deleted_on_the_server_but_keeps_unsynced_work()
    {
        var gone = _remote.AddFile(null, "gone.txt", "x");
        var edited = _remote.AddFile(null, "edited.txt", "x");
        var dir = _remote.AddFolder(null, "Dir");
        _remote.AddFile(dir.Id, "inside.txt", "x");
        _engine.Pull();
        _fs.Edit("edited.txt", "important");
        _fs.Plain("Dir\\mine.txt", "new local file");
        foreach (var id in new[] { gone.Id, edited.Id, dir.Id }) _remote.Nodes.Remove(id);
        _remote.Nodes.Remove(_remote.Nodes.Values.First(n => n.Name == "inside.txt").Id);

        _engine.Pull();
        Assert.DoesNotContain("gone.txt", _fs.Items.Keys);
        Assert.Contains("edited.txt", _fs.Items.Keys);
        Assert.Contains("Dir", _fs.Items.Keys); // holds a file the server never saw

        // edit beats delete: the next push uploads them again
        _engine.Push();
        Assert.Contains(_remote.Nodes.Values, n => n.Name == "edited.txt");
        Assert.Contains(_remote.Nodes.Values, n => n.Name == "Dir");
        Assert.Contains(_remote.Nodes.Values, n => n.Name == "mine.txt");
    }

    [Fact]
    public void A_failed_listing_never_deletes_anything()
    {
        _remote.AddFile(null, "a.txt", "x");
        _engine.Pull();
        _remote.FailList = true;
        var r = _engine.Pull();
        Assert.NotEmpty(r.Errors);
        Assert.Contains("a.txt", _fs.Items.Keys);
    }

    [Fact]
    public void Pull_follows_a_rename_on_the_server()
    {
        var a = _remote.AddFile(null, "old.txt", "x");
        _engine.Pull();
        _remote.Rename(a.Id, "new.txt");
        var r = _engine.Pull();
        Assert.Equal(1, r.Renamed);
        Assert.Contains("new.txt", _fs.Items.Keys);
        Assert.DoesNotContain("old.txt", _fs.Items.Keys);
    }

    [Fact]
    public void Existing_plain_files_from_the_old_folder_sync_are_adopted_not_duplicated()
    {
        var dir = _remote.AddFolder(null, "Docs");
        _remote.AddFile(dir.Id, "a.txt", "same!");
        _fs.PlainDir("Docs");
        _fs.Plain("Docs\\a.txt", "same!");
        Sync();
        Assert.Equal(LocalState.InSync, _fs.Items["Docs\\a.txt"].State);
        Assert.Equal(_remote.Nodes.Values.Single(n => n.Name == "a.txt").Id, _fs.Items["Docs\\a.txt"].Id);
        Assert.Equal(2, _remote.Nodes.Count);
        Assert.Empty(_remote.Calls);
    }

    [Fact]
    public void A_plain_file_that_differs_from_the_server_becomes_a_conflict_copy()
    {
        _remote.AddFile(null, "a.txt", "server");
        _fs.Plain("a.txt", "mine!!!");
        Sync();
        var names = _remote.Nodes.Values.Select(n => n.Name).OrderBy(x => x).ToList();
        Assert.Equal(["a (conflict – laptop – 2026-11-02).txt", "a.txt"], names);
        Assert.Equal("mine!!!", _remote.Content[_remote.Nodes.Values.Single(n => n.Name.Contains("conflict")).Id]);
        Assert.Equal("server", _remote.Content[_remote.Nodes.Values.Single(n => n.Name == "a.txt").Id]);
    }

    [Fact]
    public void Push_uploads_new_files_and_folders_with_their_content_and_binds_them()
    {
        _fs.PlainDir("Photos");
        _fs.Plain("Photos\\x.jpg", "jpeg");
        _fs.Plain("top.txt", "t");
        _fs.Plain("~$lock.docx", "ignored");
        Sync();
        Assert.Equal(3, _remote.Nodes.Count);
        Assert.All(_fs.Items.Where(i => !i.Key.StartsWith("~$")), i => Assert.Equal(LocalState.InSync, i.Value.State));
        var photos = _remote.Nodes.Values.Single(n => n.Name == "Photos");
        Assert.Equal(photos.Id, _remote.Nodes.Values.Single(n => n.Name == "x.jpg").ParentId);
    }

    [Fact]
    public void Push_uploads_a_modified_file_as_a_new_version()
    {
        var a = _remote.AddFile(null, "a.txt", "1");
        _engine.Pull();
        _fs.Items["a.txt"].Content = "1";
        _fs.Edit("a.txt", "edited");
        _engine.Push();
        Assert.Equal("edited", _remote.Content[a.Id]);
        Assert.Equal(LocalState.InSync, _fs.Items["a.txt"].State);
        Assert.Equal(_remote.Nodes[a.Id].Version, _fs.Items["a.txt"].Version);
    }

    [Fact]
    public void A_stale_edit_keeps_both_sides_as_a_conflict_copy()
    {
        var a = _remote.AddFile(null, "a.txt", "base");
        _engine.Pull();
        _fs.Items["a.txt"].Content = "base";
        _fs.Edit("a.txt", "local edit");
        _remote.Edit(a.Id, "server edit");
        var r = _engine.Push();
        Assert.Equal(1, r.Conflicts);
        Assert.Equal("local edit", _fs.Items["a (conflict – laptop – 2026-11-02).txt"].Content);
        Assert.Null(_fs.Items["a.txt"].Content); // the server's version, fetched when opened
        Sync();
        Assert.Equal("local edit", _remote.Content[_remote.Nodes.Values.Single(n => n.Name.Contains("conflict")).Id]);
        Assert.Equal("server edit", _remote.Content[a.Id]);
    }

    [Fact]
    public void Deleting_in_Explorer_moves_to_the_server_trash_but_the_engines_own_deletes_do_not()
    {
        var a = _remote.AddFile(null, "a.txt", "x");
        var b = _remote.AddFile(null, "b.txt", "x");
        _engine.Pull();
        _engine.LocalDeleted("a.txt", a.Id);
        Assert.DoesNotContain(a.Id, _remote.Nodes.Keys);
        _remote.Nodes.Remove(b.Id);
        _remote.Calls.Clear();
        _fs.OnDelete = (rel, id) => _engine.LocalDeleted(rel, id!); // what the filter driver does when anything is deleted
        _engine.Pull(); // deletes b.txt locally; the callback fires while the engine owns the path
        Assert.Empty(_remote.Calls);
        Assert.DoesNotContain("b.txt", _fs.Items.Keys);
    }

    [Fact]
    public void Renaming_and_moving_in_Explorer_are_sent_to_the_server()
    {
        var dir = _remote.AddFolder(null, "Dir");
        var a = _remote.AddFile(null, "a.txt", "x");
        _engine.Pull();
        _engine.LocalRenamed("a.txt", "b.txt", a.Id);
        Assert.Equal("b.txt", _remote.Nodes[a.Id].Name);
        _engine.LocalRenamed("b.txt", "Dir\\b.txt", a.Id);
        Assert.Equal(dir.Id, _remote.Nodes[a.Id].ParentId);
        _engine.LocalRenamed("Dir\\b.txt", "Dir\\c.txt", a.Id);
        Assert.Equal("c.txt", _remote.Nodes[a.Id].Name);
        Assert.Throws<RemoteException>(() => _engine.LocalRenamed("Dir\\c.txt", "Unsynced\\c.txt", a.Id));
    }

    [Fact]
    public void Conflict_names_follow_the_other_clients()
    {
        Assert.Equal("report (conflict – laptop – 2026-11-02).txt", _engine.ConflictName("report.txt"));
        Assert.Equal("Makefile (conflict – laptop – 2026-11-02)", _engine.ConflictName("Makefile"));
        Assert.Equal(".env (conflict – laptop – 2026-11-02)", _engine.ConflictName(".env"));
    }
}

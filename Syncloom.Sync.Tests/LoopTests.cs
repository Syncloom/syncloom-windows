using Syncloom.Sync;
using Xunit;

namespace Syncloom.Sync.Tests;

public class LoopTests
{
    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 120 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "did not happen within 3 seconds");
    }

    [Fact]
    public async Task The_loop_pulls_when_the_server_changes_and_pushes_after_a_poke()
    {
        var remote = new FakeRemote();
        var fs = new FakeFs();
        var engine = new SyncEngine(remote, fs, "laptop");
        using var loop = new SyncLoop(engine, remote, _ => { }) { Poll = TimeSpan.FromMilliseconds(40), Debounce = TimeSpan.FromMilliseconds(10) };
        loop.Start();

        remote.AddFile(null, "from-server.txt", "x");
        await Eventually(() => { lock (fs.Items) return fs.Items.ContainsKey("from-server.txt"); });

        lock (fs.Items) fs.Plain("from-disk.txt", "y");
        loop.Poke();
        await Eventually(() => remote.Nodes.Values.Any(n => n.Name == "from-disk.txt"));
    }
}

using System.Text.Json.Nodes;

namespace Syncloom.Services;

/// <summary>The signed-in session (a handle inside the Rust core) and background folder sync.</summary>
public static class AppState
{
    public static ulong Handle;
    public static SavedSession? Saved;
    public static string Device => Environment.MachineName;

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Syncloom");

    public static async Task<bool> RestoreAsync()
    {
        var s = SessionStore.Load();
        if (s is null) return false;
        try
        {
            var r = await Native.InvokeAsync("from_secrets", new { server = s.Server, token = s.Token, secrets = s.Secrets });
            Handle = r!["handle"]!.GetValue<ulong>();
            Saved = s;
            return true;
        }
        catch { SessionStore.Clear(); return false; }
    }

    public static async Task OpenAsync(string server, string username, string token, string password)
    {
        var r = await Native.InvokeAsync("connect", new { server, token, password });
        Handle = r!["handle"]!.GetValue<ulong>();
        Saved = new SavedSession(server, username.ToLowerInvariant(), token, r["secrets"]!.GetValue<string>(), DefaultFolder);
        SessionStore.Save(Saved);
    }

    public static Task<JsonNode?> CallAsync(string op, object? args = null)
    {
        var req = System.Text.Json.JsonSerializer.SerializeToNode(args ?? new { })!.AsObject();
        req["handle"] = Handle;
        return Native.InvokeAsync(op, req);
    }

    public static void SignOut()
    {
        _ = Native.InvokeAsync("close", new { handle = Handle });
        SessionStore.Clear();
        Handle = 0;
        Saved = null;
    }

    private static CancellationTokenSource? _sync;

    /// <summary>Keeps the folder in sync: one reconciliation pass every 30 seconds.</summary>
    public static void StartFolderSync(Action<string> onResult)
    {
        _sync?.Cancel();
        _sync = new CancellationTokenSource();
        var ct = _sync.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var r = await CallAsync("sync_once", new { root = Saved!.SyncFolder, device = Device, state_db = SessionStore.StateDb });
                    onResult($"Synced: {r!["uploaded"]!.AsArray().Count} up, {r["downloaded"]!.AsArray().Count} down, {r["conflicts"]!.AsArray().Count} conflict copies");
                }
                catch (Exception e) { onResult(e.Message); }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (TaskCanceledException) { }
            }
        }, ct);
    }
}

using System.Text.Json.Nodes;
using Syncloom.CloudFiles;

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
        StopCloud();
        if (Saved is { } s) _ = CloudFilesRegistration.UnregisterAsync(s.Username);
        _ = Native.InvokeAsync("close", new { handle = Handle });
        SessionStore.Clear();
        Handle = 0;
        Saved = null;
    }

    /// <summary>The on-demand files of this account (placeholders in the sync folder).</summary>
    public static CloudSession? Cloud;
    public static string SyncStatus { get; private set; } = "Starting…";
    public static event Action<string>? SyncStatusChanged;

    /// <summary>Connects the registered sync folder and starts mirroring the server into it. Files download when they are opened.</summary>
    public static void StartCloud()
    {
        Cloud?.Dispose();
        Cloud = CloudSession.Start(Saved!.SyncFolder, new CoreRemote(), Device, s => { SyncStatus = s; SyncStatusChanged?.Invoke(s); });
    }

    public static void ReportSyncError(string message)
    {
        SyncStatus = message;
        SyncStatusChanged?.Invoke(message);
    }

    public static void StopCloud()
    {
        Cloud?.Dispose();
        Cloud = null;
    }
}

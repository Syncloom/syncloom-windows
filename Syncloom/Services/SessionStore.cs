using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Syncloom.Services;

public sealed record SavedSession(string Server, string Username, string Token, string Secrets, string SyncFolder);

/// <summary>Keeps the signed-in session encrypted with DPAPI (bound to the Windows user) under %LOCALAPPDATA%.</summary>
public static class SessionStore
{
    private static string PathFile => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Syncloom", "session.bin");
    public static string StateDb => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Syncloom", "state.db");

    public static void Save(SavedSession s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFile)!);
        var data = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(s), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFile, data);
    }

    public static SavedSession? Load()
    {
        try { return JsonSerializer.Deserialize<SavedSession>(ProtectedData.Unprotect(File.ReadAllBytes(PathFile), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    public static void Clear() { try { File.Delete(PathFile); File.Delete(StateDb); } catch { } }
}

using Windows.Storage;
using Windows.Storage.Provider;

namespace Syncloom.Services;

/// <summary>
/// Registers the sync folder as a Windows sync root, so Explorer shows the Syncloom entry with status icons and
/// "Free up space" / "Always keep on this device". The files in it are placeholders: the engine in Syncloom.CloudFiles
/// creates them for everything on the server, and Windows asks it for the content when a file is opened.
/// </summary>
public static class CloudFilesRegistration
{
    private static string IdFor(string username) => $"Syncloom!{Environment.UserName}!{username}";

    public static async Task RegisterAsync(string folder, string username)
    {
        Directory.CreateDirectory(folder);
        var info = new StorageProviderSyncRootInfo
        {
            Id = IdFor(username),
            Path = await StorageFolder.GetFolderFromPathAsync(folder),
            DisplayNameResource = "Syncloom",
            IconResource = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            // a file is downloaded completely the first time it is opened; the engine lists the whole tree itself
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime | StorageProviderInSyncPolicy.PreserveInsyncForSyncEngine,
            Version = "0.1.2",
            ShowSiblingsAsGroup = false,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
        };
        StorageProviderSyncRootManager.Register(info);
    }

    /// <summary>On sign-out. Placeholders that were not downloaded stay in the folder as empty shells until it is deleted.</summary>
    public static Task UnregisterAsync(string username)
    {
        try { StorageProviderSyncRootManager.Unregister(IdFor(username)); } catch { }
        return Task.CompletedTask;
    }
}

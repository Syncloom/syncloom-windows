using Windows.Storage;
using Windows.Storage.Provider;

namespace Syncloom.Services;

/// <summary>
/// Registers the sync folder as a Windows sync root so Explorer shows the Syncloom entry with status icons.
/// Placeholder hydration (Cloud Files API callbacks) is the next step: until then the folder is a normal,
/// fully synced directory handled by the core's sync engine.
/// </summary>
public static class CloudFilesRegistration
{
    public static async Task RegisterAsync(string folder, string username)
    {
        Directory.CreateDirectory(folder);
        var info = new StorageProviderSyncRootInfo
        {
            Id = $"Syncloom!{Environment.UserName}!{username}",
            Path = await StorageFolder.GetFolderFromPathAsync(folder),
            DisplayNameResource = "Syncloom",
            IconResource = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime | StorageProviderInSyncPolicy.DirectoryCreationTime,
            Version = "1.0",
            ShowSiblingsAsGroup = false,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
        };
        StorageProviderSyncRootManager.Register(info);
    }
}

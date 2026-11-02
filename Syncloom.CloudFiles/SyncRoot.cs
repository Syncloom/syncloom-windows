using Syncloom.Sync;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;

namespace Syncloom.CloudFiles;

/// <summary>
/// The connection of the sync root to the Cloud Files filter driver. Windows calls back when a placeholder is opened
/// (hydration: fetch and decrypt the file, then hand the bytes over) and before one is deleted or renamed in Explorer.
/// </summary>
public sealed unsafe class SyncRoot : IDisposable
{
    private const int StatusUnsuccessful = unchecked((int)0xC0000001);
    private const int Chunk = 1 << 20; // a multiple of 4 KiB, as the platform requires for transfers that do not end the file

    private readonly string _root;
    private readonly string _rootNormalized;
    private readonly IRemote _remote;
    private readonly SyncEngine _engine;
    private readonly Action<string> _log;
    private CF_CONNECTION_KEY _key;
    private bool _connected;

    // kept in fields so the garbage collector cannot free the delegates Windows holds on to
    private readonly CF_CALLBACK _fetch;
    private readonly CF_CALLBACK _delete;
    private readonly CF_CALLBACK _rename;

    public SyncRoot(string root, IRemote remote, SyncEngine engine, Action<string> log)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\');
        _rootNormalized = _root.Length > 2 && _root[1] == ':' ? _root[2..] : _root; // callbacks report paths without the drive letter
        _remote = remote;
        _engine = engine;
        _log = log;
        _fetch = FetchData;
        _delete = NotifyDelete;
        _rename = NotifyRename;
    }

    public void Connect()
    {
        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA, Callback = _fetch },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE, Callback = _delete },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME, Callback = _rename },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, Callback = null! }, // the table ends with an empty entry
        };
        fixed (CF_CONNECTION_KEY* key = &_key)
            PInvoke.CfConnectSyncRoot(_root, table, null,
                CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH, key).ThrowOnFailure();
        _connected = true;
    }

    public void Dispose()
    {
        if (_connected) PInvoke.CfDisconnectSyncRoot(_key);
        _connected = false;
    }

    private string Relative(PCWSTR normalizedPath)
    {
        var path = normalizedPath.ToString() ?? "";
        return path.StartsWith(_rootNormalized, StringComparison.OrdinalIgnoreCase) ? path[_rootNormalized.Length..].TrimStart('\\') : path;
    }

    // ---------- hydration ----------

    private void FetchData(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var key = info->ConnectionKey;
        var transfer = info->TransferKey;
        var offset = parameters->Anonymous.FetchData.RequiredFileOffset;
        var temp = Path.Combine(Path.GetTempPath(), "syncloom-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (id, version) = Identity.Decode(info->FileIdentity, info->FileIdentityLength);
            _remote.Download(id, version, temp);
            using var file = File.OpenRead(temp);
            var total = file.Length;
            // the platform accepts data in 4 KiB aligned pieces, and the last piece may end the file anywhere
            var position = offset & ~4095L;
            file.Seek(position, SeekOrigin.Begin);
            var buffer = new byte[Chunk];
            while (position < total)
            {
                var read = file.Read(buffer, 0, (int)Math.Min(Chunk, total - position));
                if (read <= 0) break;
                fixed (byte* p = buffer) Transfer(key, transfer, p, position, read, 0);
                position += read;
                PInvoke.CfReportProviderProgress(key, transfer, total, position);
            }
        }
        catch (Exception e)
        {
            _log($"Could not download the file: {e.Message}");
            try { Transfer(key, transfer, null, offset, parameters->Anonymous.FetchData.RequiredLength, StatusUnsuccessful); } catch { }
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static void Transfer(CF_CONNECTION_KEY key, long transferKey, byte* data, long offset, long length, int status)
    {
        var op = new CF_OPERATION_INFO { Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA, ConnectionKey = key, TransferKey = transferKey };
        op.StructSize = (uint)sizeof(CF_OPERATION_INFO);
        var p = new CF_OPERATION_PARAMETERS();
        p.Anonymous.TransferData = new CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct
        {
            Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
            CompletionStatus = new NTSTATUS(status),
            Buffer = data,
            Offset = offset,
            Length = length,
        };
        p.ParamSize = ParamSize(&p, sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct));
        PInvoke.CfExecute(&op, &p).ThrowOnFailure();
    }

    /// <summary>The platform wants the size up to the end of the member that is used (CF_SIZE_OF_OP_PARAM).</summary>
    private static uint ParamSize(CF_OPERATION_PARAMETERS* p, int memberSize) => (uint)((byte*)&p->Anonymous - (byte*)p) + (uint)memberSize;

    // ---------- Explorer deletes and renames ----------

    private void NotifyDelete(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var rel = Relative(info->NormalizedPath);
        var status = 0;
        try
        {
            var (id, _) = Identity.Decode(info->FileIdentity, info->FileIdentityLength);
            _engine.LocalDeleted(rel, id);
        }
        catch (Exception e) { _log($"Could not delete {rel} on the server: {e.Message}"); status = StatusUnsuccessful; } // Windows then keeps the file
        var op = new CF_OPERATION_INFO { Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE, ConnectionKey = info->ConnectionKey, TransferKey = info->TransferKey };
        op.StructSize = (uint)sizeof(CF_OPERATION_INFO);
        var p = new CF_OPERATION_PARAMETERS();
        p.Anonymous.AckDelete = new CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckDelete_e__Struct { CompletionStatus = new NTSTATUS(status) };
        p.ParamSize = ParamSize(&p, sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckDelete_e__Struct));
        PInvoke.CfExecute(&op, &p);
    }

    private void NotifyRename(CF_CALLBACK_INFO* info, CF_CALLBACK_PARAMETERS* parameters)
    {
        var from = Relative(info->NormalizedPath);
        var flags = parameters->Anonymous.Rename.Flags;
        var status = 0;
        try
        {
            var (id, _) = Identity.Decode(info->FileIdentity, info->FileIdentityLength);
            if ((flags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE) != 0)
                _engine.LocalRenamed(from, Relative(parameters->Anonymous.Rename.TargetPath), id);
            else
                _engine.LocalDeleted(from, id); // moved out of the sync root: gone from Syncloom
        }
        catch (Exception e) { _log($"Could not rename {from} on the server: {e.Message}"); status = StatusUnsuccessful; }
        var op = new CF_OPERATION_INFO { Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME, ConnectionKey = info->ConnectionKey, TransferKey = info->TransferKey };
        op.StructSize = (uint)sizeof(CF_OPERATION_INFO);
        var p = new CF_OPERATION_PARAMETERS();
        p.Anonymous.AckRename = new CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckRename_e__Struct { CompletionStatus = new NTSTATUS(status) };
        p.ParamSize = ParamSize(&p, sizeof(CF_OPERATION_PARAMETERS._Anonymous_e__Union._AckRename_e__Struct));
        PInvoke.CfExecute(&op, &p);
    }
}

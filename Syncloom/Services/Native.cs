using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Syncloom.Services;

/// <summary>P/Invoke wrapper over the C ABI of the Rust core (syncloom_core.dll): JSON in, JSON out.</summary>
public static class Native
{
    [DllImport("syncloom_core", CallingConvention = CallingConvention.Cdecl, EntryPoint = "syncloom_call")]
    private static extern IntPtr Call([MarshalAs(UnmanagedType.LPUTF8Str)] string request);

    [DllImport("syncloom_core", CallingConvention = CallingConvention.Cdecl, EntryPoint = "syncloom_free")]
    private static extern void Free(IntPtr p);

    public sealed class CoreException(string message) : Exception(message);

    /// <summary>Runs one operation on a worker thread and returns the "ok" payload.</summary>
    public static Task<JsonNode?> InvokeAsync(string op, object? args = null) => Task.Run(() => Invoke(op, args));

    public static JsonNode? Invoke(string op, object? args = null)
    {
        var req = JsonSerializer.SerializeToNode(args ?? new { })!.AsObject();
        req["op"] = op;
        var ptr = Call(req.ToJsonString());
        try
        {
            var text = Marshal.PtrToStringUTF8(ptr)!;
            var res = JsonNode.Parse(text)!.AsObject();
            if (res.TryGetPropertyValue("error", out var err) && err is not null) throw new CoreException(err.GetValue<string>());
            return res["ok"];
        }
        finally { Free(ptr); }
    }
}

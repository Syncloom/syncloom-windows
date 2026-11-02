using System.Text;

namespace Syncloom.CloudFiles;

/// <summary>What a placeholder remembers about its server file: the node id and the version it shows. Stored as the file identity.</summary>
internal static class Identity
{
    public static byte[] Encode(string id, string? version) => Encoding.UTF8.GetBytes(id + "\n" + (version ?? ""));

    public static (string Id, string? Version) Decode(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var i = text.IndexOf('\n');
        return i < 0 ? (text, null) : (text[..i], i + 1 < text.Length ? text[(i + 1)..] : null);
    }

    public static unsafe (string Id, string? Version) Decode(void* p, uint length) => Decode(new ReadOnlySpan<byte>(p, (int)length));
}

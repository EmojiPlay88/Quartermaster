// Ported from VASSAL DeobfuscatingInputStream.java (LGPL-2.1).
using System.Text;

namespace Quartermaster.Vassal;

/// <summary>
/// The hex "obfuscation" VASSAL (through 3.7) applied to saved games: the ASCII header <c>!VCSK</c>,
/// then the key as two hex digits, then every data byte XOR'd with the key and written as two hex digits.
/// Data without the header is plain text and passes through unchanged. Never throws on garbage:
/// malformed obfuscated data is returned unchanged.
/// </summary>
public static class Obfuscation
{
    private static readonly byte[] Header = "!VCSK"u8.ToArray();

    public static byte[] Deobfuscate(byte[] data)
    {
        // VASSAL requires more than the bare header (the key digits follow it).
        if (data.Length <= Header.Length || !data.AsSpan(0, Header.Length).SequenceEqual(Header)) return data;

        var body = data.AsSpan(Header.Length);
        if (body.Length < 2 || body.Length % 2 != 0) return data;

        var key = Pair(body[0], body[1]);
        if (key < 0) return data;

        var result = new byte[body.Length / 2 - 1];
        for (var i = 0; i < result.Length; i++)
        {
            var v = Pair(body[2 + 2 * i], body[3 + 2 * i]);
            if (v < 0) return data;
            result[i] = (byte)(v ^ key);
        }
        return result;
    }

    public static byte[] Obfuscate(byte[] plain, byte key)
    {
        var sb = new StringBuilder("!VCSK");
        sb.Append(key.ToString("x2"));
        foreach (var b in plain) sb.Append(((byte)(b ^ key)).ToString("x2"));
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static int Pair(byte hi, byte lo)
    {
        var h = Unhex(hi);
        var l = Unhex(lo);
        return h < 0 || l < 0 ? -1 : (h << 4) | l;
    }

    private static int Unhex(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        _ => -1,
    };
}

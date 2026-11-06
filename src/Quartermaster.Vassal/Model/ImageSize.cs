using System.Globalization;
using System.Text;
using System.Xml;

namespace Quartermaster.Vassal;

/// <summary>Reads pixel dimensions from image headers (PNG, JPEG, GIF) or SVG markup without decoding. Never throws.</summary>
public static class ImageSize
{
    public static SizeD? Read(string name, byte[] data)
    {
        try
        {
            if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
                return Pos(BE32(data, 16), BE32(data, 20));
            if (data.Length >= 10 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
                return Pos(data[6] | data[7] << 8, data[8] | data[9] << 8);
            if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8) return Jpeg(data);
            if (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || LooksLikeSvg(data)) return Svg(data);
        }
        catch (Exception) { }
        return null;
    }

    private static int BE32(byte[] d, int i) => d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];

    private static SizeD? Pos(int w, int h) => w > 0 && h > 0 ? new SizeD(w, h) : null;

    private static SizeD? Jpeg(byte[] d)
    {
        var i = 2;
        while (i + 9 < d.Length)
        {
            if (d[i] != 0xFF) { i++; continue; }
            var m = d[i + 1];
            if (m == 0xFF) { i++; continue; }
            if (m is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
            var len = d[i + 2] << 8 | d[i + 3];
            // SOF0..SOF15 except DHT (C4), JPG (C8), DAC (CC)
            if (m is >= 0xC0 and <= 0xCF && m is not (0xC4 or 0xC8 or 0xCC))
                return Pos(d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
            if (len < 2) return null;
            i += 2 + len;
        }
        return null;
    }

    private static bool LooksLikeSvg(byte[] d)
    {
        var n = Math.Min(d.Length, 512);
        return Encoding.UTF8.GetString(d, 0, n).Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static SizeD? Svg(byte[] data)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var r = XmlReader.Create(new MemoryStream(data), settings);
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (!r.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase)) return null;
            var w = Length(r.GetAttribute("width"));
            var h = Length(r.GetAttribute("height"));
            if (w > 0 && h > 0) return new SizeD(w, h);
            var vb = r.GetAttribute("viewBox");
            if (vb != null)
            {
                var p = vb.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4 && Num(p[2]) is double vw && Num(p[3]) is double vh && vw > 0 && vh > 0)
                    return new SizeD(w > 0 ? w : vw, h > 0 ? h : vh);
            }
            return null;
        }
        return null;
    }

    private static double Length(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        s = s.Trim();
        if (s.EndsWith("px", StringComparison.OrdinalIgnoreCase)) s = s[..^2];
        return Num(s) ?? 0;   // percentages / other units are unusable; the caller falls back to the viewBox
    }

    private static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}

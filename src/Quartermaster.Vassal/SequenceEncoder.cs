// Ported from VASSAL SequenceEncoder.java (LGPL-2.1).
using System.Globalization;
using System.Text;

namespace Quartermaster.Vassal;

/// <summary>
/// Encodes a sequence of strings into one string with a given delimiter, escaping the delimiter
/// inside tokens as backslash + delimiter. Tokens that start with a backslash, or that start and
/// end with a single quote, are wrapped in single quotes.
/// </summary>
public static class SequenceEncoder
{
    public static string Encode(IEnumerable<string?> tokens, char delim)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var token in tokens)
        {
            if (!first) sb.Append(delim);
            first = false;
            Append(sb, token, delim);
        }
        return sb.ToString();
    }

    public static IReadOnlyList<string> Decode(string? value, char delim)
    {
        var result = new List<string>();
        var decoder = new SequenceDecoder(value, delim);
        while (decoder.HasMoreTokens) result.Add(decoder.NextToken());
        return result;
    }

    private static void Append(StringBuilder sb, string? s, char delim)
    {
        if (string.IsNullOrEmpty(s)) return;

        // VASSAL quotes on a leading backslash (not a trailing one), or on a leading and trailing quote.
        var quote = s[0] == '\\' || (s[0] == '\'' && s[^1] == '\'');
        if (quote) sb.Append('\'');
        foreach (var c in s)
        {
            if (c == delim) sb.Append('\\');
            sb.Append(c);
        }
        if (quote) sb.Append('\'');
    }
}

/// <summary>
/// Reads tokens written by <see cref="SequenceEncoder"/>. Never throws: a missing or unparsable
/// token yields the supplied default.
/// </summary>
public sealed class SequenceDecoder(string? value, char delim)
{
    private string? _val = value;
    private readonly int _stop = value?.Length ?? 0;
    private int _start;
    private StringBuilder? _buf;

    public bool HasMoreTokens => _val != null;

    /// <summary>Everything not yet consumed, raw and unescaped (VASSAL Decoder.getRemaining); "" when exhausted.</summary>
    public string GetRemaining() => _val == null ? "" : _val.Substring(_start, _stop - _start);

    public string NextToken(string def = "")
    {
        return _val != null ? ReadToken() : def;
    }

    public int NextInt(int def)
    {
        if (_val == null) return def;
        return int.TryParse(ReadToken(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var r) ? r : def;
    }

    public double NextDouble(double def)
    {
        if (_val == null) return def;
        return double.TryParse(ReadToken().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : def;
    }

    public bool NextBoolean(bool def)
    {
        return _val != null ? ReadToken() == "true" : def;
    }

    /// <summary>Next token read as a comma-list (VASSAL StringArrayConfigurer), padded with "" to at least <paramref name="minLength"/>.</summary>
    public string[] NextStringArray(int minLength)
    {
        var list = _val != null ? StringToArray(ReadToken()) : new List<string>();
        while (list.Count < minLength) list.Add("");
        return list.ToArray();
    }

    private static List<string> StringToArray(string s)
    {
        if (s.Length == 0) return new List<string>();
        return new List<string>(SequenceEncoder.Decode(s, ','));
    }

    private string ReadToken()
    {
        var val = _val!;
        if (_start == _stop)
        {
            // token for "null" is the empty string
            _val = null;
            return "";
        }

        _buf?.Clear();

        string? tok = null;
        var i = _start;
        for (; i < _stop; ++i)
        {
            if (val[i] != delim) continue;

            if (i > 0 && val[i - 1] == '\\')
            {
                // escaped delimiter; piece together the token
                _buf ??= new StringBuilder();
                _buf.Append(val, _start, i - 1 - _start);
                _start = i;
            }
            else
            {
                // real delimiter
                if (_buf == null || _buf.Length == 0)
                {
                    tok = val.Substring(_start, i - _start);
                }
                else
                {
                    _buf.Append(val, _start, i - _start);
                }
                _start = i + 1;
                break;
            }
        }

        if (_start < i)
        {
            // reached the end without a delimiter
            if (_buf == null || _buf.Length == 0)
            {
                tok = val.Substring(_start);
            }
            else
            {
                _buf.Append(val, _start, _stop - _start);
            }
            _val = null;
        }

        return Unquote(tok ?? _buf!.ToString());
    }

    private static string Unquote(string s)
        => s.Length > 1 && s[0] == '\'' && s[^1] == '\'' ? s.Substring(1, s.Length - 2) : s;
}

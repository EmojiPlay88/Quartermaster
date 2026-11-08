using System.Text;

namespace Quartermaster.Desktop.Shell;

/// <summary>
/// Menu labels are written exactly as in the WinForms app ("&amp;Open Module...", "&amp;&amp;" for a literal ampersand). Avalonia marks the
/// access key with "_" instead, so labels are converted on the way in and back for checks.
/// </summary>
public static class MenuText
{
    /// <summary>"&amp;Open Module..." → "_Open Module..."; "&amp;&amp;" → "&amp;"; a literal "_" → "__".</summary>
    public static string FromWinForms(string text)
    {
        var sb = new StringBuilder(text.Length + 2);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '&')
            {
                if (i + 1 < text.Length && text[i + 1] == '&') { sb.Append('&'); i++; }
                else sb.Append('_');
            }
            else if (c == '_') sb.Append("__");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>The inverse of <see cref="FromWinForms"/>.</summary>
    public static string ToWinForms(string header)
    {
        var sb = new StringBuilder(header.Length + 2);
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == '_')
            {
                if (i + 1 < header.Length && header[i + 1] == '_') { sb.Append('_'); i++; }
                else sb.Append('&');
            }
            else if (c == '&') sb.Append("&&");
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

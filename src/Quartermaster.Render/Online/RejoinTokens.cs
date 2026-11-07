using Quartermaster.Net;

namespace Quartermaster.Render;

/// <summary>
/// The rejoin token a host gave this guest, one small file per game code under <c>{root}\online\</c> (root: the layout root,
/// QUARTERMASTER_LAYOUT_ROOT in tests). Written atomically; failures are ignored (no token = join as a new player). Tokens are never logged.
/// </summary>
public sealed class RejoinTokens(string root)
{
    private const int MaxToken = 256;

    /// <summary>%LOCALAPPDATA%\Quartermaster, redirected by QUARTERMASTER_LAYOUT_ROOT.</summary>
    public static string DefaultRoot => AppDataRoot.Path;

    public string Root => root;

    private string? PathFor(string code)
    {
        var c = GameCode.Normalize(code ?? "");
        if (c.Length == 0 || c.Any(ch => !char.IsAsciiLetterOrDigit(ch))) return null;
        return Path.Combine(root, "online", "rejoin-" + c + ".token");
    }

    public string? Get(string code)
    {
        if (PathFor(code) is not { } path) return null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxToken) return null;
            var t = File.ReadAllText(path).Trim();
            return t.Length is > 0 and <= MaxToken && t.All(char.IsAsciiLetterOrDigit) ? t : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Set(string code, string token)
    {
        if (PathFor(code) is not { } path || string.IsNullOrEmpty(token) || token.Length > MaxToken || !token.All(char.IsAsciiLetterOrDigit)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            try
            {
                File.WriteAllText(tmp, token);
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Forget(string code)
    {
        if (PathFor(code) is not { } path) return;
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

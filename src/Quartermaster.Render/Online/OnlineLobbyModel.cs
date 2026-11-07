using System.Globalization;
using Quartermaster.Core;
using Quartermaster.Net;
using Quartermaster.Rules;

namespace Quartermaster.Render;

/// <summary>
/// What the launcher hands the game window for an online game (launcher spec 3.3, 4a). For a join, <see cref="Client"/> is the relay
/// client that already joined the room (the lobby window checked the password) and <see cref="Code"/> its game code; for hosting, the
/// game window connects and creates the room itself. Holds the password in memory only: <see cref="ToString"/> never prints it.
/// </summary>
public sealed record OnlineLaunch(Uri Relay, string PlayerName, string Password, string? LobbyName = null,
    IReadOnlyList<string>? HostSides = null, string? Code = null, LobbyListing? Listing = null, RelayClient? Client = null,
    CertificateTrust? Trust = null)
{
    public override string ToString() => $"OnlineLaunch {{ Relay = {Relay}, Code = {Code ?? "(new)"} }}";
}

/// <summary>
/// The certificate question of a relay client, re-pointable: a client keeps the callback it connected with for its whole life
/// (reconnects included), so the window that owns the client now (lobby window, then game window) is set here.
/// </summary>
public sealed class CertificateTrust
{
    private Func<CertificateCheck, bool> _ask = _ => false;

    public Func<CertificateCheck, bool> Ask
    {
        get => _ask;
        set => _ask = value ?? (_ => false);
    }

    /// <summary>The callback to connect with; never throws (a throwing question is a no).</summary>
    public bool Check(CertificateCheck c)
    {
        try { return _ask(c); }
        catch (Exception) { return false; }
    }
}

/// <summary>One row of the Online lobby table.</summary>
public sealed record LobbyRow(LobbyListing Listing, string Lobby, string Host, string FreeSides, string Players, bool Locked, string? CannotJoin);

/// <summary>
/// The logic behind the launcher's Online lobby window and the online game's status texts (launcher spec 3.3, 3.4, 4a). UI-free; never
/// throws on bad input. The windows only render what this returns.
/// </summary>
public static class OnlineLobbyModel
{
    public const string WindowTitlePrefix = "Online lobby — ";
    public const string OpenLobbyWarning = "Without a password, the relay can read this game.";
    public const string JoinNote = "The host's copy runs the game.";
    public const string DifferentModuleText = "This save belongs to a different module";
    public const string HostLeftText = "Host disconnected — waiting for the host to return.";
    public const string CertChangedTitle = "The relay's certificate has changed";
    public const string CertNewTitle = "Trust this relay?";
    public const string NoAddressText = "Enter the relay address (host or host:port).";
    public const string BadAddressText = "That is not a relay address. Use host, host:port or wss://host:port.";
    public const string InsecureText = "The relay needs a secure address (wss://).";
    public const string NeedsPackText = "Online play needs a rules pack with at least two sides. Choose one for this module first.";
    public const string NoOwnSideText = "Choose at least one side for yourself.";
    public const string NoOpenSideText = "Leave at least one side open for other players.";
    public const string FullText = "This lobby is full.";
    public const string ViewerCopyText = "A viewer copy cannot be hosted.";

    /// <summary>The window title for a module.</summary>
    public static string Title(string moduleTitle) => WindowTitlePrefix + moduleTitle;

    /// <summary>Parses the relay field: <c>host</c>, <c>host:port</c>, <c>wss://host[:port][/path]</c> (https is read as wss). The port defaults to 47820.</summary>
    public static Uri? ParseRelay(string? text, out string? error)
    {
        error = null;
        var t = (text ?? "").Trim();
        if (t.Length == 0) { error = NoAddressText; return null; }
        var hasScheme = t.Contains("://", StringComparison.Ordinal);
        if (!hasScheme) t = "wss://" + t;
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) || uri.Host.Contains(' ', StringComparison.Ordinal))
        {
            error = BadAddressText;
            return null;
        }
        var scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is "ws" or "http") { error = InsecureText; return null; }
        if (scheme is not ("wss" or "https")) { error = BadAddressText; return null; }
        var explicitPort = !uri.IsDefaultPort || ExplicitPort(t);
        var b = new UriBuilder(uri) { Scheme = "wss", Port = explicitPort ? uri.Port : Protocol.DefaultPort };
        if (string.IsNullOrEmpty(b.Path) || b.Path == "/") b.Path = "/qm";
        b.Query = "";
        b.Fragment = "";
        return b.Uri;
    }

    /// <summary>True when the text names the port after the host (so 443 written out is kept).</summary>
    private static bool ExplicitPort(string withScheme)
    {
        var rest = withScheme[(withScheme.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end = rest.IndexOfAny(new[] { '/', '?', '#' });
        var authority = end < 0 ? rest : rest[..end];
        var bracket = authority.LastIndexOf(']');
        var colon = authority.LastIndexOf(':');
        return colon > bracket && colon < authority.Length - 1;
    }

    /// <summary>"host:port" for display.</summary>
    public static string HostPort(Uri relay) => relay.IsDefaultPort ? relay.Host : $"{relay.Host}:{relay.Port.ToString(CultureInfo.InvariantCulture)}";

    // ---- own lobbies ----

    private static readonly HashSet<string> Hosted = new(StringComparer.Ordinal);

    /// <summary>
    /// Remembers a code this program hosts, so its own lobby is never listed to itself (launcher spec 4a.7). Per process on purpose:
    /// nothing is stored, so a second copy of Quartermaster on the same computer (or a host restart) does see the lobby, exactly as
    /// another player would.
    /// </summary>
    public static void MarkHosted(string code) { lock (Hosted) Hosted.Add(GameCode.Normalize(code ?? "")); }

    public static void UnmarkHosted(string code) { lock (Hosted) Hosted.Remove(GameCode.Normalize(code ?? "")); }

    public static IReadOnlySet<string> HostedCodes { get { lock (Hosted) return new HashSet<string>(Hosted, StringComparer.Ordinal); } }

    // ---- the table ----

    /// <summary>The lobbies to show: same module (by SHA-256), not one of <paramref name="ownCodes"/>; open seats first, then by name.</summary>
    public static IReadOnlyList<LobbyRow> Rows(IEnumerable<LobbyListing?>? listings, string? moduleSha, IReadOnlySet<string>? ownCodes = null)
    {
        var rows = new List<LobbyRow>();
        if (listings == null || string.IsNullOrEmpty(moduleSha)) return rows;
        foreach (var l in listings)
        {
            if (l == null || !string.Equals(l.ModuleSha256, moduleSha, StringComparison.OrdinalIgnoreCase)) continue;
            if (ownCodes != null && ownCodes.Contains(GameCode.Normalize(l.Code ?? ""))) continue;
            // Listings are untrusted (anyone can publish one): every shown text is cleaned and bounded.
            var free = (l.FreeSides ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var freeText = string.Join(", ", free.Take(MaxFreeSidesShown).Select(x => Clip(x, 40)));
            if (free.Count > MaxFreeSidesShown) freeText += ", …";
            rows.Add(new LobbyRow(l, Clip(l.Name, NewGameRules.MaxLobbyName), Clip(l.HostName, ChatText.MaxName),
                free.Count == 0 ? "—" : freeText,
                $"{Math.Max(0, l.Players).ToString(CultureInfo.InvariantCulture)}/{Math.Max(0, l.MaxPlayers).ToString(CultureInfo.InvariantCulture)}",
                l.Locked, CannotJoin(l)));
        }
        return rows.OrderBy(r => r.CannotJoin == null ? 0 : 1).ThenBy(r => r.Lobby, StringComparer.CurrentCultureIgnoreCase).Take(MaxRows).ToList();
    }

    /// <summary>Most rows the table shows; most free sides one row names.</summary>
    public const int MaxRows = 200, MaxFreeSidesShown = 16;

    /// <summary>Text from a listing (or another player) made safe to show: control characters removed, at most <paramref name="max"/> characters.</summary>
    public static string CleanText(string? s, int max = NewGameRules.MaxLobbyName) => Clip(s, max);

    /// <summary>Why this lobby cannot be joined now (null = it can): another network version, or no free side / no free place.</summary>
    public static string? CannotJoin(LobbyListing l)
    {
        if (l.Version != Protocol.ProtocolVersion) return Protocol.MismatchMessage(l.Version);
        if (l.FreeSides is not { Count: > 0 } || (l.MaxPlayers > 0 && l.Players >= l.MaxPlayers)) return FullText;
        if (!GameCode.IsValid(GameCode.Normalize(l.Code ?? ""))) return BadAddressText;
        return null;
    }

    private static string Clip(string? s, int max)
    {
        var t = ChatText.Clean(s) ?? "";
        return t.Length > max ? t[..max] : t;
    }

    // ---- hosting ----

    /// <summary>Null when the host may take these sides of the pack (at least one, and at least one left open), else the problem.</summary>
    public static string? ValidateHostSides(RulesPack? pack, IReadOnlyCollection<string>? hostSides)
    {
        var ids = (pack?.Sides ?? new List<SideDef>()).Where(x => !string.IsNullOrEmpty(x?.Id)).Select(x => x.Id).ToList();
        if (ids.Count < 2) return NeedsPackText;
        var mine = (hostSides ?? Array.Empty<string>()).Distinct().ToList();
        if (mine.Count == 0) return NoOwnSideText;
        if (mine.Any(x => !ids.Contains(x))) return Seats.NotASideText;
        if (mine.Count >= ids.Count) return NoOpenSideText;
        return null;
    }

    /// <summary>
    /// The listing the room is created with: every pack side not held by the host is an open seat (by name; launcher spec 4a.8);
    /// at most one place per open side plus the host's.
    /// </summary>
    public static LobbyListing Draft(string code, string lobbyName, string hostName, string moduleSha, string moduleTitle,
        RulesPack pack, IReadOnlyCollection<string> hostSides, bool locked)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var sides = pack.Sides.Where(x => !string.IsNullOrEmpty(x?.Id)).ToList();
        string NameOf(SideDef s) => string.IsNullOrEmpty(s.Name) ? s.Id : s.Name;
        var free = sides.Where(x => !hostSides.Contains(x.Id)).Select(NameOf).ToList();
        var name = (ChatText.Clean(lobbyName) ?? "Lobby");
        if (name.Length > NewGameRules.MaxLobbyName) name = name[..NewGameRules.MaxLobbyName];
        return new LobbyListing(GameCode.Normalize(code ?? ""), name, ChatText.CleanName(hostName) ?? "Host", moduleSha, Clip(moduleTitle, 80),
            sides.Select(NameOf).ToList(), free, 1, Math.Clamp(1 + free.Count, 2, Players.MaxPlayers), locked, Protocol.ProtocolVersion);
    }

    /// <summary>Null when the save at <paramref name="path"/> may be hosted with this module, else the problem (never throws).</summary>
    public static string? CheckSave(string path, string? moduleSha)
    {
        GameSession g;
        try { g = NativeSave.Read(path); }
        catch (Exception e) when (e is not OutOfMemoryException) { return "That saved game could not be read: " + e.Message; }
        if (g.ViewerCopy) return ViewerCopyText;
        if (string.IsNullOrEmpty(moduleSha) || !string.Equals(g.ModuleSha256, moduleSha, StringComparison.OrdinalIgnoreCase)) return DifferentModuleText;
        return null;
    }

    /// <summary>The sides held by the save's first player (preselected for the host), or the pack's first side.</summary>
    public static IReadOnlyList<string> SuggestedHostSides(string? savePath, RulesPack? pack)
    {
        var ids = (pack?.Sides ?? new List<SideDef>()).Where(x => !string.IsNullOrEmpty(x?.Id)).Select(x => x.Id).ToList();
        if (savePath != null)
        {
            try
            {
                var p = NativeSave.Read(savePath).State.Players?.FirstOrDefault()?.Sides?.Where(ids.Contains).ToList();
                if (p is { Count: > 0 } && p.Count < ids.Count) return p;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        return ids.Take(1).ToList();
    }

    // ---- status texts ----

    /// <summary>The status bar text: <c>Online: {code} · {n} players</c>, plus the connection state when it is not fine.</summary>
    public static string StatusText(string code, int players, string? trouble = null)
    {
        var t = $"Online: {code} · {Math.Max(1, players).ToString(CultureInfo.InvariantCulture)} players";
        return string.IsNullOrWhiteSpace(trouble) ? t : t + " — " + trouble;
    }

    /// <summary>What a guest's banner shows for its state (null = no banner).</summary>
    public static string? GuestBanner(GuestState state, string? statusText) => state switch
    {
        GuestState.Paused => statusText == GuestSession.HostGoneText ? HostLeftText : statusText ?? GuestSession.NotPlayingText,
        GuestState.Joining when statusText != null => statusText,
        _ => null,
    };

    /// <summary>Title, message and whether it is a changed certificate (a warning) for the trust prompt.</summary>
    public static (string Title, string Message, bool Changed) CertificatePrompt(CertificateCheck c)
    {
        var now = Group(c.Fingerprint);
        if (c.PinnedFingerprint is { Length: > 0 } old)
            return (CertChangedTitle,
                $"The certificate of the relay {c.HostPort} is not the one you trusted before.\n\nBefore: {Group(old)}\nNow: {now}\n\n" +
                "This happens when the relay was reinstalled, but it can also mean someone is listening in between. " +
                "Trust the new certificate only if the relay's owner confirms this fingerprint.", true);
        return (CertNewTitle,
            $"The relay {c.HostPort} uses its own certificate (not one from a public authority).\n\nFingerprint (SHA-256):\n{now}\n\n" +
            "Trust it if it matches the fingerprint the relay's owner gave you. Quartermaster remembers it for this relay.", false);
    }

    private static string Group(string hex)
    {
        var h = (hex ?? "").ToUpperInvariant();
        return string.Join(" ", Enumerable.Range(0, (h.Length + 3) / 4).Select(i => h.Substring(i * 4, Math.Min(4, h.Length - i * 4))));
    }
}

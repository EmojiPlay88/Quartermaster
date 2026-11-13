using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Quartermaster.Render;

/// <summary>Protects the mailbox password at rest (per Windows user with DPAPI; an OS secret store elsewhere).</summary>
public interface IPasswordProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedBytes);

    /// <summary>Drops whatever is kept outside the protected bytes (a secret-store entry); nothing for DPAPI.</summary>
    void Forget(byte[] blob) { }
}

/// <summary>
/// Windows DPAPI for the current user (<c>CryptProtectData</c>, the API behind <c>ProtectedData</c> with
/// <c>DataProtectionScope.CurrentUser</c>): only this Windows account can read the password back.
/// </summary>
public sealed class DpapiProtector : IPasswordProtector
{
    public static readonly DpapiProtector Instance = new();

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Quartermaster turn mailbox");

    public byte[] Protect(byte[] plain) => Run(plain, protect: true);

    public byte[] Unprotect(byte[] protectedBytes) => Run(protectedBytes, protect: false);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Run(byte[] input, bool protect)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saved mailbox passwords need Windows.");
        var inPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entPin = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var dataIn = new DataBlob { Size = input.Length, Data = inPin.AddrOfPinnedObject() };
            var entropy = new DataBlob { Size = Entropy.Length, Data = entPin.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref dataIn, "Quartermaster", ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            if (output.Size > 0) Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            inPin.Free();
            entPin.Free();
        }
    }
}

/// <summary>
/// What this computer remembers about one play-by-email game (<c>&lt;AppDataRoot&gt;/turns/{GameId}.json</c>): which player is at this
/// computer, the relay and mailbox choice, the protected mailbox password and the start hashes of turns already replayed here.
/// Written atomically; an unreadable file reads as empty. Never holds the game itself.
/// </summary>
public sealed class TurnIdentity
{
    /// <summary>At most this many played start hashes are remembered (the oldest are dropped).</summary>
    public const int MaxPlayed = 500;

    private const int MaxGameIdLength = 64;

    /// <summary>A larger identity file is not read (it reads as empty).</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private readonly List<string> _played = new();
    private readonly HashSet<string> _playedSet = new(StringComparer.Ordinal);
    private readonly IPasswordProtector _protector;

    private TurnIdentity(string gameId, string filePath, IPasswordProtector protector)
    {
        GameId = gameId;
        FilePath = filePath;
        _protector = protector;
    }

    public string GameId { get; }

    /// <summary>Where <see cref="Save"/> writes.</summary>
    public string FilePath { get; }

    public string? LocalPlayerId { get; set; }
    public string? Relay { get; set; }
    public bool UseMailbox { get; set; }

    /// <summary>The mailbox password protected by the <see cref="IPasswordProtector"/> (DPAPI on Windows, a secret-store handle
    /// elsewhere); null = none saved.</summary>
    public byte[]? ProtectedPassword { get; set; }

    /// <summary>True when protected password bytes are kept (they may still not be readable here: see <see cref="GetPassword"/>).</summary>
    public bool HasStoredPassword => ProtectedPassword is { Length: > 0 };

    /// <summary>Start hashes of turns already replayed here (newest last; capped at <see cref="MaxPlayed"/>).</summary>
    public IReadOnlySet<string> PlayedStartHashes => _playedSet;

    private readonly List<string> _sent = new();
    private readonly HashSet<string> _sentSet = new(StringComparer.Ordinal);

    /// <summary>Start hashes of turns this computer sent (newest last; capped at <see cref="MaxPlayed"/>): such a turn coming back is an older position.</summary>
    public IReadOnlySet<string> SentStartHashes => _sentSet;

    /// <summary>Remembers that the turn starting at <paramref name="startHash"/> was sent from here (the oldest beyond the cap are dropped).</summary>
    public void RecordSent(string startHash)
    {
        ArgumentNullException.ThrowIfNull(startHash);
        Remember(_sent, _sentSet, startHash);
    }

    private static void Remember(List<string> list, HashSet<string> set, string hash)
    {
        if (!set.Add(hash)) return;
        list.Add(hash);
        while (list.Count > MaxPlayed)
        {
            set.Remove(list[0]);
            list.RemoveAt(0);
        }
    }

    /// <summary>Union of what is on disk and in memory (newest last), capped; the in-memory list becomes that union.</summary>
    private static List<string> Merge(IEnumerable<string>? onDisk, List<string> list, HashSet<string> set)
    {
        var merged = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var h in (onDisk ?? Array.Empty<string>()).Concat(list))
            if (h != null && seen.Add(h)) merged.Add(h);
        if (merged.Count > MaxPlayed) merged.RemoveRange(0, merged.Count - MaxPlayed);
        list.Clear();
        set.Clear();
        foreach (var h in merged) Remember(list, set, h);
        return merged;
    }

    /// <summary>&lt;AppDataRoot&gt;/turns (honours QUARTERMASTER_LAYOUT_ROOT).</summary>
    public static string DefaultRoot => AppDataRoot.Sub("turns");

    /// <summary>True for an id that is safe as a file name: 1–64 hex digits and dashes (a GUID in any common form).</summary>
    public static bool IsValidGameId(string? gameId) =>
        !string.IsNullOrEmpty(gameId) && gameId.Length <= MaxGameIdLength && gameId.All(c => char.IsAsciiHexDigit(c) || c == '-');

    /// <summary>Loads the identity of a game (empty when none is stored or the file cannot be read).</summary>
    /// <exception cref="InvalidDataException">The game id is not a valid id (it would not be safe as a file name).</exception>
    public static TurnIdentity Load(string gameId) => Load(gameId, null, null);

    /// <param name="root">Folder of the identity files; null = <see cref="DefaultRoot"/>.</param>
    /// <param name="protector">Password protector; null = <see cref="PasswordProtectors.ForThisOs"/>.</param>
    public static TurnIdentity Load(string gameId, string? root, IPasswordProtector? protector)
    {
        if (!IsValidGameId(gameId)) throw new InvalidDataException("This game has an invalid game id.");
        var id = new TurnIdentity(gameId, Path.Combine(root ?? DefaultRoot, gameId.ToLowerInvariant() + ".json"),
            protector ?? PasswordProtectors.ForThisOs());
        if (ReadDto(id.FilePath) is not { } dto) return id;
        id.LocalPlayerId = dto.LocalPlayerId;
        id.Relay = dto.Relay;
        id.UseMailbox = dto.UseMailbox;
        id.ProtectedPassword = dto.ProtectedPassword;
        foreach (var h in dto.PlayedStartHashes ?? new List<string>())
            if (h != null) id.RecordPlayed(h);
        foreach (var h in dto.SentStartHashes ?? new List<string>())
            if (h != null) id.RecordSent(h);
        return id;
    }

    /// <summary>The stored file, or null when it is missing, larger than <see cref="MaxFileBytes"/> or unreadable (then forgotten).</summary>
    private static Dto? ReadDto(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            return JsonSerializer.Deserialize<Dto>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the identity (unique temp file, then replace). Played start hashes already on disk are kept (union, newest last, capped
    /// at <see cref="MaxPlayed"/>), so two windows of the same game never erase each other's. Throws on I/O errors.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var disk = ReadDto(FilePath);
        var played = Merge(disk?.PlayedStartHashes, _played, _playedSet);
        var sent = Merge(disk?.SentStartHashes, _sent, _sentSet);
        var dto = new Dto(LocalPlayerId, Relay, UseMailbox, ProtectedPassword, played, sent.Count == 0 ? null : sent);
        var tmp = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(dto, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Remembers that the turn starting at <paramref name="startHash"/> was replayed here (the oldest beyond the cap are dropped).</summary>
    public void RecordPlayed(string startHash)
    {
        ArgumentNullException.ThrowIfNull(startHash);
        Remember(_played, _playedSet, startHash);
    }

    /// <summary>
    /// Saves a mailbox password (protected); null or empty clears it. False (never throws) when it cannot be stored: then nothing is
    /// saved, or, with a session-only store, it is kept until the app closes. The previous password's store entry is forgotten.
    /// </summary>
    public bool SetPassword(string? password)
    {
        var old = ProtectedPassword;
        bool stored;
        if (string.IsNullOrEmpty(password)) { ProtectedPassword = null; stored = true; }
        else
        {
            try
            {
                ProtectedPassword = _protector.Protect(Encoding.UTF8.GetBytes(password));
                stored = true;
            }
            catch (KeptForSessionException session)
            {
                ProtectedPassword = session.Handle;
                stored = false;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ProtectedPassword = null;
                stored = false;
            }
        }
        if (old is { Length: > 0 } && (ProtectedPassword == null || !old.AsSpan().SequenceEqual(ProtectedPassword)))
        {
            try { _protector.Forget(old); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        return stored;
    }

    /// <summary>The saved mailbox password, or null when none is saved or it cannot be read back (another Windows user, damaged file).</summary>
    public string? GetPassword()
    {
        if (ProtectedPassword is not { Length: > 0 } p) return null;
        try { return Encoding.UTF8.GetString(_protector.Unprotect(p)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>SentStartHashes is optional (files written before it existed read as none) and omitted when empty.</summary>
    private sealed record Dto(string? LocalPlayerId, string? Relay, bool UseMailbox, byte[]? ProtectedPassword, List<string>? PlayedStartHashes,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        List<string>? SentStartHashes = null);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Quartermaster.Render;

/// <summary>Protects the mailbox password at rest (per Windows user by default).</summary>
public interface IPasswordProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedBytes);
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

    /// <summary>The mailbox password protected by the <see cref="IPasswordProtector"/> (DPAPI by default); null = none saved.</summary>
    public byte[]? ProtectedPassword { get; set; }

    /// <summary>Start hashes of turns already replayed here (newest last; capped at <see cref="MaxPlayed"/>).</summary>
    public IReadOnlySet<string> PlayedStartHashes => _playedSet;

    /// <summary>&lt;AppDataRoot&gt;/turns (honours QUARTERMASTER_LAYOUT_ROOT).</summary>
    public static string DefaultRoot => AppDataRoot.Sub("turns");

    /// <summary>True for an id that is safe as a file name: 1–64 hex digits and dashes (a GUID in any common form).</summary>
    public static bool IsValidGameId(string? gameId) =>
        !string.IsNullOrEmpty(gameId) && gameId.Length <= MaxGameIdLength && gameId.All(c => char.IsAsciiHexDigit(c) || c == '-');

    /// <summary>Loads the identity of a game (empty when none is stored or the file cannot be read).</summary>
    /// <exception cref="InvalidDataException">The game id is not a valid id (it would not be safe as a file name).</exception>
    public static TurnIdentity Load(string gameId) => Load(gameId, null, null);

    /// <param name="root">Folder of the identity files; null = <see cref="DefaultRoot"/>.</param>
    /// <param name="protector">Password protector; null = DPAPI for the current user.</param>
    public static TurnIdentity Load(string gameId, string? root, IPasswordProtector? protector)
    {
        if (!IsValidGameId(gameId)) throw new InvalidDataException("This game has an invalid game id.");
        var id = new TurnIdentity(gameId, Path.Combine(root ?? DefaultRoot, gameId.ToLowerInvariant() + ".json"),
            protector ?? DpapiProtector.Instance);
        try
        {
            if (!File.Exists(id.FilePath)) return id;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllBytes(id.FilePath));
            if (dto == null) return id;
            id.LocalPlayerId = dto.LocalPlayerId;
            id.Relay = dto.Relay;
            id.UseMailbox = dto.UseMailbox;
            id.ProtectedPassword = dto.ProtectedPassword;
            foreach (var h in dto.PlayedStartHashes ?? new List<string>())
                if (h != null) id.RecordPlayed(h);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // An unreadable identity is forgotten: the player is asked again.
        }
        return id;
    }

    /// <summary>Writes the identity (temp file, then replace). Throws on I/O errors.</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(dir);
        var dto = new Dto(LocalPlayerId, Relay, UseMailbox, ProtectedPassword, _played.ToList());
        var tmp = FilePath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(dto, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>Remembers that the turn starting at <paramref name="startHash"/> was replayed here (the oldest beyond the cap are dropped).</summary>
    public void RecordPlayed(string startHash)
    {
        ArgumentNullException.ThrowIfNull(startHash);
        if (!_playedSet.Add(startHash)) return;
        _played.Add(startHash);
        while (_played.Count > MaxPlayed)
        {
            _playedSet.Remove(_played[0]);
            _played.RemoveAt(0);
        }
    }

    /// <summary>Saves a mailbox password (protected); null or empty clears it.</summary>
    public void SetPassword(string? password) =>
        ProtectedPassword = string.IsNullOrEmpty(password) ? null : _protector.Protect(Encoding.UTF8.GetBytes(password));

    /// <summary>The saved mailbox password, or null when none is saved or it cannot be read back (another Windows user, damaged file).</summary>
    public string? GetPassword()
    {
        if (ProtectedPassword is not { Length: > 0 } p) return null;
        try { return Encoding.UTF8.GetString(_protector.Unprotect(p)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    private sealed record Dto(string? LocalPlayerId, string? Relay, bool UseMailbox, byte[]? ProtectedPassword, List<string>? PlayedStartHashes);
}

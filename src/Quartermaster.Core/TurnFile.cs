using System.IO.Compression;
using System.Text.Json;

namespace Quartermaster.Core;

/// <summary>
/// A play-by-email turn (.qturn): the whole game at the start of the sender's turn, the sender's commands (dice results included,
/// so replay never rerolls) and the hash of the sender's end state. Hidden information is only hidden on screen: the file holds everything.
/// </summary>
public sealed record TurnFile(
    int Version, string ModuleSha256, string? RulesSha256, string GameId, string FromPlayerId, string FromName, string? ToPlayerId,
    DateTime CreatedUtc, GameState Start, string StartHash, IReadOnlyList<GameCommand> Commands, string EndHash);

/// <summary>Creates, writes, reads and replays <see cref="TurnFile"/>s (JSON, gzip).</summary>
public static class TurnFiles
{
    public const int Version = 1;
    /// <summary>Largest uncompressed turn file accepted.</summary>
    public const long MaxPlain = 64L * 1024 * 1024;
    public const int MaxCommands = 100_000;

    sealed record VersionProbe(int Version);

    /// <summary>The local player's turn so far: the state at <see cref="GameSession.TurnBase"/> plus every command since. <paramref name="g"/> is not changed.</summary>
    public static TurnFile Create(GameSession g, string fromPlayerId, string fromName, string? toPlayerId)
    {
        ArgumentNullException.ThrowIfNull(g);
        int from = g.TurnBase;
        var commands = g.Log.Skip(from).ToList();
        // A turn base above 0 means a turn was sent from here before: an empty turn then just resends the current game.
        if (commands.Count == 0 && from == 0) throw new InvalidOperationException("Nothing to send yet.");
        if (commands.Count > MaxCommands) throw new InvalidOperationException("This turn has too many steps to send.");
        var start = g.StateAt(from);
        return new TurnFile(Version, g.ModuleSha256, g.RulesSha256, g.State.GameId ?? "", fromPlayerId, fromName, toPlayerId,
            DateTime.UtcNow, start, StateHash.Of(start), commands, StateHash.Of(g.State));
    }

    public static byte[] ToBytes(TurnFile t)
    {
        ArgumentNullException.ThrowIfNull(t);
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, t, QuartermasterJson.Options);
        return ms.ToArray();
    }

    public static void Write(string path, TurnFile t)
    {
        var bytes = ToBytes(t);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads a turn file; anything malformed, too large or of another version throws <see cref="InvalidDataException"/>.</summary>
    public static TurnFile Read(string path)
    {
        if (new FileInfo(path).Length > MaxPlain) throw new InvalidDataException("This turn file is too large.");
        return FromBytes(File.ReadAllBytes(path));
    }

    public static TurnFile FromBytes(byte[] gz)
    {
        ArgumentNullException.ThrowIfNull(gz);
        byte[] plain = Gunzip(gz);
        TurnFile? t;
        try
        {
            var probe = JsonSerializer.Deserialize<VersionProbe>(plain, QuartermasterJson.Options);
            if (probe == null) throw new InvalidDataException("Empty turn file.");
            if (probe.Version != Version) throw new InvalidDataException($"Unsupported turn file version {probe.Version}.");
            t = JsonSerializer.Deserialize<TurnFile>(plain, QuartermasterJson.Options);
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException("Malformed turn file: " + ex.Message, ex);
        }
        if (t == null) throw new InvalidDataException("Empty turn file.");
        static void Need(bool ok, string what) { if (!ok) throw new InvalidDataException("Malformed turn file: " + what + " is missing."); }
        Need(t.ModuleSha256 != null, "module hash");
        Need(t.GameId != null, "game id");
        Need(t.FromPlayerId != null && t.FromName != null, "sender");
        Need(t.Start != null, "start");
        Need(t.StartHash != null && t.EndHash != null, "hash");
        Need(t.Commands != null, "commands");
        if (t.Commands!.Count > MaxCommands) throw new InvalidDataException("This turn file has too many steps.");
        Need(t.Commands.All(c => c != null), "command");
        NativeSave.ValidateState(t.Start!);
        return t;
    }

    static byte[] Gunzip(byte[] gz)
    {
        try
        {
            using var src = new GZipStream(new MemoryStream(gz, writable: false), CompressionMode.Decompress);
            using var dst = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                if (dst.Length + n > MaxPlain) throw new InvalidDataException("This turn file is too large.");
                dst.Write(buf, 0, n);
            }
            return dst.ToArray();
        }
        catch (Exception ex) when (ex is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException("Malformed turn file: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Replays the sender's turn on a fresh session whose genesis is <see cref="TurnFile.Start"/>: each command runs in its own action,
    /// <paramref name="step"/> is called after each with its index; then history is rebased (the receiver cannot undo the sender's turn)
    /// and the receiver's turn base is marked.
    /// </summary>
    public static GameSession Replay(TurnFile t, string moduleSha, Action<GameCommand, int>? step = null)
    {
        ArgumentNullException.ThrowIfNull(t);
        var g = new GameSession(t.Start.DeepClone(), moduleSha) { RulesSha256 = t.RulesSha256 };
        for (int i = 0; i < t.Commands.Count; i++)
        {
            var c = t.Commands[i];
            using (g.BeginAction()) g.Execute(c);
            step?.Invoke(c, i);
        }
        g.Rebase();
        g.MarkTurnBase();
        return g;
    }
}

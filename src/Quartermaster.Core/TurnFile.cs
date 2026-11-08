using System.IO.Compression;
using System.Text.Json;

namespace Quartermaster.Core;

/// <summary>
/// A play-by-email turn (.qturn): the whole game at the start of the sender's turn, the sender's commands (dice results included,
/// so replay never rerolls) and the hash of the sender's end state. Hidden information is only hidden on screen: the file holds everything.
/// </summary>
public sealed record TurnFile(
    int Version, string ModuleSha256, string? RulesSha256, string GameId, string FromPlayerId, string FromName, string? ToPlayerId,
    DateTime CreatedUtc, GameState Start, string StartHash, IReadOnlyList<GameCommand> Commands, string EndHash, string? AppVersion = null);

/// <summary>Creates, writes, reads and replays <see cref="TurnFile"/>s (JSON, gzip).</summary>
public static class TurnFiles
{
    public const int Version = 1;
    /// <summary>Largest uncompressed turn file accepted.</summary>
    public const long MaxPlain = 64L * 1024 * 1024;
    public const int MaxCommands = 100_000;

    sealed record VersionProbe(int Version);

    /// <summary>The local player's turn so far: the state at <see cref="GameSession.TurnBase"/> plus every command since. <paramref name="g"/> is not changed.</summary>
    public static TurnFile Create(GameSession g, string fromPlayerId, string fromName, string? toPlayerId, string? appVersion = null)
    {
        ArgumentNullException.ThrowIfNull(g);
        int from = g.TurnBase;
        var commands = g.Log.Skip(from).ToList();
        // A turn base above 0 means a turn was sent from here before: an empty turn then just resends the current game.
        if (commands.Count == 0 && from == 0) throw new InvalidOperationException("Nothing to send yet.");
        if (commands.Count > MaxCommands) throw new InvalidOperationException("This turn has too many steps to send.");
        var start = g.StateAt(from);
        return new TurnFile(Version, g.ModuleSha256, g.RulesSha256, g.State.GameId ?? "", fromPlayerId, fromName, toPlayerId,
            DateTime.UtcNow, start, StateHash.Of(start), commands, StateHash.Of(g.State), appVersion);
    }

    public static byte[] ToBytes(TurnFile t)
    {
        ArgumentNullException.ThrowIfNull(t);
        var plain = JsonSerializer.SerializeToUtf8Bytes(t, QuartermasterJson.Options);
        if (plain.LongLength > MaxPlain) throw new InvalidOperationException("This turn is too large to send.");
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(plain, 0, plain.Length);
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
        Need(t.Commands.All(IsWellFormed), "command data");
        NativeSave.ValidateState(t.Start!, "turn file");
        return t;
    }

    /// <summary>False for a null command or one missing an id or other field the reducer needs.</summary>
    static bool IsWellFormed(GameCommand? c) => c switch
    {
        null => false,
        AddPiece a => a.PieceId != null && a.TypeString != null && a.MapId != null,
        MovePieces m => m.PieceIds != null && m.PieceIds.All(id => id != null) && m.MapId != null,
        RemovePiece r => r.PieceId != null,
        SetTraitState s => s.PieceId != null && s.NewState != null,
        SetStackExpanded e => e.StackId != null,
        ClearMoved cm => cm.MapId != null,
        ToggleTrail tt => tt.PieceId != null,
        _ => true,
    };

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
    /// and the receiver's turn base is marked. A command that fails to apply throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static GameSession Replay(TurnFile t, string moduleSha, Action<GameCommand, int>? step = null)
    {
        ArgumentNullException.ThrowIfNull(t);
        var g = new GameSession(t.Start.DeepClone(), moduleSha) { RulesSha256 = t.RulesSha256 };
        for (int i = 0; i < t.Commands.Count; i++)
        {
            Step(g, t, i);
            step?.Invoke(t.Commands[i], i);
        }
        g.Rebase();
        g.MarkTurnBase();
        return g;
    }

    /// <summary>
    /// Runs command <paramref name="index"/> of the turn on <paramref name="g"/> in its own action, exactly as <see cref="Replay"/> does
    /// (interactive replay steps through the same code). A command that fails to apply throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static void Step(GameSession g, TurnFile t, int index)
    {
        ArgumentNullException.ThrowIfNull(g);
        ArgumentNullException.ThrowIfNull(t);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, t.Commands.Count);
        try
        {
            using (g.BeginAction()) g.Execute(t.Commands[index]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"Malformed turn file: step {index + 1} could not be replayed ({ex.Message}).", ex);
        }
    }
}

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Quartermaster.Vassal;

namespace Quartermaster.Core;

public sealed class PieceInstance
{
    public string Id { get; set; } = "";
    /// <summary>Raw, unexpanded type string.</summary>
    public string TypeString { get; set; } = "";
    /// <summary>Aligned to the unexpanded trait chain.</summary>
    public List<string> TraitStates { get; set; } = new();
    public string? StackId { get; set; }
    public string MapId { get; set; } = "";
    public PointD Pos { get; set; }
    public List<PointD> Trail { get; set; } = new();
    public bool TrailVisible { get; set; }
    public bool Moved { get; set; }
}

public sealed class StackInstance
{
    public string Id { get; set; } = "";
    public string MapId { get; set; } = "";
    public PointD Pos { get; set; }
    /// <summary>Bottom to top.</summary>
    public List<string> PieceIds { get; set; } = new();
    public bool Expanded { get; set; }
}

public sealed class GameState
{
    /// <summary>Insertion order is the deterministic draw order.</summary>
    public OrderedDictionary<string, PieceInstance> Pieces { get; set; } = new();
    public OrderedDictionary<string, StackInstance> Stacks { get; set; } = new();
    public List<string> MapOrder { get; set; } = new();
    public long NextStackSeq { get; set; }

    public IEnumerable<StackInstance> StacksOn(string mapId) =>
        Stacks.Values.Where(s => s.MapId == mapId);

    public GameState DeepClone() =>
        JsonSerializer.Deserialize<GameState>(JsonSerializer.Serialize(this, QuartermasterJson.Options), QuartermasterJson.Options)!;

    /// <summary>Deterministic reducer. Never throws on unknown ids.</summary>
    public void Apply(GameCommand c)
    {
        switch (c)
        {
            case AddPiece a: DoAdd(a); break;
            case MovePieces m: DoMove(m); break;
            case RemovePiece r:
                if (Pieces.TryGetValue(r.PieceId, out var rp))
                {
                    DetachFromStack(rp);
                    Pieces.Remove(r.PieceId);
                    PruneEmpty();
                }
                break;
            case SetTraitState t:
                if (Pieces.TryGetValue(t.PieceId, out var tp) && t.StateIndex >= 0)
                {
                    while (tp.TraitStates.Count <= t.StateIndex) tp.TraitStates.Add("");
                    tp.TraitStates[t.StateIndex] = t.NewState;
                }
                break;
            case SetStackExpanded e:
                if (Stacks.TryGetValue(e.StackId, out var es)) es.Expanded = e.Expanded;
                break;
            case ClearMoved cm:
                foreach (var p in Pieces.Values)
                    if (p.MapId == cm.MapId) p.Moved = false;
                break;
            case ToggleTrail tt:
                if (Pieces.TryGetValue(tt.PieceId, out var trp)) trp.TrailVisible = !trp.TrailVisible;
                break;
        }
    }

    void DoAdd(AddPiece a)
    {
        if (Pieces.ContainsKey(a.PieceId)) return;
        var p = new PieceInstance
        {
            Id = a.PieceId, TypeString = a.TypeString, MapId = a.MapId, Pos = a.Pos,
            TraitStates = string.IsNullOrEmpty(a.StateString) ? new() : a.StateString.Split('\t').ToList(),
        };
        Pieces[a.PieceId] = p;
        Place(new[] { p }, a.MapId, a.Pos, a.StackOnto);
    }

    void DoMove(MovePieces m)
    {
        var moved = new List<PieceInstance>();
        foreach (var id in m.PieceIds)
            if (Pieces.TryGetValue(id, out var p) && !moved.Contains(p)) moved.Add(p);
        if (moved.Count == 0) return;
        foreach (var p in moved) DetachFromStack(p);
        Place(moved, m.MapId, m.Pos, m.StackOnto);
        foreach (var p in moved) { p.Moved = true; p.Trail.Add(p.Pos); }
        PruneEmpty();
    }

    void Place(IReadOnlyList<PieceInstance> ps, string mapId, PointD pos, string? onto)
    {
        StackInstance st;
        if (onto != null && Stacks.TryGetValue(onto, out var existing) && existing.MapId == mapId)
            st = existing;
        else
        {
            st = new StackInstance { Id = "s:" + ps[0].Id + ":" + NextStackSeq++, MapId = mapId, Pos = pos };
            Stacks[st.Id] = st;
        }
        foreach (var p in ps)
        {
            p.StackId = st.Id; p.MapId = mapId; p.Pos = st.Pos;
            st.PieceIds.Add(p.Id);
        }
    }

    void DetachFromStack(PieceInstance p)
    {
        if (p.StackId != null && Stacks.TryGetValue(p.StackId, out var s)) s.PieceIds.Remove(p.Id);
        p.StackId = null;
    }

    void PruneEmpty()
    {
        var empty = Stacks.Where(kv => kv.Value.PieceIds.Count == 0).Select(kv => kv.Key).ToList();
        foreach (var k in empty) Stacks.Remove(k);
    }
}

/// <summary>JSON converter that preserves OrderedDictionary order.</summary>
sealed class OrderedDictionaryConverter<T> : JsonConverter<OrderedDictionary<string, T>>
{
    public override OrderedDictionary<string, T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected object.");
        var d = new OrderedDictionary<string, T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString()!;
            reader.Read();
            d[key] = JsonSerializer.Deserialize<T>(ref reader, options)!;
        }
        return d;
    }

    public override void Write(Utf8JsonWriter writer, OrderedDictionary<string, T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var kv in value)
        {
            writer.WritePropertyName(kv.Key);
            JsonSerializer.Serialize(writer, kv.Value, options);
        }
        writer.WriteEndObject();
    }
}

sealed class OrderedDictionaryConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) =>
        t.IsGenericType && t.GetGenericTypeDefinition() == typeof(OrderedDictionary<,>) && t.GetGenericArguments()[0] == typeof(string);

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(OrderedDictionaryConverter<>).MakeGenericType(t.GetGenericArguments()[1]))!;
}

public static class QuartermasterJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new OrderedDictionaryConverterFactory() },
    };
}

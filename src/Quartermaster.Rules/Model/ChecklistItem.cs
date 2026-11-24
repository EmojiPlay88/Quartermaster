using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quartermaster.Rules;

/// <summary>
/// One line of a phase's checklist. A text-only item (no id, rule reference or notes) is stored as a plain JSON string, so packs
/// written before items had fields keep their bytes.
/// </summary>
[JsonConverter(typeof(ChecklistItemConverter))]
public sealed record ChecklistItem
{
    /// <summary>Stable id that survives reordering; null = the item is ticked by position only.</summary>
    public string? Id { get; init; }
    /// <summary>The text shown to players; may contain {item:id} and {formula:id} links.</summary>
    public string Text { get; init; } = "";
    public string? RuleRefId { get; init; }
    public string? Notes { get; init; }

    public static implicit operator ChecklistItem(string text) => new() { Text = text ?? "" };

    [JsonIgnore]
    public bool IsTextOnly => Id is null && RuleRefId is null && Notes is null;
}

public sealed class ChecklistItemConverter : JsonConverter<ChecklistItem>
{
    public override ChecklistItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return new ChecklistItem { Text = reader.GetString() ?? "" };
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("A checklist item must be a string or an object.");
        string? id = null, text = null, rule = null, notes = null;
        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string? Str() => prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()
                : prop.Value.ValueKind == JsonValueKind.Null ? null
                : throw new JsonException($"Checklist item field \"{prop.Name}\" must be a string.");
            if (string.Equals(prop.Name, "id", StringComparison.OrdinalIgnoreCase)) id = Str();
            else if (string.Equals(prop.Name, "text", StringComparison.OrdinalIgnoreCase)) text = Str();
            else if (string.Equals(prop.Name, "ruleRefId", StringComparison.OrdinalIgnoreCase)) rule = Str();
            else if (string.Equals(prop.Name, "notes", StringComparison.OrdinalIgnoreCase)) notes = Str();
        }
        return new ChecklistItem { Id = id, Text = text ?? "", RuleRefId = rule, Notes = notes };
    }

    public override void Write(Utf8JsonWriter writer, ChecklistItem value, JsonSerializerOptions options)
    {
        if (value.IsTextOnly) { writer.WriteStringValue(value.Text ?? ""); return; }
        writer.WriteStartObject();
        if (value.Id is not null) writer.WriteString("id", value.Id);
        writer.WriteString("text", value.Text ?? "");
        if (value.RuleRefId is not null) writer.WriteString("ruleRefId", value.RuleRefId);
        if (value.Notes is not null) writer.WriteString("notes", value.Notes);
        writer.WriteEndObject();
    }
}

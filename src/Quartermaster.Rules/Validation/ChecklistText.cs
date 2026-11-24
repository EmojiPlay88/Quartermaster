using System.Text.RegularExpressions;

namespace Quartermaster.Rules;

/// <summary>Checklist item ids and the {item:id} / {formula:id} links a checklist text may hold.</summary>
public static class ChecklistText
{
    /// <summary>Longest quoted text of a linked item; longer text is cut and followed by an ellipsis.</summary>
    public const int CutAt = 40;

    private static readonly Regex IdRx = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex TokenRx = new(@"\{(item|formula):([A-Za-z0-9._-]+)\}", RegexOptions.CultureInvariant);

    public static bool IsUsableId(string id) => id is not null && IdRx.IsMatch(id);

    /// <summary>The links in a text, in order: kind ("item" or "formula"), id, and the token as written.</summary>
    public static IReadOnlyList<(string Kind, string Id, string Token)> Tokens(string text) =>
        TokenRx.Matches(text ?? "").Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Value)).ToList();

    /// <summary>The text with each link replaced: an item by its quoted (cut) text, a formula by its name. A link to nothing stays as written.</summary>
    public static string Render(RulesPack pack, string text) =>
        TokenRx.Replace(text ?? "", m =>
        {
            var id = m.Groups[2].Value;
            if (m.Groups[1].Value == "item")
            {
                if (ItemById(pack, id) is not { } item) return m.Value;
                var t = item.Text ?? "";
                return "\"" + (t.Length > CutAt ? t[..CutAt] + "…" : t) + "\"";
            }
            return pack.ById<FormulaDef>(id) is { } f ? f.Name : m.Value;
        });

    /// <summary>The first checklist item, in phase order, that has the id.</summary>
    public static ChecklistItem? ItemById(RulesPack pack, string id) => Find(pack, id)?.Item;

    /// <summary>The first checklist item with the id, with the phase that holds it.</summary>
    public static (PhaseDef Phase, ChecklistItem Item)? Find(RulesPack pack, string id)
    {
        foreach (var ph in pack.Phases)
            foreach (var it in ph.Checklist ?? new())
                if (it?.Id is { } i && string.Equals(i, id, StringComparison.Ordinal)) return (ph, it);
        return null;
    }
}

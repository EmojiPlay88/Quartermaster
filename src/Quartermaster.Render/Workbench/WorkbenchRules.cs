using Quartermaster.Render.Ocr;
using Quartermaster.Rules;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Workbench;

public enum WorkbenchTab { Tables, Formulas, Properties, Phases, Alerts, Visibility }

/// <summary>One line of the workbench's problems list. <see cref="Issue"/> is null for the "No problems found." row.</summary>
public sealed record IssueRow(string Severity, string Item, string Problem, RulesIssue? Issue);

/// <summary>The Rules Workbench's UI-free rules: window title, problems list, navigation target, rule-ref creation, naming and confirm texts.</summary>
public static class WorkbenchRules
{
    public const string NoPackText = "No rules pack is loaded. A rules pack holds the tables and formulas you write yourself.";
    public const string ReadOnlyText = "Read-only: the host's rules pack for this online game. Only the host can change it.";
    public const string NoProblemsText = "No problems found.";
    public const string RefNotAllowedText = "This document can't be used as a rule ref: rule refs name a module document or an opened scan's file name, never a path on this computer.";
    public const string OpenDocumentFirstText = "Open a document in the left pane first, then go to the page to link.";

    public static string Title(RulesPack? pack, bool guest) =>
        pack != null ? $"Rules Workbench — {pack.Name}" + (guest ? " (read-only)" : "") : "Rules Workbench";

    /// <summary>The module's palette entries as a Place output names them: the path (folders and entry name joined with "/").</summary>
    public static IReadOnlyList<string> SlotPaths(ModuleModel model) =>
        model.Slots.Select(s => string.Join('/', s.NavPath.Append(s.EntryName))).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Every key a Place output may name a palette entry by: its path or its gpid.</summary>
    public static IReadOnlyList<string> SlotKeys(ModuleModel model) =>
        SlotPaths(model).Concat(model.Slots.Select(s => s.Gpid).Where(g => !string.IsNullOrEmpty(g))).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<IssueRow> Issues(RulesPack pack, IReadOnlyList<string> mapIds) => Issues(pack, mapIds, null);

    /// <param name="slotKeys">The palette entries a Place output may name (<see cref="SlotKeys"/>); null = not checked.</param>
    public static IReadOnlyList<IssueRow> Issues(RulesPack pack, IReadOnlyList<string> mapIds, IReadOnlyCollection<string>? slotKeys) =>
        Rows(RulesValidator.Validate(pack, mapIds, slotKeys));

    /// <summary>B6: as <see cref="Issues(RulesPack, IReadOnlyList{string}, IReadOnlyCollection{string})"/>, checked against the module
    /// (the four-argument <see cref="RulesValidator"/>.Validate): Remove's To map is found by id or name, and hex name constants must be
    /// hexes of a board.</summary>
    public static IReadOnlyList<IssueRow> Issues(RulesPack pack, IReadOnlyList<string> mapIds, IReadOnlyCollection<string>? slotKeys,
        Quartermaster.Vassal.ModuleModel? model) =>
        Rows(RulesValidator.Validate(pack, mapIds, slotKeys, model));

    private static IReadOnlyList<IssueRow> Rows(IReadOnlyList<RulesIssue> list)
    {
        if (list.Count == 0) return [new IssueRow("", "", NoProblemsText, null)];
        return list.Select(i => new IssueRow(i.Severity.ToString(), i.EntityName, i.Message, i)).ToList();
    }

    public static (WorkbenchTab Tab, string Id)? IssueTarget(RulesPack pack, string entityId)
    {
        if (pack.ById<TableDef>(entityId) != null) return (WorkbenchTab.Tables, entityId);
        if (pack.ById<FormulaDef>(entityId) != null) return (WorkbenchTab.Formulas, entityId);
        if (pack.ById<PhaseDef>(entityId) != null) return (WorkbenchTab.Phases, entityId);
        if (pack.ById<AlertDef>(entityId) != null) return (WorkbenchTab.Alerts, entityId);
        if (pack.ById<PropertyDef>(entityId) != null) return (WorkbenchTab.Properties, entityId);
        if (pack.ById<HolderDef>(entityId) != null) return (WorkbenchTab.Properties, entityId);
        if (pack.MapOwners.Any(o => o.MapId == entityId)) return (WorkbenchTab.Visibility, entityId);
        return null;
    }

    public static (RulesPack Pack, RuleRef Ref) MakeOrReuseRef(RulesPack pack, string label, string entry, int page)
    {
        var existing = pack.RuleRefs.FirstOrDefault(r => r.DocumentEntry == entry && r.Page == page);
        if (existing != null) return (pack, existing);
        var made = new RuleRef { Id = Guid.NewGuid().ToString("N"), Label = label, DocumentEntry = entry, Page = page };
        return (pack with { RuleRefs = pack.RuleRefs.Append(made).ToList() }, made);
    }

    public static string MissingRefText(string entry) =>
        $"The reference points to '{entry}', which is not in this module" + (ScanSessionModel.IsScanFile(entry) ? " (open it with Open scan… to see it)." : ".");

    public static string UniqueName(IEnumerable<string> names, string stem)
    {
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = set.Count + 1; ; n++) if (!set.Contains($"{stem} {n}")) return $"{stem} {n}";
    }

    public static string DeleteQuestion(string kind, string name, IReadOnlyList<FormulaDef> users)
    {
        var text = $"Delete the {kind} \"{name}\"?";
        if (users.Count > 0)
            text += "\n\nThese formulas use it and will report errors until you fix them:\n  " + string.Join("\n  ", users.Take(15).Select(f => f.Name)) + (users.Count > 15 ? "\n  …" : "");
        return text;
    }
}

using Quartermaster.Render.Search;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Workbench;

/// <summary>Rulebook search of the workbench (port stage 4a task 11 fills this file; until then a stub).</summary>
public sealed partial class WorkbenchWindow
{
    /// <summary>Called once from the constructor, after the tabs and the viewer exist.</summary>
    void WireSearch() { }

    /// <summary>Rebuilds the rulebook index (a PDF was opened as a scan).</summary>
    void RestartIndex() { }

    /// <summary>The window closed: running index builds are cancelled.</summary>
    void OnSearchClosed() { }

    /// <summary>How the editors show a rule ref; null = the plain "label, page n" text.</summary>
    Func<RuleRef?, RefDisplay>? Describe => null;
}

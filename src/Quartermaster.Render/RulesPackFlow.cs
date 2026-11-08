namespace Quartermaster.Render;

/// <summary>The dialogs the rules-pack flow needs; each front end implements them (WinForms message boxes, Avalonia dialogs).</summary>
public interface IRulesPackUi
{
    /// <summary>"Save changes to the rules pack '<paramref name="packName"/>'?" Yes = true, No = false, Cancel = null.</summary>
    bool? AskSaveChanges(string packName);
    /// <summary>Open Rules Pack file picker (*.qrules); null = cancelled.</summary>
    string? PickOpen();
    /// <summary>Save Rules Pack file picker; null = cancelled.</summary>
    string? PickSave(string suggestedName);
    /// <summary>Name prompt; null = cancelled.</summary>
    string? AskName(string title, string prompt, string def);
    /// <summary>Shows an error message.</summary>
    void Warn(string text);
}

/// <summary>New / Open / Save / confirm-saved for the rules pack of one session. UI-free: all prompts go through <see cref="IRulesPackUi"/>.</summary>
public sealed class RulesPackFlow
{
    private readonly ModuleSession _s;
    private readonly IRulesPackUi _ui;
    private readonly string? _rulesMemoryRoot;

    public RulesPackFlow(ModuleSession s, IRulesPackUi ui, string? rulesMemoryRoot)
    {
        _s = s;
        _ui = ui;
        _rulesMemoryRoot = rulesMemoryRoot;
    }

    /// <summary>Offers to save an edited rules pack. Returns false when the user cancels (or cancels the save).</summary>
    public bool ConfirmSaved()
    {
        if (_s is not { RulesDirty: true }) return true;
        var r = _ui.AskSaveChanges(_s.Rules?.Name ?? "");
        if (r == null) return false;
        if (r == false) return true;
        try { return Save(false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ui.Warn(ex.Message);
            return false;
        }
    }

    public void New()
    {
        if (!ConfirmSaved()) return;
        var name = _ui.AskName("New Rules Pack", "Name of the new rules pack:", "Rules");
        if (!string.IsNullOrWhiteSpace(name)) _s.NewRules(name.Trim());
    }

    public void Open()
    {
        if (!ConfirmSaved()) return;
        var path = _ui.PickOpen();
        if (path != null) _s.OpenRules(path);
    }

    /// <summary>Saves the pack; returns false when the user cancelled the file dialog.</summary>
    public bool Save(bool saveAs)
    {
        if (_s.Rules == null) return true;
        string? path = null;
        if (saveAs || _s.RulesPath == null)
        {
            path = _ui.PickSave(_s.Rules.Name);
            if (path == null) return false;
        }
        _s.SaveRules(path);
        return true;
    }

    /// <summary>Remembers the current pack path as this module's last rules pack (no-op without a root or a saved/opened path).</summary>
    public void RememberCurrent()
    {
        if (_rulesMemoryRoot != null && _s.RulesPath != null && !_s.IsGuest)
            RulesMemory.Remember(_rulesMemoryRoot, _s.Archive.Sha256, _s.RulesPath);
    }
}

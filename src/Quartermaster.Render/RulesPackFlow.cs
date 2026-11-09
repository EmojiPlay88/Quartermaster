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

/// <summary>Async form of <see cref="IRulesPackUi"/> for front ends whose dialogs are asynchronous (Avalonia).</summary>
public interface IRulesPackUiAsync
{
    Task<bool?> AskSaveChangesAsync(string packName);
    Task<string?> PickOpenAsync();
    Task<string?> PickSaveAsync(string suggestedName);
    Task<string?> AskNameAsync(string title, string prompt, string def);
    Task WarnAsync(string text);
}

/// <summary>New / Open / Save / confirm-saved for the rules pack of one session. UI-free: all prompts go through the UI interface. One async core; the sync API runs it over completed tasks.</summary>
public sealed class RulesPackFlow
{
    private sealed class SyncAdapter : IRulesPackUiAsync
    {
        private readonly IRulesPackUi _ui;
        public SyncAdapter(IRulesPackUi ui) => _ui = ui;
        public Task<bool?> AskSaveChangesAsync(string packName) => Task.FromResult(_ui.AskSaveChanges(packName));
        public Task<string?> PickOpenAsync() => Task.FromResult(_ui.PickOpen());
        public Task<string?> PickSaveAsync(string suggestedName) => Task.FromResult(_ui.PickSave(suggestedName));
        public Task<string?> AskNameAsync(string title, string prompt, string def) => Task.FromResult(_ui.AskName(title, prompt, def));
        public Task WarnAsync(string text) { _ui.Warn(text); return Task.CompletedTask; }
    }

    private readonly ModuleSession _s;
    private readonly IRulesPackUiAsync _ui;

    public RulesPackFlow(ModuleSession s, IRulesPackUi ui) : this(s, new SyncAdapter(ui)) { }

    public RulesPackFlow(ModuleSession s, IRulesPackUiAsync ui)
    {
        _s = s;
        _ui = ui;
    }

    // Sync API: only for UIs whose tasks are already complete (SyncAdapter); never blocks on an incomplete task.
    public bool ConfirmSaved() => Done(ConfirmSavedAsync());
    public void New() => Done(NewAsync());
    public void Open() => Done(OpenAsync());
    public bool Save(bool saveAs) => Done(SaveAsync(saveAs));

    private static T Done<T>(Task<T> t) => t.IsCompleted ? t.GetAwaiter().GetResult() : throw new InvalidOperationException("The sync rules flow needs a synchronous UI.");
    private static void Done(Task t)
    {
        if (!t.IsCompleted) throw new InvalidOperationException("The sync rules flow needs a synchronous UI.");
        t.GetAwaiter().GetResult();
    }

    /// <summary>Offers to save an edited rules pack. Returns false when the user cancels (or cancels the save).</summary>
    public async Task<bool> ConfirmSavedAsync()
    {
        if (_s is not { RulesDirty: true }) return true;
        var r = await _ui.AskSaveChangesAsync(_s.Rules?.Name ?? "");
        if (r == null) return false;
        if (r == false) return true;
        try { return await SaveAsync(false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _ui.WarnAsync(ex.Message);
            return false;
        }
    }

    public async Task NewAsync()
    {
        if (!await ConfirmSavedAsync()) return;
        var name = await _ui.AskNameAsync("New Rules Pack", "Name of the new rules pack:", "Rules");
        if (!string.IsNullOrWhiteSpace(name)) _s.NewRules(name.Trim());
    }

    public async Task OpenAsync()
    {
        if (!await ConfirmSavedAsync()) return;
        var path = await _ui.PickOpenAsync();
        if (path != null) _s.OpenRules(path);
    }

    /// <summary>Saves the pack; returns false when the user cancelled the file dialog.</summary>
    public async Task<bool> SaveAsync(bool saveAs)
    {
        if (_s.Rules == null) return true;
        string? path = null;
        if (saveAs || _s.RulesPath == null)
        {
            path = await _ui.PickSaveAsync(_s.Rules.Name);
            if (path == null) return false;
        }
        _s.SaveRules(path);
        return true;
    }
}

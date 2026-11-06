using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.App;

/// <summary>
/// Rules Workbench: the module's rulebook on the left as a reference the user reads, the user's own editors on the right.
/// Nothing is ever read out of the documents; every table and formula is typed or pasted by the user.
/// </summary>
sealed class WorkbenchWindow : Form, WindowManager.IReparentAware
{
    readonly ModuleSession _s;
    readonly PdfViewerControl _viewer;
    readonly SplitContainer _split = new() { Dock = DockStyle.Fill };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly TabPage _tablesPage = new("Tables"), _formulasPage = new("Formulas"), _propsPage = new("Properties & Holders");
    readonly TablesTab _tables;
    readonly FormulasTab _formulas;
    readonly PropertiesTab _props;
    readonly ListView _issues = new()
    {
        Dock = DockStyle.Bottom, Height = 110, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
    };
    readonly Panel _main = new() { Dock = DockStyle.Fill };
    readonly Panel _prompt = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _validateTimer = new() { Interval = 300 };
    bool _splitSet;

    public WorkbenchWindow(ModuleSession s, Action newPack, Action openPack)
    {
        _s = s;
        ShowIcon = false;
        Size = new Size(1000, 640);

        _viewer = new PdfViewerControl(s, toolbar: true, allowWeb: !Program.Smoke) { Dock = DockStyle.Fill };
        _tables = new TablesTab(s, LinkCurrentPage, ShowRule) { Dock = DockStyle.Fill };
        _tablesPage.Controls.Add(_tables);
        _formulas = new FormulasTab(s, LinkCurrentPage, ShowRule) { Dock = DockStyle.Fill };
        _formulasPage.Controls.Add(_formulas);
        _props = new PropertiesTab(s) { Dock = DockStyle.Fill };
        _propsPage.Controls.Add(_props);
        _tabs.TabPages.AddRange(new[] { _tablesPage, _formulasPage, _propsPage });
        _split.Panel1.Controls.Add(_viewer);
        _split.Panel2.Controls.Add(_tabs);

        _issues.Columns.Add("Severity", 70);
        _issues.Columns.Add("Item", 140);
        _issues.Columns.Add("Problem", 600);
        _issues.DoubleClick += (_, _) => GoToIssue();
        _main.Controls.Add(_split);
        _main.Controls.Add(new Splitter { Dock = DockStyle.Bottom, Height = 4 });
        _main.Controls.Add(_issues);

        var msg = new Label { Text = "No rules pack is loaded. A rules pack holds the tables and formulas you write yourself.", AutoSize = true, Left = 20, Top = 20 };
        var bNew = new Button { Text = "New rules pack…", AutoSize = true, Left = 20, Top = 56 };
        var bOpen = new Button { Text = "Open…", AutoSize = true, Left = 140, Top = 56 };
        bNew.Click += (_, _) => newPack();
        bOpen.Click += (_, _) => openPack();
        _prompt.Controls.AddRange(new Control[] { msg, bNew, bOpen });

        Controls.Add(_main);
        Controls.Add(_prompt);

        _validateTimer.Tick += (_, _) => { _validateTimer.Stop(); Validate2(); };
        _s.RulesChanged += OnRulesChanged;
        Shown += (_, _) => SetSplit();
        FormClosed += (_, _) => { _s.RulesChanged -= OnRulesChanged; _validateTimer.Dispose(); };

        if (Program.Smoke && s.Model.Documents.FirstOrDefault(d => d.Kind == Quartermaster.Vassal.DocumentKind.Pdf) is { } doc) _viewer.Open(doc);
        Apply();
    }

    public PdfViewerControl Viewer => _viewer;

    void SetSplit()
    {
        if (_splitSet || _split.Width < 300) return;
        _splitSet = true;
        _split.SplitterDistance = _split.Width * 40 / 100;
    }

    void WindowManager.IReparentAware.BeforeReparent() => _viewer.BeforeReparent();

    void WindowManager.IReparentAware.AfterReparent() => _viewer.AfterReparent();

    // ---------- pack state ----------

    void OnRulesChanged() => OnUi(() =>
    {
        Apply(validate: false);
        _validateTimer.Stop();
        _validateTimer.Start(); // debounced
    });

    void OnUi(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) BeginInvoke(new Action(() => { if (!IsDisposed) a(); }));
        else a();
    }

    void Apply(bool validate = true)
    {
        var has = _s.Rules != null;
        _main.Visible = has;
        _prompt.Visible = !has;
        Text = has ? $"Rules Workbench — {_s.Rules!.Name}" : "Rules Workbench";
        if (has && validate) Validate2();
    }

    void Validate2()
    {
        if (IsDisposed || _s.Rules is not { } pack) return;
        IReadOnlyList<RulesIssue> list;
        try { list = RulesValidator.Validate(pack); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.AppLog("workbench validation failed: " + ex);
            return;
        }
        _issues.BeginUpdate();
        try
        {
            _issues.Items.Clear();
            foreach (var i in list)
            {
                var it = new ListViewItem(new[] { i.Severity.ToString(), i.EntityName, i.Message }) { Tag = i };
                _issues.Items.Add(it);
            }
            if (list.Count == 0) _issues.Items.Add(new ListViewItem(new[] { "", "", "No problems found." }));
        }
        finally { _issues.EndUpdate(); }
    }

    public int IssueCountForSmoke => _issues.Items.Cast<ListViewItem>().Count(i => i.Tag is RulesIssue);

    void GoToIssue()
    {
        if (_issues.SelectedItems.Count == 0 || _issues.SelectedItems[0].Tag is not RulesIssue i || _s.Rules is not { } p) return;
        if (p.ById<TableDef>(i.EntityId) != null) { _tabs.SelectedTab = _tablesPage; _tables.SelectTable(i.EntityId); }
        else if (p.ById<FormulaDef>(i.EntityId) != null) { _tabs.SelectedTab = _formulasPage; _formulas.SelectFormula(i.EntityId); }
        else if (p.ById<PropertyDef>(i.EntityId) != null) { _tabs.SelectedTab = _propsPage; _props.SelectProperty(i.EntityId); }
        else if (p.ById<HolderDef>(i.EntityId) != null) { _tabs.SelectedTab = _propsPage; _props.SelectHolder(i.EntityId); }
    }

    // ---------- rule references ----------

    /// <summary>Creates or reuses a RuleRef for the document and page shown in the viewer. Null (with a message) when nothing is open.</summary>
    public RuleRef? LinkCurrentPage(string label)
    {
        if (_viewer.Document is not { } doc)
        {
            Warn("Open a document in the left pane first, then go to the page to link.");
            return null;
        }
        var page = _viewer.Page;
        RuleRef? made = null;
        _s.UpdateRules(p =>
        {
            var existing = p.RuleRefs.FirstOrDefault(r => r.DocumentEntry == doc.EntryName && r.Page == page);
            if (existing != null) { made = existing; return p; }
            made = new RuleRef { Id = Guid.NewGuid().ToString("N"), Label = label, DocumentEntry = doc.EntryName, Page = page };
            return p with { RuleRefs = p.RuleRefs.Append(made).ToList() };
        });
        return made;
    }

    void ShowRule(RuleRef r)
    {
        var doc = _s.Model.Documents.FirstOrDefault(d => d.EntryName == r.DocumentEntry);
        if (doc == null) { Warn($"The reference points to '{r.DocumentEntry}', which is not in this module."); return; }
        _viewer.Open(doc, r.Page);
    }

    void Warn(string text)
    {
        if (Program.Smoke) { Program.AppLog("workbench: " + text); return; }
        MessageBox.Show(this, text, "Rules Workbench", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---------- smoke ----------

    /// <summary>Smoke: shows the named tab (tables, formulas, properties); unknown or empty keeps the Tables tab.</summary>
    public void SelectTabForSmoke(string? name)
    {
        _tabs.SelectedTab = name?.ToLowerInvariant() switch { "formulas" => _formulasPage, "properties" => _propsPage, _ => _tablesPage };
        if (_tabs.SelectedTab == _formulasPage && _formulas.SelectedFormulaId == null) Program.AppLog("smoke: no formula to show");
    }

    public string? SelectedTableForSmoke => _tables.SelectedTableId;

    public void SaveScreenshot(string path)
    {
        SetSplit();
        Update();
        using var bmp = new Bitmap(Math.Max(Width, 1), Math.Max(Height, 1));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Program.AppLog("smoke: workbench screenshot saved to " + path);
    }
}

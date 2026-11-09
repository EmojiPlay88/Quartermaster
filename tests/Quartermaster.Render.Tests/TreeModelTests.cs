using Quartermaster.Render;
using Quartermaster.Rules;
using Quartermaster.TestFixtures;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

public class TreeModelTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-tree-" + Guid.NewGuid().ToString("N"));
    readonly List<IDisposable> _d = new();
    public void Dispose() { foreach (var d in _d) d.Dispose(); try { Directory.Delete(_tmp, true); } catch (IOException) { } }

    ModuleModel Model()
    {
        var fx = PhaseFixture.Module(); _d.Add(fx);
        var s = ModuleSession.Open(fx.Build(), Path.Combine(_tmp, "c"), Path.Combine(_tmp, "r")); _d.Add(s);
        return s.Model;
    }

    static IEnumerable<TreeItem> Walk(IEnumerable<TreeItem> xs) => xs.SelectMany(x => new[] { x }.Concat(Walk(x.Children)));

    [Fact]
    public void Module_tree_matches_module_window_tree()
    {
        var m = Model();
        var root = Assert.Single(ModuleTreeModel.Build(m, null));
        void Check(TreeItem i, WindowTreeNode n)
        {
            Assert.Equal(n.Title, i.Text); Assert.Equal(n.Kind.ToString(), i.Tooltip); Assert.Same(n, i.Tag);
            Assert.Equal(n.Children.Count, i.Children.Count);
            for (var k = 0; k < n.Children.Count; k++) Check(i.Children[k], n.Children[k]);
        }
        Check(root, m.Tree);
    }

    [Fact]
    public void Hidden_map_left_out_for_viewer()
    {
        var m = Model(); var id = m.Maps[0].Id;
        Assert.Contains(Walk(ModuleTreeModel.Build(m, null)), i => i.Tag is WindowTreeNode { Ref: MapDef d } && d.Id == id);
        Assert.DoesNotContain(Walk(ModuleTreeModel.Build(m, x => x != id)), i => i.Tag is WindowTreeNode { Ref: MapDef });
    }

    [Fact]
    public void Signature_changes_when_visibility_changes()
    {
        var m = Model(); var id = m.Maps[0].Id;
        Assert.Equal("", ModuleTreeModel.VisibilitySignature(m, null));
        Assert.Equal("", ModuleTreeModel.VisibilitySignature(m, _ => true));
        Assert.Equal(id, ModuleTreeModel.VisibilitySignature(m, x => x != id));
    }

    [Fact]
    public void Tables_tree_roots_sorted_with_tooltips()
    {
        var pack = PhaseFixture.Pack("x") with
        {
            Tables = new()
            {
                new TableDef { Id = "b", Name = "beta", Kind = TableKind.TwoD, Cells = new() { new() { "1", "2" }, new() { "3" } }, Notes = "note" },
                new TableDef { Id = "a", Name = "Alpha", Kind = TableKind.Dice, Cells = new() },
            },
        };
        var roots = TablesTreeModel.Build(pack);
        Assert.Equal(["Tables", "Formulas"], roots.Select(r => r.Text));
        Assert.Equal(["Alpha", "beta"], roots[0].Children.Select(c => c.Text));
        Assert.Equal("Dice, 0×0", roots[0].Children[0].Tooltip);
        Assert.Equal("note\nTwoD, 2×2", roots[0].Children[1].Tooltip);
        Assert.Equal(new TableRef("b"), roots[0].Children[1].Tag);
        Assert.Equal(["Daily water", "Fuel for movement", "Weather roll"], roots[1].Children.Select(c => c.Text));
        Assert.Equal(new FormulaRef("f-water"), roots[1].Children[0].Tag);
        Assert.Null(roots[1].Children[0].Tooltip);
    }

    [Fact]
    public void Tables_tree_without_rules()
    {
        var r = Assert.Single(TablesTreeModel.Build(null));
        Assert.Equal("(no rules pack)", r.Text); Assert.Empty(r.Children);
    }
}

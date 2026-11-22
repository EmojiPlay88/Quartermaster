using System.Diagnostics;
using Quartermaster.Rules.Expr;
using Quartermaster.Rules.Tables;

namespace Quartermaster.Rules.Tests;

public class TableLookupIndexTests
{
    private static TableDef OneD(string[] rows) => new()
    {
        Id = "t1", Name = "T", Kind = TableKind.OneD,
        RowKeys = rows.ToList(), ColumnKeys = new() { "Value" },
        Cells = rows.Select((_, i) => new List<string> { i.ToString() }).ToList(),
    };

    [Fact]
    public void Indexed_lookup_matches_linear_scan_on_mixed_keys()
    {
        var keys = new[] { "1-3", "Forest", "2", "4+", "forest", "<=0", "Hill", "5" };
        var t = OneD(keys);
        var values = new List<Value>();
        foreach (var n in new[] { 0.0, 1, 2, 3, 4, 5, 9, -1, 2.5 }) values.Add(Value.Of(n));
        foreach (var s in new[] { "2", "FOREST", "  Hill ", "5", "Swamp", "4" }) values.Add(Value.Of(s));
        values.Add(Value.Of(true));

        foreach (var v in values)
        {
            int expected = t.RowKeys.FindIndex(k => TableLookup.Matches(TableLookup.ParseKey(k), v));
            if (expected < 0)
            {
                var ex = Assert.Throws<EvalError>(() => TableLookup.Locate(t, v, null));
                Assert.StartsWith("No row for", ex.Message);
            }
            else Assert.Equal(expected, TableLookup.Locate(t, v, null).Row);
        }
    }

    [Fact]
    public void Text_key_lookup_is_case_insensitive_and_first_wins()
    {
        var t = OneD(new[] { "Plain", "Forest", "Hill", "Marsh", "forest" });
        Assert.Equal(1, TableLookup.Locate(t, Value.Of("FOREST"), null).Row);
    }

    [Fact]
    public void Edited_table_is_seen_at_once()
    {
        var t = OneD(new[] { "Plain", "Forest", "Hill" });
        Assert.Equal(2, TableLookup.Locate(t, Value.Of("Hill"), null).Row);

        var edited = TableEdit.SetRowKey(t, 2, "Ridge");
        Assert.Equal(2, TableLookup.Locate(edited, Value.Of("Ridge"), null).Row);
        Assert.Throws<EvalError>(() => TableLookup.Locate(edited, Value.Of("Hill"), null));
        Assert.Equal(2, TableLookup.Locate(t, Value.Of("Hill"), null).Row);
    }

    [Fact]
    public void Two_dimensional_columns_use_the_index()
    {
        var t = new TableDef
        {
            Id = "t2", Name = "Grid", Kind = TableKind.TwoD,
            RowKeys = new() { "1", "2" }, ColumnKeys = new() { "Clear", "Rough" },
            Cells = new() { new() { "10", "11" }, new() { "20", "21" } },
        };
        Assert.Equal(21, TableLookup.Lookup(t, Value.Of(2), Value.Of("rough")).Number);
        Assert.Equal(10, TableLookup.Lookup(t, Value.Of(1), Value.Of("Clear")).Number);
    }

    [Fact]
    public void Big_text_table_lookup_is_fast()
    {
        var keys = Enumerable.Range(0, 1000).Select(i => $"Unit-{i:0000}").ToArray();
        var t = OneD(keys);
        var v = Value.Of("unit-0999");
        Assert.Equal(999, TableLookup.Lookup(t, v, null).Number);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 10_000; i++)
            Assert.Equal(999, TableLookup.Lookup(t, v, null).Number);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"10,000 lookups took {sw.ElapsedMilliseconds} ms");
    }
}

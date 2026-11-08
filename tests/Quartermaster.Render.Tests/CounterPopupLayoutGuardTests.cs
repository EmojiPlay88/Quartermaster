using Quartermaster.Core;
using Quartermaster.Render;
using Quartermaster.Vassal;
using SkiaSharp;

namespace Quartermaster.Render.Tests;

/// <summary>
/// Guard for the shared hover popup layout (WinForms CounterDetailPopup and the Avalonia popup draw from it): pins size, text lines and the
/// ledger lines' pixel rectangles at 125% and 150% for fixed Riverland demo stacks, including one past <see cref="CounterPopupLayout.MaxRows"/>
/// ("+ n more"). Text widths depend on Tahoma; where it is missing (off Windows) only the font-independent values are checked.
/// </summary>
[Collection(AppDataRootEnv.Name)]
public sealed class CounterPopupLayoutGuardTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-popup-guard-" + Guid.NewGuid().ToString("N"));
    private ModuleSession? _s;

    public void Dispose()
    {
        _s?.Dispose();
        try { Directory.Delete(_tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Demo(string file, [System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "docs", "wiki", "demo", file));

    private static bool HasTahoma()
    {
        using var t = SKTypeface.FromFamilyName("Tahoma");
        return t != null && string.Equals(t.FamilyName, "Tahoma", StringComparison.OrdinalIgnoreCase);
    }

    private (GameState State, string MapId, PieceRenderer Pieces) Riverland()
    {
        _s = ModuleSession.Open(Demo("Riverland-Demo.vmod"), Path.Combine(_tmp, "cache"), null);
        _s.StartNewGame(Difficulty.FullyAuto);
        _s.OpenRules(Demo("Riverland-Demo.qrules"));
        return (_s.View!, _s.Model.Maps[0].Id, new PieceRenderer(_s.Images));
    }

    /// <summary>Checks a pixel rectangle: always its vertical extent, the horizontal one only with Tahoma (text widths).</summary>
    private static void Rect(SKRectI actual, int left, int top, int width, int height)
    {
        Assert.Equal((top, height), (actual.Top, actual.Height));
        if (HasTahoma()) Assert.Equal((left, width), (actual.Left, actual.Width));
    }

    [Fact]
    public void Single_piece_layout_is_pinned()
    {
        var (st, mapId, pieces) = Riverland();
        var stack = new StackInstance { Id = "guard-1", MapId = mapId, PieceIds = { "n:1" } };
        var l = CounterPopupLayout.Build(_s!, st, stack, pieces);

        Assert.Equal(117, l.Height);
        if (HasTahoma()) Assert.Equal(176, l.Width);
        Assert.Equal(0, l.More);
        Assert.Equal(new[] { "NL 1 Rifles", "Side: Northern League", "Kind: Unit", "Type: RIF", "Ammo: 3", "Fuel: 3", "Readiness: 3" }, l.Lines);
        Assert.Equal(3, l.HotLines.Count);
        Rect(l.HotLines[0].PixelRect(1.25f), 75, 82, 56, 18);
        Rect(l.HotLines[1].PixelRect(1.25f), 75, 101, 42, 18);
        Rect(l.HotLines[2].PixelRect(1.25f), 75, 120, 78, 18);
        Rect(l.HotLines[0].PixelRect(1.5f), 90, 99, 67, 22);
        Rect(l.HotLines[1].PixelRect(1.5f), 90, 121, 51, 22);
        Rect(l.HotLines[2].PixelRect(1.5f), 90, 144, 94, 22);
    }

    [Fact]
    public void Stack_past_max_rows_layout_is_pinned()
    {
        // 14 pieces: the top 12 are listed (top first), then "+ 2 more"; the depot has no ledger lines.
        var (st, mapId, pieces) = Riverland();
        var stack = new StackInstance { Id = "guard-14", MapId = mapId };
        stack.PieceIds.AddRange(Enumerable.Range(1, 14).Select(i => "n:" + i));
        var l = CounterPopupLayout.Build(_s!, st, stack, pieces);

        Assert.Equal(CounterPopupLayout.MaxRows, l.Details.Count);
        Assert.Equal(2, l.More);
        Assert.Equal(1314, l.Height);
        if (HasTahoma()) Assert.Equal(184, l.Width);
        Assert.Equal(11 * 7 + 4 + 1, l.Lines.Count);
        Assert.Equal(new[] { "SC 6 Guns", "Side: Southern Compact", "Kind: Unit", "Type: GUN", "Ammo: 4", "Fuel: 2", "Readiness: 2" }, l.Lines.Take(7));
        Assert.Contains("NL Depot", l.Lines);
        Assert.Equal(new[] { "NL 3 Rifles", "Side: Northern League", "Kind: Unit", "Type: RIF", "Ammo: 3", "Fuel: 3", "Readiness: 3", "+ 2 more" },
            l.Lines.TakeLast(8));
        Assert.Equal(33, l.HotLines.Count);
        Rect(l.HotLines[0].PixelRect(1.25f), 75, 82, 56, 18);
        Rect(l.HotLines[0].PixelRect(1.5f), 90, 99, 67, 22);
        Rect(l.HotLines[3].PixelRect(1.25f), 75, 221, 56, 18);
        Rect(l.HotLines[3].PixelRect(1.5f), 90, 265, 67, 22);
        Rect(l.HotLines[^1].PixelRect(1.25f), 75, 1590, 78, 18);
        Rect(l.HotLines[^1].PixelRect(1.5f), 90, 1908, 94, 22);
    }
}

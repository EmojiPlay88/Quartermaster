using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Quartermaster.Core;
using Quartermaster.Desktop.Phases;
using Quartermaster.Render;
using Quartermaster.Rules;

namespace Quartermaster.Desktop.Tests;

/// <summary>Phase conditions: the Avalonia strip draws skipped / will-skip chips. Headless, invented phases.</summary>
public sealed class PhaseStripTests
{
    static PhaseView View(params PhaseChip[] chips) =>
        new(false, false, 2, null, null, chips, new PhaseDef { Id = "b", Name = "Movement" }, null, Array.Empty<Decision>(), Array.Empty<WarningHit>(), null);

    [AvaloniaFact]
    public void Strip_draws_skipped_and_will_skip_with_tips()
    {
        var strip = new PhaseStrip();
        var window = new Window { Content = strip };
        window.Show();
        strip.SetView(View(
            new PhaseChip("a", "Weather", ChipState.Done),
            new PhaseChip("b", "Rain", ChipState.Skipped, null, "Skipped this pass (condition false)"),
            new PhaseChip("c", "Movement", ChipState.Current, "Move notes"),
            new PhaseChip("d", "Snow", ChipState.WillSkip, null, "Will be skipped unless Flag changes"),
            new PhaseChip("e", "Combat", ChipState.Upcoming, "Fight")));

        Assert.Equal(new ChipState?[] { null, ChipState.Done, ChipState.Skipped, ChipState.Current, ChipState.WillSkip, ChipState.Upcoming }, strip.States);
        Assert.Equal("Skipped this pass (condition false)", strip.NotesFor("Rain"));
        Assert.Equal("Will be skipped unless Flag changes", strip.NotesFor("Snow"));
        Assert.Equal("Fight", strip.NotesFor("Combat"));   // no Tip: Notes stay the tooltip
        Assert.True(strip.IsStruckThrough("Rain"));
        Assert.False(strip.IsStruckThrough("Snow"));
        Assert.False(strip.IsStruckThrough("Combat"));
        Assert.Equal(1.0, strip.OpacityOf("Rain"));
        Assert.True(strip.OpacityOf("Snow") < 1.0);
        Assert.Equal(1.0, strip.OpacityOf("Combat"));
        window.Close();
    }
}

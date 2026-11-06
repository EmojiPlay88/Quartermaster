using Quartermaster.Vassal;

namespace Quartermaster.Vassal.Tests;

public class DiagnosticLogTests
{
    [Fact]
    public void AddOnce_DedupesByKey()
    {
        var log = new DiagnosticLog();
        var raised = 0;
        log.Added += _ => raised++;
        Assert.True(log.AddOnce("k", DiagnosticSeverity.Warning, "cat", "m"));
        Assert.False(log.AddOnce("k", DiagnosticSeverity.Warning, "cat", "m2"));
        Assert.True(log.AddOnce("k2", DiagnosticSeverity.Error, "cat", "m3", "ctx"));
        Assert.Equal(2, log.Items.Count);
        Assert.Equal(2, raised);
        Assert.Equal("ctx", log.Items[1].Context);
    }

    [Fact]
    public void Add_IsThreadSafe()
    {
        var log = new DiagnosticLog();
        Parallel.For(0, 1000, i => log.Add(DiagnosticSeverity.Info, "c", "m" + i));
        Assert.Equal(1000, log.Items.Count);
    }
}

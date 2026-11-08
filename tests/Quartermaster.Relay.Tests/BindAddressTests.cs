namespace Quartermaster.Relay.Tests;

public class BindAddressTests
{
    [Fact]
    public void Default_IsAny() => Assert.Equal("any", new RelayOptions().BindAddress);

    [Fact]
    public async Task Loopback_ListensOn127001Only()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-bind-" + Guid.NewGuid().ToString("N"));
        try
        {
            var app = RelayApp.Build([], b =>
            {
                b.Configuration["Relay:DataDir"] = dir;
                b.Configuration["Relay:Port"] = "0";
                b.Configuration["Relay:BindAddress"] = "loopback";
            });
            await app.StartAsync();
            try
            {
                Assert.NotEmpty(app.Urls);
                Assert.All(app.Urls, u => Assert.Equal("127.0.0.1", new Uri(u).Host));
            }
            finally { await app.StopAsync(); await app.DisposeAsync(); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Invalid_FailsAtStartup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qm-bind-" + Guid.NewGuid().ToString("N"));
        var e = Assert.Throws<InvalidOperationException>(() => RelayApp.Build([], b =>
        {
            b.Configuration["Relay:DataDir"] = dir;
            b.Configuration["Relay:BindAddress"] = "nonsense";
        }));
        Assert.Contains("BindAddress", e.Message);
    }
}

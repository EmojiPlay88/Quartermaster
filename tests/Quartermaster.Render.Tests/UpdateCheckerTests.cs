using System.Net;
using System.Text;
using Quartermaster.Render;
using Quartermaster.Render.Update;
using Quartermaster.Vassal;

namespace Quartermaster.Render.Tests;

/// <summary>Release parsing, throttle, check outcomes and the GitHub feed's request rules. No network: fake handler and temp folders only.</summary>
public class UpdateCheckerTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "qm-upd-check-" + Guid.NewGuid().ToString("N"));

    public UpdateCheckerTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch (IOException) { }
    }

    internal static string ReleaseJson(string tag = "quartermaster-v0.2.0", bool prerelease = false, bool draft = false, bool withSha = true, bool withZip = true, string ver = "0.2.0")
    {
        var assets = new List<string>();
        if (withZip)
            assets.Add($$"""{ "name": "Quartermaster-{{ver}}-win-x64.zip", "size": 12345, "browser_download_url": "https://github.com/EmojiPlay88/Quartermaster/releases/download/{{tag}}/Quartermaster-{{ver}}-win-x64.zip" }""");
        if (withSha)
            assets.Add($$"""{ "name": "Quartermaster-{{ver}}-win-x64.zip.sha256", "size": 99, "browser_download_url": "https://github.com/EmojiPlay88/Quartermaster/releases/download/{{tag}}/Quartermaster-{{ver}}-win-x64.zip.sha256" }""");
        assets.Add($$"""{ "name": "QuartermasterRelay-{{ver}}-linux-x64.tar.gz", "size": 5, "browser_download_url": "https://github.com/x/y" }""");
        return $$"""
        {
          "tag_name": "{{tag}}",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/EmojiPlay88/Quartermaster/releases/tag/{{tag}}",
          "body": "TEST notes\n- one",
          "assets": [ {{string.Join(",", assets)}} ]
        }
        """;
    }

    // ---------- Parse ----------

    [Fact]
    public void Parse_Valid_GivesVersionAndUrls()
    {
        var u = UpdateChecker.Parse(ReleaseJson());
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 2, 0), u!.Version);
        Assert.Equal("quartermaster-v0.2.0", u.Tag);
        Assert.Equal("TEST notes\n- one", u.Notes);
        Assert.Equal("https://github.com/EmojiPlay88/Quartermaster/releases/download/quartermaster-v0.2.0/Quartermaster-0.2.0-win-x64.zip", u.ZipUrl.ToString());
        Assert.EndsWith("Quartermaster-0.2.0-win-x64.zip.sha256", u.ShaUrl.ToString());
        Assert.Equal(12345, u.ZipSize);
        Assert.Equal("https://github.com/EmojiPlay88/Quartermaster/releases/tag/quartermaster-v0.2.0", u.PageUrl.ToString());
    }

    [Theory]
    [InlineData("v0.2.0")]
    [InlineData("quartermasterrelay-v0.2.0")]
    [InlineData("quartermaster-v0.2.0-beta")]
    public void Parse_WrongTag_Null(string tag) => Assert.Null(UpdateChecker.Parse(ReleaseJson(tag: tag)));

    [Theory]
    [InlineData("quartermaster-v0.2")]
    [InlineData("quartermaster-v0.2.x")]
    [InlineData("quartermaster-v99999999999.0.0")]
    public void Parse_BadSemver_Null(string tag) => Assert.Null(UpdateChecker.Parse(ReleaseJson(tag: tag)));

    [Fact] public void Parse_Prerelease_Null() => Assert.Null(UpdateChecker.Parse(ReleaseJson(prerelease: true)));
    [Fact] public void Parse_Draft_Null() => Assert.Null(UpdateChecker.Parse(ReleaseJson(draft: true)));
    [Fact] public void Parse_MissingSha_Null() => Assert.Null(UpdateChecker.Parse(ReleaseJson(withSha: false)));
    [Fact] public void Parse_MissingZip_Null() => Assert.Null(UpdateChecker.Parse(ReleaseJson(withZip: false)));
    [Fact] public void Parse_AssetsForOtherVersion_Null() => Assert.Null(UpdateChecker.Parse(ReleaseJson(ver: "0.1.9")));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{ \"tag_name\": 5 }")]
    public void Parse_Garbage_Null(string json) => Assert.Null(UpdateChecker.Parse(json));

    // ---------- Due ----------

    [Fact]
    public void Due_Throttles24Hours()
    {
        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateChecker.Due(null, now));
        Assert.False(UpdateChecker.Due(now.AddHours(-23), now));
        Assert.True(UpdateChecker.Due(now.AddHours(-24), now));
        Assert.True(UpdateChecker.Due(now.AddDays(3), now)); // clock went back: do not block checks forever
    }

    // ---------- Check ----------

    sealed class StubFeed(Func<string?> json) : IUpdateFeed
    {
        public Task<string?> LatestJson(CancellationToken ct) => Task.FromResult(json());
        public Task<Stream> Open(Uri asset, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Check_Newer_Available()
    {
        var r = await UpdateChecker.Check(new StubFeed(() => ReleaseJson()), new Version(0, 1, 0), null, default);
        Assert.Equal(CheckOutcome.Available, r.Outcome);
        Assert.Equal(new Version(0, 2, 0), r.Info!.Version);
        Assert.Null(r.Error);
    }

    [Theory]
    [InlineData(0, 2, 0)]
    [InlineData(0, 3, 1)]
    public async Task Check_SameOrOlder_UpToDate(int a, int b, int c)
    {
        var r = await UpdateChecker.Check(new StubFeed(() => ReleaseJson()), new Version(a, b, c), null, default);
        Assert.Equal(CheckOutcome.UpToDate, r.Outcome);
    }

    [Fact]
    public async Task Check_NoUsableRelease_UpToDate()
    {
        Assert.Equal(CheckOutcome.UpToDate, (await UpdateChecker.Check(new StubFeed(() => null), new Version(0, 1, 0), null, default)).Outcome);
        Assert.Equal(CheckOutcome.UpToDate, (await UpdateChecker.Check(new StubFeed(() => ReleaseJson(prerelease: true)), new Version(0, 1, 0), null, default)).Outcome);
    }

    [Fact]
    public async Task Check_NewerButSkipped_Skipped_AndNewerStillOffered()
    {
        var r = await UpdateChecker.Check(new StubFeed(() => ReleaseJson()), new Version(0, 1, 0), "0.2.0", default);
        Assert.Equal(CheckOutcome.Skipped, r.Outcome);
        Assert.NotNull(r.Info);
        var r2 = await UpdateChecker.Check(new StubFeed(() => ReleaseJson()), new Version(0, 1, 0), "0.1.5", default);
        Assert.Equal(CheckOutcome.Available, r2.Outcome);
    }

    [Fact]
    public async Task Check_FeedThrows_Failed()
    {
        var r = await UpdateChecker.Check(new StubFeed(() => throw new InvalidOperationException("boom")), new Version(0, 1, 0), null, default);
        Assert.Equal(CheckOutcome.Failed, r.Outcome);
        Assert.Contains("boom", r.Error);
    }

    [Fact]
    public async Task Check_RateLimited403_FailedWithMessage()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") });
        var r = await UpdateChecker.Check(new GitHubFeed(new HttpClient(h)), new Version(0, 1, 0), null, default);
        Assert.Equal(CheckOutcome.Failed, r.Outcome);
        Assert.Contains("limit", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Check_Offline_Failed()
    {
        var h = new FakeHandler(_ => throw new HttpRequestException("No such host is known."));
        var r = await UpdateChecker.Check(new GitHubFeed(new HttpClient(h)), new Version(0, 1, 0), null, default);
        Assert.Equal(CheckOutcome.Failed, r.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public async Task Check_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateChecker.Check(new GitHubFeed(new HttpClient(h)), new Version(0, 1, 0), null, cts.Token));
    }

    // ---------- GitHubFeed ----------

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request);
            var r = respond(request);
            r.RequestMessage = request;
            return Task.FromResult(r);
        }
    }

    static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return r;
    }

    static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public void AppVersion_Is_0_1_0()
    {
        Assert.Equal("0.1.0", AppVersion.Text);
        Assert.Equal(new Version(0, 1, 0), AppVersion.Current);
    }

    [Fact]
    public async Task GitHubFeed_SendsUserAgent_AndHitsLatestApi()
    {
        var h = new FakeHandler(_ => Ok(ReleaseJson()));
        var json = await new GitHubFeed(new HttpClient(h)).LatestJson(default);
        Assert.NotNull(json);
        var req = Assert.Single(h.Requests);
        Assert.Equal("Quartermaster-Updater/0.1.0", req.Headers.UserAgent.ToString());
        Assert.Equal("https://api.github.com/repos/EmojiPlay88/Quartermaster/releases/latest", req.RequestUri!.ToString());
    }

    [Fact]
    public async Task GitHubFeed_NoRelease404_Null()
    {
        var h = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        Assert.Null(await new GitHubFeed(new HttpClient(h)).LatestJson(default));
    }

    [Fact]
    public async Task GitHubFeed_RedirectToEvilHost_Throws()
    {
        var h = new FakeHandler(r => r.RequestUri!.Host == "github.com" ? Redirect("https://evil.example/x.zip") : Ok("pwned"));
        var ex = await Assert.ThrowsAsync<UpdateRefused>(() => new GitHubFeed(new HttpClient(h)).Open(new Uri("https://github.com/EmojiPlay88/Quartermaster/releases/download/t/a.zip"), default));
        Assert.Contains("evil.example", ex.Message);
        Assert.Single(h.Requests); // never contacted the evil host
    }

    [Fact]
    public async Task GitHubFeed_SecondHopChecked()
    {
        var h = new FakeHandler(r => r.RequestUri!.Host switch
        {
            "github.com" => Redirect("https://objects.githubusercontent.com/a"),
            "objects.githubusercontent.com" => Redirect("https://evil.example/b"),
            _ => Ok("pwned"),
        });
        await Assert.ThrowsAsync<UpdateRefused>(() => new GitHubFeed(new HttpClient(h)).Open(new Uri("https://github.com/x/a.zip"), default));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task GitHubFeed_RedirectToGitHubAssetHost_Ok()
    {
        var h = new FakeHandler(r => r.RequestUri!.Host == "github.com" ? Redirect("https://objects.githubusercontent.com/blob/1") : Ok("ZIPDATA"));
        await using var s = await new GitHubFeed(new HttpClient(h)).Open(new Uri("https://github.com/x/a.zip"), default);
        using var rd = new StreamReader(s);
        Assert.Equal("ZIPDATA", await rd.ReadToEndAsync());
        Assert.Equal(2, h.Requests.Count);
        Assert.All(h.Requests, q => Assert.Equal("Quartermaster-Updater/0.1.0", q.Headers.UserAgent.ToString()));
    }

    [Fact]
    public async Task GitHubFeed_RelativeRedirect_ResolvedOnSameHost()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/x/a.zip" ? Redirect("/y/b.zip") : Ok("B"));
        await using var s = await new GitHubFeed(new HttpClient(h)).Open(new Uri("https://github.com/x/a.zip"), default);
        Assert.Equal("https://github.com/y/b.zip", h.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task GitHubFeed_Http_Throws()
    {
        var h = new FakeHandler(_ => Ok("x"));
        await Assert.ThrowsAsync<UpdateRefused>(() => new GitHubFeed(new HttpClient(h)).Open(new Uri("http://github.com/x/a.zip"), default));
        Assert.Empty(h.Requests);
        var h2 = new FakeHandler(r => r.RequestUri!.Scheme == "https" ? Redirect("http://objects.githubusercontent.com/a") : Ok("x"));
        await Assert.ThrowsAsync<UpdateRefused>(() => new GitHubFeed(new HttpClient(h2)).Open(new Uri("https://github.com/x/a.zip"), default));
        Assert.Single(h2.Requests);
    }

    [Fact]
    public async Task GitHubFeed_UnknownHostAsset_Throws()
    {
        var h = new FakeHandler(_ => Ok("x"));
        await Assert.ThrowsAsync<UpdateRefused>(() => new GitHubFeed(new HttpClient(h)).Open(new Uri("https://evil.example/a.zip"), default));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task GitHubFeed_RedirectLoop_Throws()
    {
        var h = new FakeHandler(_ => Redirect("https://github.com/again"));
        await Assert.ThrowsAsync<UpdateRefused>(() => new GitHubFeed(new HttpClient(h)).Open(new Uri("https://github.com/x"), default));
        Assert.True(h.Requests.Count <= 11);
    }

    // ---------- FolderFeed / UpdateFeeds ----------

    [Fact]
    public async Task FolderFeed_ReadsReleaseJsonAndAssetsByFileName()
    {
        File.WriteAllText(Path.Combine(_tmp, "release.json"), ReleaseJson());
        File.WriteAllText(Path.Combine(_tmp, "Quartermaster-0.2.0-win-x64.zip.sha256"), "abc");
        var f = new FolderFeed(_tmp);
        Assert.Equal(ReleaseJson(), await f.LatestJson(default));
        var u = UpdateChecker.Parse((await f.LatestJson(default))!)!;
        await using var s = await f.Open(u.ShaUrl, default);
        Assert.Equal("abc", await new StreamReader(s).ReadToEndAsync());
        await using var s2 = await f.Open(new Uri("Quartermaster-0.2.0-win-x64.zip.sha256", UriKind.Relative), default);
    }

    [Fact]
    public async Task FolderFeed_NoReleaseJson_Null_AndTraversalRefused()
    {
        var f = new FolderFeed(_tmp);
        Assert.Null(await f.LatestJson(default));
        await Assert.ThrowsAsync<UpdateRefused>(() => f.Open(new Uri("..\\secret.txt", UriKind.Relative), default));
    }

    /// <summary>Refused the same on every OS: either separator, and the characters Windows forbids, even where this OS would allow them.</summary>
    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("sub\\secret.txt")]
    [InlineData("sub/secret.txt")]
    [InlineData("a*b.zip")]
    [InlineData("a<b.zip")]
    [InlineData("a?.zip")]
    [InlineData("..")]
    public async Task FolderFeed_RefusesNamesThatAreNotPortableFileNames(string name)
    {
        var f = new FolderFeed(_tmp);
        await Assert.ThrowsAsync<UpdateRefused>(() => f.Open(new Uri(name, UriKind.Relative), default));
    }

    [Fact]
    public void UpdateFeeds_Default_EnvVarWins()
    {
        var old = Environment.GetEnvironmentVariable(UpdateFeeds.EnvVar);
        try
        {
            Assert.Equal("QUARTERMASTER_UPDATE_FEED", UpdateFeeds.EnvVar);
            Environment.SetEnvironmentVariable(UpdateFeeds.EnvVar, _tmp);
            Assert.IsType<FolderFeed>(UpdateFeeds.Default(new HttpClient()));
            Environment.SetEnvironmentVariable(UpdateFeeds.EnvVar, null);
            Assert.IsType<GitHubFeed>(UpdateFeeds.Default(new HttpClient()));
        }
        finally { Environment.SetEnvironmentVariable(UpdateFeeds.EnvVar, old); }
    }

    // ---------- Settings ----------

    [Fact]
    public void Settings_UpdateFields_RoundTripAndTolerant()
    {
        var d = new Settings();
        Assert.True(d.CheckUpdatesOnStart);
        Assert.Null(d.LastUpdateCheckUtc);
        Assert.Null(d.SkippedVersion);
        var when = new DateTime(2026, 11, 8, 10, 30, 0, DateTimeKind.Utc);
        new SettingsStore(_tmp).Save(new Settings { CheckUpdatesOnStart = false, LastUpdateCheckUtc = when, SkippedVersion = "0.2.0" });
        var back = new SettingsStore(_tmp).Load(new DiagnosticLog());
        Assert.False(back.CheckUpdatesOnStart);
        Assert.Equal(when, back.LastUpdateCheckUtc);
        Assert.Equal(DateTimeKind.Utc, back.LastUpdateCheckUtc!.Value.Kind);
        Assert.Equal("0.2.0", back.SkippedVersion);

        File.WriteAllText(Path.Combine(_tmp, "settings.json"), "{ \"CheckUpdatesOnStart\": 3, \"LastUpdateCheckUtc\": \"yesterday\", \"SkippedVersion\": \"not a version\" }");
        var bad = new SettingsStore(_tmp).Load(new DiagnosticLog());
        Assert.True(bad.CheckUpdatesOnStart);
        Assert.Null(bad.LastUpdateCheckUtc);
        Assert.Null(bad.SkippedVersion);
    }
}

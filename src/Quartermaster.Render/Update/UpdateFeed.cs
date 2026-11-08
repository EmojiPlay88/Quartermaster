using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Quartermaster.Render.Update;

/// <summary>Where releases come from: the latest release as GitHub API JSON (null = there is none) and its asset files.</summary>
public interface IUpdateFeed
{
    Task<string?> LatestJson(CancellationToken ct);
    Task<Stream> Open(Uri asset, CancellationToken ct);
}

/// <summary>
/// GitHub Releases of <see cref="Repo"/> over HTTPS. Every request carries <c>User-Agent: Quartermaster-Updater/&lt;version&gt;</c> and nothing
/// personal. Redirects are followed by hand, each hop checked: https only and the host must be in <see cref="AllowedHosts"/>.
/// Give it a client from <see cref="CreateClient"/> (auto-redirect off); with an auto-redirecting client the final address is still checked.
/// </summary>
public sealed class GitHubFeed(HttpClient http) : IUpdateFeed
{
    public const string Repo = "EmojiPlay88/Quartermaster";
    public static readonly Uri LatestUrl = new($"https://api.github.com/repos/{Repo}/releases/latest");

    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com",
    };

    const int MaxHops = 10;
    const int MaxJsonBytes = 4 * 1024 * 1024;

    public static string UserAgent => "Quartermaster-Updater/" + AppVersion.Text;

    /// <summary>An HttpClient that never follows redirects on its own (this feed follows and checks them).</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromMinutes(10),
    };

    public async Task<string?> LatestJson(CancellationToken ct)
    {
        using var r = await Send(LatestUrl, "application/vnd.github+json", ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null; // the repo has no release yet
        EnsureOk(r);
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int n;
        while ((n = await s.ReadAsync(chunk, ct)) > 0)
        {
            if (buf.Length + n > MaxJsonBytes) throw new InvalidDataException("The release information from GitHub is too large.");
            buf.Write(chunk, 0, n);
        }
        return Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);
    }

    public async Task<Stream> Open(Uri asset, CancellationToken ct)
    {
        var r = await Send(asset, "application/octet-stream", ct);
        try
        {
            EnsureOk(r);
            return new OwningStream(await r.Content.ReadAsStreamAsync(ct), r);
        }
        catch
        {
            r.Dispose();
            throw;
        }
    }

    /// <summary>Sends GET to <paramref name="start"/>, following up to <see cref="MaxHops"/> redirects; every address is checked before it is contacted.</summary>
    async Task<HttpResponseMessage> Send(Uri start, string accept, CancellationToken ct)
    {
        var uri = start;
        for (var hop = 0; ; hop++)
        {
            CheckAllowed(uri);
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!IsRedirect(r.StatusCode))
            {
                // An auto-redirecting client hides the hops; at least the address that answered must be allowed.
                if (r.RequestMessage?.RequestUri is { } final && final != uri)
                {
                    try { CheckAllowed(final); }
                    catch { r.Dispose(); throw; }
                }
                return r;
            }
            var location = r.Headers.Location;
            var code = (int)r.StatusCode;
            r.Dispose();
            if (location == null) throw new HttpRequestException($"GitHub answered HTTP {code} without a redirect address.");
            if (hop + 1 >= MaxHops) throw new UpdateRefused("Too many redirects while downloading the update.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    static bool IsRedirect(HttpStatusCode c) => c is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Throws <see cref="UpdateRefused"/> unless the address is https on an allowed GitHub host.</summary>
    public static void CheckAllowed(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateRefused($"Refused to download from {uri}: only https addresses are allowed.");
        if (!AllowedHosts.Contains(uri.IdnHost) || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
            throw new UpdateRefused($"Refused to download from {uri.Host}: only GitHub addresses are allowed.");
    }

    static void EnsureOk(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return;
        if (r.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"GitHub refused the request (HTTP {(int)r.StatusCode}); the hourly limit for update checks is probably reached. Try again later.", null, r.StatusCode);
        throw new HttpRequestException($"GitHub answered HTTP {(int)r.StatusCode} {r.ReasonPhrase}.", null, r.StatusCode);
    }

    /// <summary>The response body; disposing it also disposes the response.</summary>
    sealed class OwningStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// A local test feed (tests and the hands-on update test): <c>release.json</c> in the GitHub API shape plus the asset files. An asset
/// address resolves to its last path segment (the file name) inside the folder; anything that would leave the folder, or is not a file name
/// on every OS (a '/' or '\' anywhere, ':' and the other characters Windows forbids), is refused, the same on every OS.
/// </summary>
public sealed class FolderFeed(string folder) : IUpdateFeed
{
    public string Folder => folder;

    public async Task<string?> LatestJson(CancellationToken ct)
    {
        var path = Path.Combine(folder, "release.json");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path, ct);
    }

    public Task<Stream> Open(Uri asset, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var name = asset.IsAbsoluteUri ? Uri.UnescapeDataString(asset.Segments[^1]) : asset.OriginalString;
        // Portable rules, not this OS's: '\' is a separator on Windows only, so "..\x" must be refused everywhere, as on Windows.
        if (name.Length == 0 || name is "." or ".." || PortableFileName.HasInvalidChars(name) || name != Path.GetFileName(name))
            throw new UpdateRefused($"Refused test-feed asset name '{name}'.");
        return Task.FromResult<Stream>(new FileStream(Path.Combine(folder, name), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }
}

public static class UpdateFeeds
{
    /// <summary>Points the updater at a local <see cref="FolderFeed"/> instead of GitHub.</summary>
    public const string EnvVar = "QUARTERMASTER_UPDATE_FEED";

    /// <summary>The test feed when <see cref="EnvVar"/> is set, otherwise GitHub through <paramref name="http"/> (see <see cref="GitHubFeed.CreateClient"/>).</summary>
    public static IUpdateFeed Default(HttpClient http) =>
        Environment.GetEnvironmentVariable(EnvVar) is { } dir && !string.IsNullOrWhiteSpace(dir) ? new FolderFeed(dir) : new GitHubFeed(http);
}

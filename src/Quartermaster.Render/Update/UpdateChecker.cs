using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quartermaster.Render.Update;

/// <summary>Reads the latest release, decides whether it is an update for this app, and throttles the automatic check.</summary>
public static class UpdateChecker
{
    static readonly Regex TagPattern = new(@"^quartermaster-v(\d+)\.(\d+)\.(\d+)$", RegexOptions.CultureInvariant);

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public static string ZipName(Version v) => AssetName(v, PackageKind.WinZip);

    /// <summary>The release asset that updates an install of the given package kind.</summary>
    public static string AssetName(Version v, PackageKind k)
    {
        var f = AppVersion.Format(v);
        return k switch
        {
            PackageKind.WinZip or PackageKind.WinSetup => $"Quartermaster-{f}-win-x64.zip",
            PackageKind.MacApp => $"Quartermaster-{f}-osx-arm64.zip",
            PackageKind.LinuxTar => $"Quartermaster-{f}-linux-x64.tar.gz",
            PackageKind.LinuxAppImage => $"Quartermaster-{f}-x86_64.AppImage",
            PackageKind.LinuxDeb => $"quartermaster_{f}_amd64.deb",
            PackageKind.LinuxRpm => $"quartermaster-{f}-1.x86_64.rpm",
            _ => throw new ArgumentOutOfRangeException(nameof(k)),
        };
    }

    /// <summary>
    /// The release as an update, or null when it does not count: tag not <c>quartermaster-v&lt;major.minor.patch&gt;</c>, draft or
    /// pre-release, the Windows asset (<c>Quartermaster-&lt;v&gt;-win-x64.zip</c>) or its <c>.sha256</c> missing, or the JSON is not a release object.
    /// </summary>
    public static UpdateInfo? Parse(string releaseJson) => Parse(releaseJson, PackageKind.WinZip);

    /// <summary>
    /// As <see cref="Parse(string)"/>, but for the asset of <paramref name="kind"/> (named by <see cref="AssetName"/>: the Windows or macOS zip,
    /// the Linux tar.gz, AppImage, .deb or .rpm): null when that asset or its <c>.sha256</c> is missing.
    /// </summary>
    public static UpdateInfo? Parse(string releaseJson, PackageKind kind)
    {
        try
        {
            using var doc = JsonDocument.Parse(releaseJson);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            if (Str(o, "tag_name") is not { } tag) return null;
            var m = TagPattern.Match(tag);
            if (!m.Success) return null;
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                || !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch)) return null;
            if (Bool(o, "draft") || Bool(o, "prerelease")) return null;
            var version = new Version(major, minor, patch);

            var zipName = AssetName(version, kind);
            var shaName = zipName + ".sha256";
            (Uri Url, long Size)? zip = null, sha = null;
            if (o.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    if (a.ValueKind != JsonValueKind.Object || Str(a, "name") is not { } name) continue;
                    if (name != zipName && name != shaName) continue;
                    if (Str(a, "browser_download_url") is not { } url || !Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out var uri)) continue;
                    var size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var l) ? l : -1;
                    if (name == zipName) zip = (uri, size); else sha = (uri, size);
                }
            if (zip is not { Size: > 0 } z || sha is not { } s) return null;

            var page = Str(o, "html_url") is { } h && Uri.TryCreate(h, UriKind.Absolute, out var hu) && hu.Scheme == Uri.UriSchemeHttps
                ? hu : new Uri($"https://github.com/{GitHubFeed.Repo}/releases/tag/{tag}");
            return new UpdateInfo(version, tag, Str(o, "body") ?? "", z.Url, s.Url, z.Size, page, kind, zipName);
        }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>The automatic check runs when it never ran, 24 h have passed, or the last check is in the future (clock changed).</summary>
    public static bool Due(DateTime? lastUtc, DateTime nowUtc) =>
        lastUtc is not { } last || nowUtc - last >= Interval || last > nowUtc;

    /// <summary>Asks the feed for the latest release. Never throws except on cancellation of <paramref name="ct"/>; problems come back as Failed with a plain message.</summary>
    public static async Task<CheckResult> Check(IUpdateFeed feed, Version current, string? skipped, CancellationToken ct, PackageKind kind = PackageKind.WinZip)
    {
        string? json;
        try
        {
            json = await feed.LatestJson(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new CheckResult(CheckOutcome.Failed, null, Describe(ex));
        }
        if (json == null || Parse(json, kind) is not { } info || info.Version <= Normalize(current))
            return new CheckResult(CheckOutcome.UpToDate, null, null);
        if (skipped != null && Version.TryParse(skipped, out var sv) && Normalize(sv) == info.Version)
            return new CheckResult(CheckOutcome.Skipped, info, null);
        return new CheckResult(CheckOutcome.Available, info, null);
    }

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "The update check timed out.",
        HttpRequestException { StatusCode: null } h => "Could not reach GitHub (offline?): " + h.Message,
        _ => ex.Message,
    };
}

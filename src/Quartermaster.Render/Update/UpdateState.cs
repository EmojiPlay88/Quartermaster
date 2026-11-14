namespace Quartermaster.Render.Update;

/// <summary>
/// A usable app release: version, tag, notes (Markdown as text), the asset for this install's <see cref="Kind"/> (named <see cref="AssetName"/>:
/// a zip for Windows and macOS, a tar.gz, AppImage, .deb or .rpm for Linux) and its <c>.sha256</c>, the asset's size and the release page.
/// ZipUrl/ZipSize keep their 0.1.0 names but mean that asset, whatever its kind.
/// </summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri ZipUrl, Uri ShaUrl, long ZipSize, Uri PageUrl,
    PackageKind Kind = PackageKind.WinZip, string AssetName = "");

public enum CheckOutcome { UpToDate, Available, Skipped, Failed }

/// <summary>What a check found. <see cref="Info"/> is set for Available and Skipped; <see cref="Error"/> for Failed.</summary>
public sealed record CheckResult(CheckOutcome Outcome, UpdateInfo? Info, string? Error);

/// <summary>
/// A verified, staged update; <see cref="Root"/> = <c>&lt;updatesRoot&gt;\&lt;version&gt;</c>. <see cref="StagedDir"/> is what the kind hands on:
/// <c>&lt;Root&gt;\staged</c> (Windows zip, Linux tar.gz), <c>&lt;Root&gt;\staged\Quartermaster.app</c> (macOS), <c>&lt;Root&gt;\staged\Quartermaster.AppImage</c>
/// (AppImage) or the downloaded package file in <c>&lt;Root&gt;</c> (.deb, .rpm).
/// </summary>
public sealed record StagedUpdate(Version Version, string StagedDir, string Root);

/// <summary>The updater will not go on; the message is plain text for the user.</summary>
public sealed class UpdateRefused(string message) : Exception(message);

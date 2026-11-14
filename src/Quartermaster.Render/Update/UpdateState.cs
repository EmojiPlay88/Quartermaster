namespace Quartermaster.Render.Update;

/// <summary>A usable app release: version, tag, notes (Markdown as text), the app zip and its <c>.sha256</c>, the zip's size and the release page. ZipUrl/ZipSize mean "the asset" for <see cref="Kind"/>, named <see cref="AssetName"/>.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, Uri ZipUrl, Uri ShaUrl, long ZipSize, Uri PageUrl,
    PackageKind Kind = PackageKind.WinZip, string AssetName = "");

public enum CheckOutcome { UpToDate, Available, Skipped, Failed }

/// <summary>What a check found. <see cref="Info"/> is set for Available and Skipped; <see cref="Error"/> for Failed.</summary>
public sealed record CheckResult(CheckOutcome Outcome, UpdateInfo? Info, string? Error);

/// <summary>A verified, extracted update: <see cref="StagedDir"/> = <c>&lt;Root&gt;\staged</c>, <see cref="Root"/> = <c>&lt;updatesRoot&gt;\&lt;version&gt;</c>.</summary>
public sealed record StagedUpdate(Version Version, string StagedDir, string Root);

/// <summary>The updater will not go on; the message is plain text for the user.</summary>
public sealed class UpdateRefused(string message) : Exception(message);

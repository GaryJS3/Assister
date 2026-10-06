namespace Assister.Contracts;

public sealed record ClientRelease(string AppId, string Platform, string Version, string FileName,
    long Size, string Sha256, DateTimeOffset PublishedAt, string? Notes, string DownloadUrl);
public sealed record ClientUpdateCheck(bool UpdateAvailable, ClientRelease? Release);

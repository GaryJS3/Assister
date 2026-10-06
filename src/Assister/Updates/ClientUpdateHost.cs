using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assister.Contracts;
using Microsoft.AspNetCore.Http.Features;

namespace Assister.Updates;

public sealed partial class ClientUpdateHost
{
    public const long MaximumBytes = 512L * 1024 * 1024;
    private readonly string Root;
    private readonly string? UploadToken;
    private readonly SemaphoreSlim PublishLock = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ClientUpdateHost(IConfiguration Configuration, IHostEnvironment Environment)
    {
        Root = Path.Combine(Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data"), "client-releases");
        UploadToken = Configuration["ClientUpdates:UploadToken"] ?? (Environment.IsDevelopment() ? "dev" : null);
        Directory.CreateDirectory(Root);
    }

    public static bool ValidKey(string Value) => KeyPattern().IsMatch(Value);
    [GeneratedRegex("\\A[a-z0-9][a-z0-9-]{0,63}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static bool TryVersion(string Value, out Version Version)
    {
        Version = new Version(0, 0, 0, 0);
        if (Value.Length > 40 || !System.Version.TryParse(Value, out var Parsed)) return false;
        Version = new Version(Parsed.Major, Parsed.Minor, Math.Max(0, Parsed.Build), Math.Max(0, Parsed.Revision));
        return true;
    }

    public ClientRelease[] List() => Directory.EnumerateDirectories(Root)
        .Where(Directory => !Path.GetFileName(Directory).StartsWith('.'))
        .Select(Directory => JsonSerializer.Deserialize<ClientRelease>(File.ReadAllText(Path.Combine(Directory, "release.json")), Json)!)
        .OrderBy(Release => Release.AppId).ThenBy(Release => Release.Platform)
        .ThenByDescending(Release => System.Version.Parse(Release.Version)).ToArray();

    private string ReleasePath(string AppId, string Platform, string Version) => Path.Combine(Root, $"{AppId}_{Platform}_{Version}");

    public IResult Download(string AppId, string Platform, string Version)
    {
        if (!ValidKey(AppId) || !ValidKey(Platform) || !TryVersion(Version, out var Parsed)) return Results.BadRequest();
        var Directory = ReleasePath(AppId, Platform, Parsed.ToString());
        var Manifest = Path.Combine(Directory, "release.json");
        if (!File.Exists(Manifest)) return Results.NotFound();
        var Release = JsonSerializer.Deserialize<ClientRelease>(File.ReadAllText(Manifest), Json)!;
        return Results.File(Path.Combine(Directory, "package"),
            Release.FileName.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) ? "application/vnd.android.package-archive" : "application/octet-stream",
            Release.FileName, enableRangeProcessing: true, lastModified: Release.PublishedAt,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{Release.Sha256}\""));
    }

    public async Task<IResult> UploadAsync(HttpContext Http, string AppId, string Platform, string Version, CancellationToken Token)
    {
        var Authorization = Http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(UploadToken)) return Results.Problem("Upload authentication is not configured.", statusCode: 503);
        if (!Authorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(Authorization[7..])),
                SHA256.HashData(Encoding.UTF8.GetBytes(UploadToken)))) return Results.Unauthorized();
        var Name = Http.Request.Query["name"].ToString();
        var Notes = Http.Request.Query["notes"].ToString();
        if (!ValidKey(AppId) || !ValidKey(Platform) || !TryVersion(Version, out var Parsed) ||
            Name.Length is < 5 or > 128 || Name.Any(Character => char.IsControl(Character) || "/\\:<>\"|?*".Contains(Character)) ||
            !(Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) || Notes.Length > 4000)
            return Results.BadRequest(new { Error = "Use lowercase app/platform identifiers, a numeric version (major.minor[.build[.revision]]), and an APK or EXE filename. Notes are limited to 4000 characters." });
        var Limit = Http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (Limit is { IsReadOnly: false }) Limit.MaxRequestBodySize = MaximumBytes;
        if (Http.Request.ContentLength > MaximumBytes) return Results.StatusCode(413);
        var Destination = ReleasePath(AppId, Platform, Parsed.ToString());
        if (Directory.Exists(Destination)) return Results.Conflict(new { Error = "This version is already published. Upload a new version." });
        var Temporary = Path.Combine(Root, $".upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Temporary);
        try
        {
            long Size = 0;
            using var Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var File = new FileStream(Path.Combine(Temporary, "package"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var Buffer = new byte[81920];
                int Read;
                while ((Read = await Http.Request.Body.ReadAsync(Buffer, Token)) > 0)
                {
                    Size += Read;
                    if (Size > MaximumBytes) return Results.StatusCode(413);
                    Hash.AppendData(Buffer, 0, Read);
                    await File.WriteAsync(Buffer.AsMemory(0, Read), Token);
                }
            }
            if (Size == 0) return Results.BadRequest(new { Error = "The package is empty." });
            var Url = $"/api/updates/{AppId}/{Platform}/{Parsed}/download";
            var Release = new ClientRelease(AppId, Platform, Parsed.ToString(), Name, Size,
                Convert.ToHexStringLower(Hash.GetHashAndReset()), DateTimeOffset.UtcNow, Notes.Length == 0 ? null : Notes, Url);
            await File.WriteAllTextAsync(Path.Combine(Temporary, "release.json"), JsonSerializer.Serialize(Release, Json), Token);
            await PublishLock.WaitAsync(Token);
            try
            {
                if (Directory.Exists(Destination)) return Results.Conflict(new { Error = "This version is already published." });
                Directory.Move(Temporary, Destination);
            }
            finally { PublishLock.Release(); }
            return Results.Created(Url, Release);
        }
        finally { if (Directory.Exists(Temporary)) Directory.Delete(Temporary, true); }
    }
}

public static class ClientUpdateEndpoints
{
    public static void MapClientUpdates(this WebApplication App)
    {
        App.MapGet("/api/updates", (ClientUpdateHost Host) => Results.Ok(Host.List()));
        App.MapGet("/api/updates/{appId}/{platform}/check", (string AppId, string Platform, string CurrentVersion, ClientUpdateHost Host, HttpContext Http) =>
        {
            Http.Response.Headers.CacheControl = "no-store";
            if (!ClientUpdateHost.ValidKey(AppId) || !ClientUpdateHost.ValidKey(Platform) || !ClientUpdateHost.TryVersion(CurrentVersion, out var Current))
                return Results.BadRequest(new { Error = "Supply valid app/platform identifiers and currentVersion." });
            var Latest = Host.List().FirstOrDefault(Release => Release.AppId == AppId && Release.Platform == Platform);
            var Available = Latest is not null && System.Version.Parse(Latest.Version) > Current;
            return Results.Ok(new ClientUpdateCheck(Available, Available ? Latest : null));
        });
        App.MapGet("/api/updates/{appId}/{platform}/{version}/download", (string AppId, string Platform, string Version, ClientUpdateHost Host) => Host.Download(AppId, Platform, Version));
        App.MapPut("/api/updates/{appId}/{platform}/{version}", (HttpContext Http, string AppId, string Platform, string Version, ClientUpdateHost Host, CancellationToken Token)
            => Host.UploadAsync(Http, AppId, Platform, Version, Token));
    }
}

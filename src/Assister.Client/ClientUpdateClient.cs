using System.Net.Http.Json;
using System.Security.Cryptography;
using Assister.Contracts;

namespace Assister.Client;

// Use a dedicated HttpClient with the update host's HTTPS BaseAddress.
public sealed class ClientUpdateClient(HttpClient Http)
{
    public async Task<ClientUpdateCheck> CheckAsync(string AppId, string Platform, string CurrentVersion, CancellationToken Token = default)
        => await Http.GetFromJsonAsync<ClientUpdateCheck>($"api/updates/{Uri.EscapeDataString(AppId)}/{Uri.EscapeDataString(Platform)}/check?currentVersion={Uri.EscapeDataString(CurrentVersion)}", Token)
            ?? throw new InvalidDataException("The update host returned no result.");

    public async Task DownloadAsync(ClientRelease Release, string Destination, CancellationToken Token = default)
    {
        if (Release.Size is <= 0 or > 512L * 1024 * 1024 || Release.Sha256.Length != 64)
            throw new InvalidDataException("Invalid update metadata.");
        var Target = Path.GetFullPath(Destination);
        var Temporary = Target + $".{Guid.NewGuid():N}.partial";
        try
        {
            // Construct a host-relative URL so metadata cannot redirect credentials to another host.
            var Url = $"api/updates/{Uri.EscapeDataString(Release.AppId)}/{Uri.EscapeDataString(Release.Platform)}/{Uri.EscapeDataString(Release.Version)}/download";
            using var Response = await Http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead, Token);
            Response.EnsureSuccessStatusCode();
            await using var Source = await Response.Content.ReadAsStreamAsync(Token);
            using var Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long Size = 0;
            await using (var Output = new FileStream(Temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var Buffer = new byte[81920];
                int Read;
                while ((Read = await Source.ReadAsync(Buffer, Token)) > 0)
                {
                    Size += Read;
                    if (Size > Release.Size) throw new InvalidDataException("The package exceeds its advertised size.");
                    Hash.AppendData(Buffer, 0, Read);
                    await Output.WriteAsync(Buffer.AsMemory(0, Read), Token);
                }
            }
            if (Size != Release.Size || !string.Equals(Convert.ToHexString(Hash.GetHashAndReset()), Release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update package failed size or SHA-256 verification.");
            File.Move(Temporary, Target, overwrite: true);
        }
        finally { if (File.Exists(Temporary)) File.Delete(Temporary); }
    }

    // The app implements its OS installer flow (and prompts/restart) in Install.
    public async Task<bool> UpdateAsync(string AppId, string Platform, string CurrentVersion, string Destination,
        Func<string, CancellationToken, Task> Install, CancellationToken Token = default)
    {
        var Check = await CheckAsync(AppId, Platform, CurrentVersion, Token);
        if (!Check.UpdateAvailable || Check.Release is null) return false;
        await DownloadAsync(Check.Release, Destination, Token);
        await Install(Path.GetFullPath(Destination), Token);
        return true;
    }
}

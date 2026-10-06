# Client update host

Open `/releases.html` to publish APK/EXE packages and browse public downloads.
Upload authentication uses `Authorization: Bearer dev` in Development. In other
environments set `ClientUpdates__UploadToken` (set it to `dev` to use that token).
An unset token disables uploads outside Development. Use HTTPS for remote access.
For the Compose stack, set `CLIENT_UPDATES_UPLOAD_TOKEN` as a secret environment
override in Dockhand. Compose maps it to `ClientUpdates__UploadToken` in the host.

Packages persist under `<Assister:DataPath>/client-releases`, including the existing
Docker `/data` volume. Run one replica. Uploads are streamed, limited to 512 MiB,
hashed and published atomically. Versions are immutable; publish a new version
instead of replacing a file. File extensions are restricted to APK and EXE;
the host stores uploaded bytes without verifying package signatures or format.

App IDs and platforms are lowercase letters, digits and hyphens, up to 64
characters. Choose separate platforms such as `android-arm64` and `windows-x64`.
Versions use `major.minor[.build[.revision]]`, compared numerically and normalized
to four components. Prerelease labels are not supported. There are no automatic
downgrades. An older upload does not become latest when a newer version exists.

## HTTP API

- `GET /api/updates`: public release archive, newest version first per app/platform.
- `PUT /api/updates/{appId}/{platform}/{version}?name=client.apk&notes=...`:
  authenticated upload with the package as the raw request body. Returns 201 with
  metadata, 401 for invalid authentication, 409 for an existing version,
  400 for invalid input, or 413 above the size limit.
- `GET /api/updates/{appId}/{platform}/check?currentVersion=1.0.0`:
  public check; returns `{ "updateAvailable": false, "release": null }` when
  there is no newer release. Otherwise `release` contains version, filename,
  size, SHA-256, notes, publication time and a host-relative download URL.
- `GET /api/updates/{appId}/{platform}/{version}/download`: public package download
  with range support, attachment filename and a SHA-256 ETag.

PowerShell upload:

```powershell
$headers = @{ Authorization = 'Bearer dev' }
Invoke-RestMethod -Method Put -Uri 'https://your-host/api/updates/assister/windows-x64/1.2.0?name=Assister.exe' -Headers $headers -InFile './Assister.exe' -ContentType 'application/octet-stream'
```

## C# client integration

`Assister.Client.ClientUpdateClient` checks, streams downloads to a temporary file,
verifies size and SHA-256, then atomically replaces the requested destination.
Failed downloads leave the existing destination intact. The parent directory
must exist. Use a dedicated HTTP client with an HTTPS base address and no upload
credentials. Update checks never require the developer token.

```csharp
using var http = new HttpClient { BaseAddress = new Uri("https://your-host/") };
var updates = new Assister.Client.ClientUpdateClient(http);
bool installerStarted = await updates.UpdateAsync(
    "assister", "windows-x64", "1.0.0",
    Path.Combine(Path.GetTempPath(), "Assister-update.exe"),
    (package, cancellation) =>
    {
        cancellation.ThrowIfCancellationRequested();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(package)
        {
            UseShellExecute = true
        });
        return Task.CompletedTask;
    });
```

The callback implements the real app's installer flow. The EXE must itself be an
installer to use the example above. `UpdateAsync` returning true means the
callback completed, not that installation succeeded. Android clients must provide
their APK installation flow and handle OS permissions and user confirmation.
Installable updates need compatible app identity and signing. This repository
contains no native Windows or Android app to wire those flows into; the shared
library supplies the check/download/verified-install handoff. SHA-256 verifies
the download against host metadata; it does not replace platform signing or HTTPS.

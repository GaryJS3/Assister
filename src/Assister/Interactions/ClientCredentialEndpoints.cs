using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration.Memory;

namespace Assister.Interactions;

public sealed record ManagedCredential(string Owner, string Hash, bool Revoked = false);
public sealed record CredentialInput(string ClientId, string Owner);
public sealed record CredentialOwner(string Owner);

// Persistent overrides win over environment credentials, including explicit revocations.
// Only SHA-256 hashes of high-entropy generated tokens are persisted.
public sealed class ClientCredentialStore
{
    private readonly ConfigurationManager Configuration;
    private readonly IConfigurationProvider Provider;
    private readonly string FilePath;
    private readonly object Gate = new();
    private Dictionary<string, ManagedCredential> Managed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ClientCredentialStore(ConfigurationManager Configuration)
    {
        this.Configuration = Configuration;
        var DirectoryPath = Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(DirectoryPath);
        FilePath = Path.Combine(DirectoryPath, "client-credentials.json");
        Managed = File.Exists(FilePath)
            ? JsonSerializer.Deserialize<Dictionary<string, ManagedCredential>>(File.ReadAllText(FilePath), Json)!
            : new(StringComparer.OrdinalIgnoreCase);
        Managed = new(Managed, StringComparer.OrdinalIgnoreCase);
        var Source = new MemoryConfigurationSource();
        ((IConfigurationBuilder)Configuration).Add(Source);
        Provider = ((IConfigurationRoot)Configuration).Providers.Last();
        foreach (var (Id, Value) in Managed) Apply(Id, Value);
    }

    private void Apply(string Id, ManagedCredential Value)
    {
        Provider.Set($"RichClients:Clients:{Id}:Owner", Value.Owner);
        Provider.Set($"RichClients:Clients:{Id}:Token", Value.Revoked ? "" : Value.Hash);
        Provider.Set($"RichClients:Clients:{Id}:TokenIsHash", "true");
    }

    public object[] List()
    {
        lock (Gate) return Configuration.GetSection("RichClients:Clients").GetChildren().OrderBy(Client => Client.Key).Select(Client => (object)new
        {
            ClientId = Client.Key, Owner = Client["Owner"] ?? Client.Key,
            Revoked = string.IsNullOrWhiteSpace(Client["Token"]), Managed = Managed.ContainsKey(Client.Key)
        }).ToArray();
    }

    private static void Validate(string Id, string Owner)
    {
        if (!Regex.IsMatch(Id, @"^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$") || string.IsNullOrWhiteSpace(Owner)
            || Owner.Length > 128 || Owner.Any(char.IsControl))
            throw new ArgumentException("Use a client ID of 1–64 letters, digits, underscores or hyphens and an owner of 1–128 characters.");
    }

    public bool Exists(string Id) => Configuration.GetSection("RichClients:Clients").GetChildren()
        .Any(Client => string.Equals(Client.Key, Id, StringComparison.OrdinalIgnoreCase));

    public string Issue(string Id, string Owner, bool Create)
    {
        Validate(Id, Owner);
        lock (Gate)
        {
            if (Create && Exists(Id)) throw new InvalidOperationException("Client ID already exists. Rotate its token instead.");
            if (!Create && !Exists(Id)) throw new KeyNotFoundException();
            var Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Save(Id, new(Owner.Trim(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Token)))));
            return Token;
        }
    }

    public void Reassign(string Id, string Owner)
    {
        Validate(Id, Owner);
        lock (Gate)
        {
            if (!Exists(Id)) throw new KeyNotFoundException();
            var Current = Configuration[$"RichClients:Clients:{Id}:Token"] ?? "";
            var Hash = Configuration.GetValue<bool>($"RichClients:Clients:{Id}:TokenIsHash") ? Current
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Current)));
            Save(Id, new(Owner.Trim(), Hash, Current.Length == 0));
        }
    }

    public void Revoke(string Id)
    {
        lock (Gate)
        {
            if (!Exists(Id)) throw new KeyNotFoundException();
            Save(Id, new(Configuration[$"RichClients:Clients:{Id}:Owner"] ?? Id, "", true));
        }
    }

    private void Save(string Id, ManagedCredential Value)
    {
        var Next = new Dictionary<string, ManagedCredential>(Managed, StringComparer.OrdinalIgnoreCase) { [Id] = Value };
        var Temporary = FilePath + ".tmp";
        File.WriteAllText(Temporary, JsonSerializer.Serialize(Next, Json));
        File.Move(Temporary, FilePath, true);
        Managed = Next;
        Apply(Id, Value);
    }
}

public static class ClientCredentialEndpoints
{
    public static void MapClientCredentials(this WebApplication App, ConfigurationManager Configuration)
    {
        var Store = new ClientCredentialStore(Configuration);
        var AdminPath = Path.Combine(Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data"), "client-management-admin-token.txt");
        var Group = App.MapGroup("/api/devices/credentials");
        Group.AddEndpointFilter(async (Context, Next) =>
        {
            var Http = Context.HttpContext;
            Http.Response.Headers.CacheControl = "no-store";
            var Origin = Http.Request.Headers.Origin.ToString();
            if (Origin.Length > 0 && (!Uri.TryCreate(Origin, UriKind.Absolute, out var OriginUri)
                || OriginUri.Authority != Http.Request.Host.Value || OriginUri.Scheme != Http.Request.Scheme)) return Results.StatusCode(403);
            var Expected = Configuration["ClientManagement:AdminToken"]
                ?? (File.Exists(AdminPath) ? File.ReadAllText(AdminPath).Trim() : null);
            var Header = Http.Request.Headers.Authorization.ToString();
            var Token = Header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? Header[7..] : "";
            if (string.IsNullOrEmpty(Expected) || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(Expected)), SHA256.HashData(Encoding.UTF8.GetBytes(Token)))) return Results.Unauthorized();
            try { return await Next(Context); }
            catch (ArgumentException Error) { return Results.BadRequest(new { Error = Error.Message }); }
            catch (InvalidOperationException Error) { return Results.Conflict(new { Error = Error.Message }); }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        Group.MapGet("/", () => Store.List());
        Group.MapPost("/", (CredentialInput Request) => Results.Ok(new
            { Request.ClientId, Token = Store.Issue(Request.ClientId, Request.Owner, true) }));
        Group.MapPost("/{id}/rotate", (string Id, ClientSignals Signals) =>
        {
            if (!Store.Exists(Id)) return Results.NotFound();
            var Token = Store.Issue(Id, Configuration[$"RichClients:Clients:{Id}:Owner"] ?? Id, false);
            Signals.Disconnect(Id);
            return Results.Ok(new { ClientId = Id, Token });
        });
        Group.MapPut("/{id}/owner", (string Id, CredentialOwner Request, ClientSignals Signals) =>
        {
            Store.Reassign(Id, Request.Owner);
            Signals.Disconnect(Id);
            return Results.NoContent();
        });
        Group.MapDelete("/{id}", (string Id, ClientSignals Signals) =>
        {
            Store.Revoke(Id);
            Signals.Disconnect(Id);
            return Results.NoContent();
        });
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Assister.Contracts;
using Microsoft.Data.Sqlite;

namespace Assister.Interactions;

// Identity comes from authentication. Capability advertisement is availability, never authorization.
public sealed class ClientSignals : IDisposable
{
    private readonly object Gate = new();
    private readonly Dictionary<string, RegisteredClient> Registered = [];
    private readonly Dictionary<Guid, TaskCompletionSource<DeviceResponse>> Waiting = [];
    private readonly SqliteConnection Database;
    private readonly InteractionStore Interactions;
    private readonly ClientAuthentication Authentication;
    private readonly Assister.Diagnostics.DiagnosticSanitizer Sanitizer;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ClientSignals(IConfiguration Configuration, InteractionStore Interactions, ClientAuthentication Authentication)
    {
        this.Interactions = Interactions;
        this.Authentication = Authentication;
        Sanitizer = new(Configuration);
        var Path = System.IO.Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(Path);
        Database = new($"Data Source={System.IO.Path.Combine(Path, "client-signals.db")}");
        Database.Open();
        using var Setup = Database.CreateCommand();
        Setup.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Requests(Id TEXT PRIMARY KEY, InteractionId TEXT NOT NULL, ClientId TEXT NOT NULL, Owner TEXT NOT NULL, Request TEXT NOT NULL, Response TEXT);
            """;
        Setup.ExecuteNonQuery();
        using var Recover = Database.CreateCommand();
        Recover.CommandText = "SELECT Id FROM Requests WHERE Response IS NULL";
        var Ids = new List<Guid>();
        using (var Reader = Recover.ExecuteReader()) while (Reader.Read()) Ids.Add(Guid.Parse(Reader.GetString(0)));
        foreach (var Id in Ids) Complete(Id, new(Id, false, Error: new("server_restarted", "The server restarted; this device request was not repeated.")));
    }
    private SqliteCommand Command(string Sql, params (string, object?)[] Values)
    {
        var Command = Database.CreateCommand(); Command.CommandText = Sql;
        foreach (var (Key, Value) in Values) Command.Parameters.AddWithValue(Key, Value ?? DBNull.Value);
        return Command;
    }
    public bool Register(string ClientId, string Owner, ClientRegistration Request)
    {
        if (Request.ClientType is not { Length: > 0 and <= 32 } || Request.DeviceName is not { Length: > 0 and <= 128 }
            || Request.Capabilities is null || Request.Capabilities.Count > 32
            || Request.Capabilities.Any(Capability => Capability is null || Capability.Length > 64
                || !Regex.IsMatch(Capability, @"^[a-z][a-z0-9_]*(?:\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))) return false;
        lock (Gate)
        {
            Registered[ClientId] = new(ClientId, Owner, Request.ClientType, Request.DeviceName, Request.Capabilities.Distinct().ToArray(), DateTimeOffset.UtcNow);
            foreach (var Pending in PendingCore(ClientId).Where(Item => !Request.Capabilities.Contains(Item.Capability)))
                Complete(Pending.RequestId, new(Pending.RequestId, false, Error: new("device_unavailable", "The requested capability was withdrawn.", true)));
            return true;
        }
    }
    public RegisteredClient[] Clients(string Owner)
    {
        lock (Gate) return Registered.Values.Where(Client => Client.Owner == Owner && Authentication.Owner(Client.ClientId) == Owner)
            .Select(Client => Client with { Online = DateTimeOffset.UtcNow - Client.LastSeen < TimeSpan.FromSeconds(90) }).ToArray();
    }
    public bool Heartbeat(string ClientId)
    {
        lock (Gate)
        {
            if (!Registered.TryGetValue(ClientId, out var Client) || Authentication.Owner(ClientId) != Client.Owner) return false;
            Registered[ClientId] = Client with { LastSeen = DateTimeOffset.UtcNow };
            return true;
        }
    }
    public void Disconnect(string ClientId)
    {
        lock (Gate)
        {
            Registered.Remove(ClientId);
            foreach (var Request in PendingCore(ClientId)) Complete(Request.RequestId,
                new(Request.RequestId, false, Error: new("device_unavailable", "The device disconnected.", true)));
        }
    }
    public async Task<DeviceResponse> RequestAsync(Guid InteractionId, string ClientId, string Capability,
        JsonElement Parameters, TimeSpan Timeout, CancellationToken Token)
    {
        Token.ThrowIfCancellationRequested();
        TaskCompletionSource<DeviceResponse> Completion;
        DeviceRequest Request;
        lock (Gate)
        {
            if (!Registered.TryGetValue(ClientId, out var Client) || DateTimeOffset.UtcNow - Client.LastSeen > TimeSpan.FromSeconds(90)
                || !Client.Capabilities.Contains(Capability) || Authentication.Owner(ClientId) != Client.Owner
                || Interactions.Get(InteractionId, Client.Owner) is not { Status: "created" or "transcribing" or "running" or "responding" })
                throw new InvalidOperationException("device_unavailable");
            if (Parameters.ValueKind != JsonValueKind.Object || System.Text.Encoding.UTF8.GetByteCount(Parameters.GetRawText()) > 8192 || Timeout < TimeSpan.FromMilliseconds(100) || Timeout > TimeSpan.FromMinutes(2))
                throw new ArgumentException("Invalid device request parameters or deadline.");
            using var Count = Command("SELECT COUNT(*) FROM Requests WHERE Response IS NULL");
            if ((long)Count.ExecuteScalar()! >= 64) throw new InvalidOperationException("device_queue_full");
            Request = new(Guid.NewGuid(), InteractionId, ClientId, Capability, Parameters.Clone(), DateTimeOffset.UtcNow.Add(Timeout));
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var Insert = Command("INSERT INTO Requests VALUES($id,$interaction,$client,$owner,$request,NULL)", ("$id", Request.RequestId.ToString()), ("$interaction", InteractionId.ToString()),
                ("$client", ClientId), ("$owner", Client.Owner), ("$request", JsonSerializer.Serialize(Request, Json)));
            Insert.ExecuteNonQuery();
            Waiting[Request.RequestId] = Completion;
            Interactions.Append(InteractionId, "device.request", new { requestId = Request.RequestId, targetClientId = ClientId, capability = Capability, deadline = Request.Deadline });
        }
        try { return await Completion.Task.WaitAsync(Timeout, Token); }
        catch (TimeoutException) { Complete(Request.RequestId, new(Request.RequestId, false, Error: new("device_timeout", "The device did not respond before its deadline.", true))); }
        catch (OperationCanceledException) { Complete(Request.RequestId, new(Request.RequestId, false, Error: new("cancelled", "The device request was cancelled."))); }
        return await Completion.Task;
    }
    private DeviceRequest[] PendingCore(string ClientId)
    {
        using var Query = Command("SELECT Request FROM Requests WHERE ClientId=$client AND Owner=$owner AND Response IS NULL", ("$client", ClientId), ("$owner", Authentication.Owner(ClientId)));
        using var Reader = Query.ExecuteReader();
        var Items = new List<DeviceRequest>();
        while (Reader.Read()) Items.Add(JsonSerializer.Deserialize<DeviceRequest>(Reader.GetString(0), Json)!);
        return Items.ToArray();
    }
    public DeviceRequest[] Pending(string ClientId)
    {
        lock (Gate)
        {
            var Items = PendingCore(ClientId);
            foreach (var Item in Items.Where(Item => Item.Deadline <= DateTimeOffset.UtcNow))
                Complete(Item.RequestId, new(Item.RequestId, false, Error: new("device_timeout", "The request deadline expired.", true)));
            return PendingCore(ClientId);
        }
    }
    public bool Respond(string ClientId, DeviceResponse Response)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Request,Response FROM Requests WHERE Id=$id AND ClientId=$client AND Owner=$owner", ("$id", Response.RequestId.ToString()), ("$client", ClientId), ("$owner", Authentication.Owner(ClientId)));
            DeviceRequest Request;
            string? Existing;
            using (var Reader = Query.ExecuteReader())
            {
                if (!Reader.Read()) return false;
                Request = JsonSerializer.Deserialize<DeviceRequest>(Reader.GetString(0), Json)!;
                Existing = Reader.IsDBNull(1) ? null : Reader.GetString(1);
            }
            if (Existing is not null) return Existing == JsonSerializer.Serialize(Response, Json);
            if (Request.Deadline <= DateTimeOffset.UtcNow || Response.Result is { } Result && System.Text.Encoding.UTF8.GetByteCount(Result.GetRawText()) > 8192
                || !Response.Success && Response.Error is null || Response.Success && Response.Error is not null
                || Response.Error is { } Error && (Error.Message is not { Length: <= 1024 } || Error.Code is not { Length: > 0 and <= 64 })) return false;
            Complete(Response.RequestId, Response);
            return true;
        }
    }
    private void Complete(Guid Id, DeviceResponse Response)
    {
        lock (Gate)
        {
            using var Update = Command("UPDATE Requests SET Response=$response WHERE Id=$id AND Response IS NULL", ("$response", JsonSerializer.Serialize(Response, Json)), ("$id", Id.ToString()));
            if (Update.ExecuteNonQuery() == 0) return;
            using var Query = Command("SELECT InteractionId,ClientId FROM Requests WHERE Id=$id", ("$id", Id.ToString()));
            using var Reader = Query.ExecuteReader();
            if (Reader.Read()) Interactions.Append(Guid.Parse(Reader.GetString(0)), "device.response",
                new { requestId = Id, clientId = Reader.GetString(1), success = Response.Success, errorCode = Response.Error is null ? null : Sanitizer.Text(Response.Error.Code, 64) });
            if (Waiting.Remove(Id, out var Completion)) Completion.TrySetResult(Response);
        }
    }
    public DeviceResponse? Outcome(string ClientId, Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Response FROM Requests WHERE Id=$id AND ClientId=$client AND Owner=$owner", ("$id", Id.ToString()), ("$client", ClientId), ("$owner", Authentication.Owner(ClientId)));
            return Query.ExecuteScalar() is string Payload ? JsonSerializer.Deserialize<DeviceResponse>(Payload, Json) : null;
        }
    }
    public void CancelInteraction(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id FROM Requests WHERE InteractionId=$id AND Response IS NULL", ("$id", Id.ToString()));
            var Ids = new List<Guid>();
            using (var Reader = Query.ExecuteReader()) while (Reader.Read()) Ids.Add(Guid.Parse(Reader.GetString(0)));
            foreach (var RequestId in Ids) Complete(RequestId, new(RequestId, false, Error: new("cancelled", "The interaction was cancelled.")));
        }
    }
    public void Dispose()
    {
        lock (Gate)
        {
            foreach (var (Id, Completion) in Waiting) Completion.TrySetResult(new(Id, false, Error: new("server_restarted", "The server is stopping.")));
            Waiting.Clear();
            Database.Dispose();
        }
    }
}

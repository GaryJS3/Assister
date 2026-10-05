using System.Net.WebSockets;
using System.Text.Json;

namespace Assister.Modules.HomeAssistant;

public interface IHomeAssistantConnection : IAsyncDisposable
{
    Task ConnectAsync(Uri Url, CancellationToken Token);
    Task SendAsync(object Message, CancellationToken Token);
    Task<JsonElement> ReceiveAsync(CancellationToken Token);
}

public sealed class HomeAssistantConnection : IHomeAssistantConnection
{
    private readonly ClientWebSocket Socket = new();
    public HomeAssistantConnection()
    {
        Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        Socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
    }
    public Task ConnectAsync(Uri Url, CancellationToken Token) => Socket.ConnectAsync(Url, Token);
    public async Task SendAsync(object Message, CancellationToken Token) => await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Message).AsMemory(), WebSocketMessageType.Text, true, Token);
    public async Task<JsonElement> ReceiveAsync(CancellationToken Token)
    {
        using var Buffer = new MemoryStream();
        var Chunk = new byte[8192];
        ValueWebSocketReceiveResult Result;
        do
        {
            Result = await Socket.ReceiveAsync(Chunk.AsMemory(), Token);
            if (Result.MessageType != WebSocketMessageType.Text) { throw new InvalidDataException("HA connection closed or sent a non-text message."); }
            if (Buffer.Length + Result.Count > 16 * 1024 * 1024) { throw new InvalidDataException("HA message exceeds limit."); }
            Buffer.Write(Chunk, 0, Result.Count);
        } while (!Result.EndOfMessage);
        using var Document = JsonDocument.Parse(Buffer.ToArray());
        return Document.RootElement.Clone();
    }
    public ValueTask DisposeAsync()
    {
        Socket.Abort();
        Socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class HomeAssistantClient(IConfiguration Configuration, HomeAssistantStateCache Cache, Func<IHomeAssistantConnection> ConnectionFactory, ILogger<HomeAssistantClient> Logger, TimeProvider? Clock = null) : BackgroundService
{
    private string ConnectionStatus = "NotConfigured";
    public string Status => Volatile.Read(ref ConnectionStatus);

    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        var Attempt = 0;
        while (!StoppingToken.IsCancellationRequested)
        {
            var Url = Configuration["HomeAssistant:Url"];
            var Secret = Configuration["HomeAssistant:Token"];
            if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Secret))
            {
                Volatile.Write(ref ConnectionStatus, "NotConfigured");
                await Task.Delay(TimeSpan.FromSeconds(5), StoppingToken);
                continue;
            }
            try
            {
                Volatile.Write(ref ConnectionStatus, "Connecting");
                await using var Connection = ConnectionFactory();
                var Endpoint = new UriBuilder(new Uri(new Uri(Url), "api/websocket"));
                Endpoint.Scheme = Endpoint.Scheme == "https" ? "wss" : "ws";
                using var Startup = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
                Startup.CancelAfter(TimeSpan.FromSeconds(30));
                await Connection.ConnectAsync(Endpoint.Uri, Startup.Token);
                await InitializeAsync(Connection, Secret, Startup.Token);
                await RefreshRegistriesAsync(Connection, Startup.Token);
                Cache.SetStale(false);
                Volatile.Write(ref ConnectionStatus, "Connected");
                Attempt = 0;
                var ScheduledRefresh = Task.Delay(TimeSpan.FromMinutes(15), Clock ?? TimeProvider.System, StoppingToken);
                while (!StoppingToken.IsCancellationRequested)
                {
                    PendingReceive ??= Connection.ReceiveAsync(StoppingToken);
                    await Task.WhenAny(PendingReceive, ScheduledRefresh, RebuildRequested.Task);
                    var Rebuild = ScheduledRefresh.IsCompleted || RebuildRequested.Task.IsCompleted;
                    if (!Rebuild) { ApplyMessage(await ReceiveNextAsync(Connection, StoppingToken)); }
                    using var Refresh = CancellationTokenSource.CreateLinkedTokenSource(StoppingToken);
                    Refresh.CancelAfter(TimeSpan.FromSeconds(30));
                    if (Rebuild)
                    {
                        await RebuildCacheAsync(Connection, Refresh.Token);
                        ScheduledRefresh = Task.Delay(TimeSpan.FromMinutes(15), Clock ?? TimeProvider.System, StoppingToken);
                    }
                    await RefreshRegistriesAsync(Connection, Refresh.Token);
                }
            }
            catch (OperationCanceledException) when (StoppingToken.IsCancellationRequested) { break; }
            catch (Exception Error)
            {
                // Never log remote payloads, URLs or exception messages which may contain secrets.
                Logger.LogWarning("Home Assistant disconnected ({FailureType}); reconnecting.", Error.GetType().Name);
            }
            finally
            {
                Volatile.Write(ref ConnectionStatus, "Unavailable");
                PendingReceive = null;
                FailRebuild();
                Cache.SetStale(true);
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(Attempt++, 5)))) + TimeSpan.FromMilliseconds(Random.Shared.Next(250)), StoppingToken);
        }
    }

    public async Task InitializeAsync(IHomeAssistantConnection Connection, string Secret, CancellationToken Token)
    {
        if ((await Connection.ReceiveAsync(Token)).GetProperty("type").GetString() != "auth_required") { throw new InvalidDataException("Unexpected HA greeting."); }
        await Connection.SendAsync(new { type = "auth", access_token = Secret }, Token);
        if ((await Connection.ReceiveAsync(Token)).GetProperty("type").GetString() != "auth_ok") { throw new InvalidDataException("HA authentication rejected."); }
        CommandId = 0;
        RegistryRefreshNeeded = false;
        // Subscribe before the snapshot, then replay queued events so updates during loading are retained.
        await CommandAsync(Connection, Token, "subscribe_events", "state_changed");
        await LoadSnapshotAsync(Connection, Token);
        foreach (var EventType in RegistryEvents)
        {
            await CommandAsync(Connection, Token, "subscribe_events", EventType);
        }
        // Capture edits made during the initial snapshot before subscriptions were active.
        RegistryRefreshNeeded = true;
    }

    private async Task LoadSnapshotAsync(IHomeAssistantConnection Connection, CancellationToken Token)
    {
        var Pending = new List<JsonElement>();
        LoadingEvents = Pending;
        try
        {
            var States = await CommandAsync(Connection, Token, "get_states");
            // Events before the snapshot reply are already represented in that snapshot.
            Pending.Clear();
            var Services = await CommandAsync(Connection, Token, "get_services");
            var Entities = await CommandAsync(Connection, Token, "config/entity_registry/list");
            var Devices = await CommandAsync(Connection, Token, "config/device_registry/list");
            var Areas = await CommandAsync(Connection, Token, "config/area_registry/list");
            Cache.Load(States, Services, Entities, Devices, Areas);
            foreach (var Data in Pending) { Cache.ApplyEvent(Data); }
        }
        finally { LoadingEvents = null; }
    }

    private static readonly string[] RegistryEvents = ["entity_registry_updated", "device_registry_updated", "area_registry_updated"];
    private int CommandId;
    private bool RegistryRefreshNeeded;
    private Task<JsonElement>? PendingReceive;
    private readonly object RebuildGate = new();
    private TaskCompletionSource RebuildRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? RebuildCompleted;

    public async Task<bool> RequestCacheRebuildAsync(CancellationToken Token)
    {
        Task<bool> Completion;
        lock (RebuildGate)
        {
            if (Status != "Connected") { return false; }
            RebuildCompleted ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            Completion = RebuildCompleted.Task;
            RebuildRequested.TrySetResult();
        }
        return await Completion.WaitAsync(Token);
    }

    private void CompleteRebuild(bool Success)
    {
        lock (RebuildGate)
        {
            RebuildCompleted?.TrySetResult(Success);
            RebuildCompleted = null;
            RebuildRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private void FailRebuild() => CompleteRebuild(false);

    private async Task RebuildCacheAsync(IHomeAssistantConnection Connection, CancellationToken Token)
    {
        Cache.SetStale(true);
        await LoadSnapshotAsync(Connection, Token);
        await RefreshRegistriesAsync(Connection, Token);
        Cache.SetStale(false);
        CompleteRebuild(true);
    }

    private async Task<JsonElement> ReceiveNextAsync(IHomeAssistantConnection Connection, CancellationToken Token)
    {
        PendingReceive ??= Connection.ReceiveAsync(Token);
        var Message = await PendingReceive.WaitAsync(Token);
        PendingReceive = null;
        return Message;
    }

    private async Task<JsonElement> CommandAsync(IHomeAssistantConnection Connection, CancellationToken Token, string Type, string? EventType = null)
    {
        var Id = ++CommandId;
        var Message = new Dictionary<string, object> { ["id"] = Id, ["type"] = Type };
        if (EventType is not null) { Message["event_type"] = EventType; }
        await Connection.SendAsync(Message, Token);
        while (true)
        {
            var Reply = await ReceiveNextAsync(Connection, Token);
            if (Reply.GetProperty("type").GetString() == "event") { ApplyMessage(Reply); continue; }
            if (Reply.GetProperty("id").GetInt32() != Id || !Reply.GetProperty("success").GetBoolean()) { throw new InvalidDataException("HA command failed."); }
            return Reply.GetProperty("result").Clone();
        }
    }

    private async Task RefreshRegistriesAsync(IHomeAssistantConnection Connection, CancellationToken Token)
    {
        while (RegistryRefreshNeeded)
        {
            RegistryRefreshNeeded = false;
            var Entities = await CommandAsync(Connection, Token, "config/entity_registry/list");
            var Devices = await CommandAsync(Connection, Token, "config/device_registry/list");
            var Areas = await CommandAsync(Connection, Token, "config/area_registry/list");
            Cache.UpdateRegistries(Entities, Devices, Areas);
        }
    }

    private List<JsonElement>? LoadingEvents;
    private void ApplyMessage(JsonElement Message)
    {
        if (Message.GetProperty("type").GetString() != "event") { return; }
        var Event = Message.GetProperty("event");
        var EventType = Event.GetProperty("event_type").GetString();
        if (RegistryEvents.Contains(EventType))
        {
            RegistryRefreshNeeded = true;
            return;
        }
        if (EventType != "state_changed") { return; }
        var Data = Event.GetProperty("data");
        if (LoadingEvents is not null)
        {
            if (LoadingEvents.Count >= 10000) { throw new InvalidDataException("HA startup event queue exceeds limit."); }
            LoadingEvents.Add(Data.Clone());
        }
        else { Cache.ApplyEvent(Data); }
    }
}


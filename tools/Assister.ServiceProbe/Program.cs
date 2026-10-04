using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Assister.Contracts;
using Assister.Speech.Wyoming;

if (args.Contains("--llm-contract"))
{
    return await MvpProbe.CheckModelContractAsync();
}

if (args.Contains("--mvp-readonly"))
{
    var Index = Array.IndexOf(args, "--url");
    return await MvpProbe.RunAsync(Index >= 0 && Index + 1 < args.Length ? args[Index + 1] : "http://127.0.0.1:8080");
}

if (args.Contains("--direct-intents"))
{
    var Index = Array.IndexOf(args, "--light");
    return await LiveIntentProbe.RunAsync(Index >= 0 && Index + 1 < args.Length ? args[Index + 1] : null);
}

var Results = new List<object>();
var Failures = 0;
var SynthesizedAudio = new List<AudioChunk>();
const string Phrase = "This is a test of the local voice assistant.";

await CheckAsync("Assister health", async Token =>
{
    using var Client = new HttpClient();
    using var Response = await Client.GetAsync("http://127.0.0.1:8080/health", Token);
    Response.EnsureSuccessStatusCode();
    return "HTTP 200";
});

await CheckAsync("Home Assistant authenticated REST", async Token =>
{
    using var Client = new HttpClient();
    Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Required("HomeAssistant__Token"));
    using var Response = await Client.GetAsync(new Uri(new Uri(Required("HomeAssistant__Url")), "api/"), Token);
    Response.EnsureSuccessStatusCode();
    return "Authenticated HTTP 200";
});

await CheckAsync("Home Assistant WebSocket and registries", async Token =>
{
    var Url = new UriBuilder(new Uri(new Uri(Required("HomeAssistant__Url")), "api/websocket"));
    Url.Scheme = Url.Scheme == "https" ? "wss" : "ws";
    using var Socket = new ClientWebSocket();
    await Socket.ConnectAsync(Url.Uri, Token);
    var Greeting = await ReceiveAsync(Socket, Token);
    if (Greeting.GetProperty("type").GetString() != "auth_required")
    {
        throw new InvalidDataException("Unexpected HA greeting.");
    }
    await SendAsync(Socket, new { type = "auth", access_token = Required("HomeAssistant__Token") }, Token);
    var Auth = await ReceiveAsync(Socket, Token);
    if (Auth.GetProperty("type").GetString() != "auth_ok")
    {
        throw new InvalidDataException("HA authentication rejected.");
    }
    var Counts = new Dictionary<string, int>();
    var Id = 0;
    foreach (var Command in new[] { "get_states", "get_services", "config/entity_registry/list", "config/device_registry/list", "config/area_registry/list" })
    {
        await SendAsync(Socket, new { id = ++Id, type = Command }, Token);
        var Reply = await ReceiveAsync(Socket, Token);
        if (!Reply.GetProperty("success").GetBoolean())
        {
            throw new InvalidDataException($"HA command rejected: {Command}");
        }
        var Data = Reply.GetProperty("result");
        Counts[Command] = Data.ValueKind == JsonValueKind.Array ? Data.GetArrayLength() : Data.EnumerateObject().Count();
    }
    var SubscriptionId = ++Id;
    await SendAsync(Socket, new { id = SubscriptionId, type = "subscribe_events", event_type = "state_changed" }, Token);
    var Subscription = await ReceiveAsync(Socket, Token);
    if (!Subscription.GetProperty("success").GetBoolean())
    {
        throw new InvalidDataException("HA state subscription rejected.");
    }
    // Closing the connection removes its subscription. Do not alter real states to trigger events.
    Socket.Abort();
    return JsonSerializer.Serialize(new { Authentication = "OK", Counts, StateChangedSubscription = "Accepted; no state mutation performed" });
});

await CheckAsync("Wyoming STT capability", async Token =>
{
    await using var Connection = await WyomingConnection.ConnectAsync(Endpoint("SpeechToText", 10300), Token);
    var Info = await Connection.DescribeAsync(Token);
    if (!Info.Supports("asr"))
    {
        throw new InvalidDataException("No ASR capability advertised.");
    }
    return "ASR capability advertised";
});

await CheckAsync("Wyoming TTS synthesis", async Token =>
{
    var Provider = new WyomingTextToSpeechProvider(Endpoint("TextToSpeech", 10200));
    await foreach (var Chunk in Provider.SynthesizeAsync(Phrase, new(Optional("TextToSpeech__Voice")), Token))
    {
        SynthesizedAudio.Add(Chunk);
        if (SynthesizedAudio.Sum(Item => Item.Pcm.Length) > 8_000_000)
        {
            throw new InvalidDataException("Speech test audio exceeded limit.");
        }
    }
    if (SynthesizedAudio.Count == 0)
    {
        throw new InvalidDataException("TTS returned no audio.");
    }
    var First = SynthesizedAudio[0];
    return JsonSerializer.Serialize(new { Bytes = SynthesizedAudio.Sum(Item => Item.Pcm.Length), First.SampleRate, First.SampleWidth, First.Channels });
});

await CheckAsync("TTS to STT round trip", async Token =>
{
    if (SynthesizedAudio.Count == 0)
    {
        throw new InvalidDataException("No TTS audio available for STT test.");
    }
    var Provider = new WyomingSpeechToTextProvider(Endpoint("SpeechToText", 10300));
    var Transcript = await Provider.TranscribeAsync(AudioAsync(SynthesizedAudio), new(Optional("SpeechToText__Language") ?? "en"), Token);
    var Normalized = Transcript.Text.ToLowerInvariant();
    if (!Normalized.Contains("test") || !Normalized.Contains("assistant"))
    {
        throw new InvalidDataException("STT transcript did not recognize the test phrase.");
    }
    return Transcript.Text;
});

await CheckAsync("LLM models and completion", async Token =>
{
    using var Client = new HttpClient();
    var Key = Optional("LanguageModel__ApiKey");
    if (Key is not null)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
    }
    var Base = Required("LanguageModel__BaseUrl").TrimEnd('/');
    if (!Base.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
    {
        Base += "/v1";
    }
    using var ModelsResponse = await Client.GetAsync(Base + "/models", Token);
    ModelsResponse.EnsureSuccessStatusCode();
    var Models = JsonDocument.Parse(await ModelsResponse.Content.ReadAsStringAsync(Token));
    var Available = Models.RootElement.GetProperty("data").EnumerateArray().Select(Item => Item.GetProperty("id").GetString()).ToArray();
    var Model = Optional("LanguageModel__Model") ?? Available.FirstOrDefault() ?? throw new InvalidDataException("No LLM models available.");
    // Some local compatible servers require Content-Length rather than chunked requests.
    using var Content = new StringContent(JsonSerializer.Serialize(new
    {
        model = Model,
        temperature = 0,
        max_tokens = 500,
        stream = false,
        messages = new[] { new { role = "user", content = "Reply with the word OK only." } }
    }), Encoding.UTF8, "application/json");
    using var Completion = await Client.PostAsync(Base + "/chat/completions", Content, Token);
    Completion.EnsureSuccessStatusCode();
    var Reply = JsonDocument.Parse(await Completion.Content.ReadAsStringAsync(Token));
    var Text = Reply.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    if (string.IsNullOrWhiteSpace(Text))
    {
        throw new InvalidDataException("LLM returned empty completion.");
    }
    if (Reply.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString() == "length")
    {
        throw new InvalidDataException("LLM test exhausted its token limit before completion.");
    }
    return JsonSerializer.Serialize(new { AvailableModels = Available, SelectedModel = Model, Reply = Text[..Math.Min(Text.Length, 100)] });
});

await CheckAsync("EchoMuse controller web and setup API", async Token =>
{
    using var Client = new HttpClient();
    var Base = Required("EchoMuse__ControllerUrl").TrimEnd('/') + "/";
    using var Page = await Client.GetAsync(Base, Token);
    Page.EnsureSuccessStatusCode();
    using var State = await Client.GetAsync(Base + "api/system/setup-state", Token);
    State.EnsureSuccessStatusCode();
    var Setup = JsonDocument.Parse(await State.Content.ReadAsStringAsync(Token));
    return JsonSerializer.Serialize(new { Web = "HTTP 200", NeedsSetup = Setup.RootElement.GetProperty("needs_setup").GetBoolean(), AudioTransport = "Not tested: controller adapter not implemented" });
});

Console.WriteLine(JsonSerializer.Serialize(new { Results, Failures }, new JsonSerializerOptions { WriteIndented = true }));
return Failures == 0 ? 0 : 1;

async Task CheckAsync(string Name, Func<CancellationToken, Task<string>> Check)
{
    if (args.Contains("--ha-only") && !Name.StartsWith("Home Assistant") && !Name.StartsWith("Assister"))
    {
        return;
    }
    if (args.Contains("--llm-only") && !Name.StartsWith("LLM"))
    {
        return;
    }
    if (args.Contains("--speech-only") && !Name.StartsWith("Wyoming") && !Name.StartsWith("TTS to STT"))
    {
        return;
    }
    using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var Watch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        var Detail = await Check(Timeout.Token);
        Results.Add(new { Name, Status = "Passed", Detail, DurationMs = Watch.ElapsedMilliseconds });
    }
    catch (Exception Error)
    {
        Failures++;
        // Do not print arbitrary responses or exception messages that may contain credentials.
        var Detail = Error is HttpRequestException Http && Http.StatusCode is not null
            ? $"HTTP {(int)Http.StatusCode}" : Error is OperationCanceledException ? "Timed out" : Error.GetType().Name;
        Results.Add(new { Name, Status = "Failed", Detail, DurationMs = Watch.ElapsedMilliseconds });
    }
}

static string? Optional(string Name)
{
    var Value = Environment.GetEnvironmentVariable(Name);
    return string.IsNullOrWhiteSpace(Value) ? null : Value;
}

static string Required(string Name) => Optional(Name) ?? throw new InvalidOperationException($"Missing configuration: {Name}");

static WyomingEndpoint Endpoint(string Prefix, int DefaultPort)
{
    return new(Required(Prefix + "__Host"), int.Parse(Optional(Prefix + "__Port") ?? DefaultPort.ToString()), 40);
}

static async IAsyncEnumerable<AudioChunk> AudioAsync(IEnumerable<AudioChunk> Audio)
{
    foreach (var Chunk in Audio)
    {
        yield return Chunk;
        await Task.Yield();
    }
}

static async Task SendAsync(ClientWebSocket Socket, object Value, CancellationToken Token)
{
    await Socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(Value).AsMemory(), WebSocketMessageType.Text, true, Token);
}

static async Task<JsonElement> ReceiveAsync(ClientWebSocket Socket, CancellationToken Token)
{
    using var Data = new MemoryStream();
    var Buffer = new byte[8192];
    ValueWebSocketReceiveResult Part;
    do
    {
        Part = await Socket.ReceiveAsync(Buffer.AsMemory(), Token);
        if (Part.MessageType != WebSocketMessageType.Text)
        {
            throw new InvalidDataException("Unexpected WebSocket message.");
        }
        await Data.WriteAsync(Buffer.AsMemory(0, Part.Count), Token);
        if (Data.Length > 16_000_000)
        {
            throw new InvalidDataException("HA response exceeded probe limit.");
        }
    } while (!Part.EndOfMessage);
    return JsonDocument.Parse(Data.ToArray()).RootElement.Clone();
}

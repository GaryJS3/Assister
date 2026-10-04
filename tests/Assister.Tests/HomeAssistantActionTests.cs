using System.Net;
using System.Text.Json;
using Assister.Modules.HomeAssistant;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assister.Tests;

public sealed class HomeAssistantActionTests
{
    [Theory]
    [InlineData(HomeAssistantAction.TurnOn, null, "turn_on")]
    [InlineData(HomeAssistantAction.TurnOff, null, "turn_off")]
    [InlineData(HomeAssistantAction.SetBrightness, 50, "turn_on")]
    [InlineData(HomeAssistantAction.SetBrightness, 0, "turn_off")]
    public async Task ControlsUseFixedServiceRoutesValidatedTargetsAndFixedLengthJson(HomeAssistantAction Action, int? Percent, string Service)
    {
        var Cache = new HomeAssistantStateCache();
        var Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HomeAssistant:Url"] = "https://home.example/",
            ["HomeAssistant:Token"] = "test-token"
        }).Build();
        using var Connection = new HomeAssistantClient(Configuration, Cache, () => new FakeConnection(), NullLogger<HomeAssistantClient>.Instance);
        await Connection.StartAsync(CancellationToken.None);
        using var Deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (Connection.Status != "Connected")
            {
                await Task.Delay(10, Deadline.Token);
            }
            var Transport = new RecordingHandler();
            using var Http = new HttpClient(Transport);
            var Client = new HomeAssistantActionClient(Http, Configuration, Connection, Cache);
            var Control = new HomeAssistantControl(Action, ["light.desk"], Percent);
            await Client.ControlAsync(Control, Deadline.Token);
            Assert.Equal($"https://home.example/api/services/light/{Service}", Transport.Url!.AbsoluteUri);
            Assert.Equal("Bearer test-token", Transport.Authorization);
            Assert.NotNull(Transport.ContentLength);
            Assert.Equal("light.desk", Transport.Body.GetProperty("entity_id")[0].GetString());
            Assert.Equal(Action == HomeAssistantAction.SetBrightness && Percent > 0, Transport.Body.TryGetProperty("brightness_pct", out _));

            Cache.SetStale(true);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client.ControlAsync(Control, Deadline.Token));
            Assert.Equal(1, Transport.Calls);
            Cache.SetStale(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client.ControlAsync(new(Action, ["light.missing"], Percent), Deadline.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client.ControlAsync(new(HomeAssistantAction.SetBrightness, ["light.desk"], 101), Deadline.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client.ControlAsync(new((HomeAssistantAction)999, ["light.desk"]), Deadline.Token));
            Assert.Equal(1, Transport.Calls);

            Transport.ResponseStatus = HttpStatusCode.Unauthorized;
            await Assert.ThrowsAsync<InvalidOperationException>(() => Client.ControlAsync(Control, Deadline.Token));
            Assert.Equal(2, Transport.Calls);
        }
        finally { await Connection.StopAsync(CancellationToken.None); }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls
        {
            get; private set;
        }
        public Uri? Url
        {
            get; private set;
        }
        public string? Authorization
        {
            get; private set;
        }
        public long? ContentLength
        {
            get; private set;
        }
        public JsonElement Body
        {
            get; private set;
        }
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage Request, CancellationToken CancellationToken)
        {
            Calls++;
            Url = Request.RequestUri;
            Authorization = Request.Headers.Authorization?.ToString();
            ContentLength = Request.Content!.Headers.ContentLength;
            Body = JsonSerializer.Deserialize<JsonElement>(await Request.Content.ReadAsStringAsync(CancellationToken));
            return new(ResponseStatus);
        }
    }

    private sealed class FakeConnection : IHomeAssistantConnection
    {
        private readonly Queue<JsonElement> Messages = new();
        public FakeConnection()
        {
            Messages.Enqueue(Json("""{"type":"auth_required"}"""));
            Messages.Enqueue(Json("""{"type":"auth_ok"}"""));
        }

        public Task ConnectAsync(Uri Url, CancellationToken Token) => Task.CompletedTask;
        public Task SendAsync(object Message, CancellationToken Token)
        {
            var Data = JsonSerializer.SerializeToElement(Message);
            if (Data.GetProperty("type").GetString() == "auth")
            {
                return Task.CompletedTask;
            }
            var Result = Data.GetProperty("type").GetString() switch
            {
                "get_states" => """[{"entity_id":"light.desk","state":"on","attributes":{"supported_color_modes":["brightness"]}}]""",
                "get_services" => """{"light":{"turn_on":{},"turn_off":{}}}""",
                "subscribe_events" => "null",
                _ => "[]"
            };
            Messages.Enqueue(Json($$"""{"type":"result","id":{{Data.GetProperty("id").GetInt32()}},"success":true,"result":{{Result}}}"""));
            return Task.CompletedTask;
        }

        public async Task<JsonElement> ReceiveAsync(CancellationToken Token)
        {
            if (Messages.Count == 0)
            {
                await Task.Delay(Timeout.Infinite, Token);
            }
            return Messages.Dequeue();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static JsonElement Json(string Text) => JsonSerializer.Deserialize<JsonElement>(Text);
    }
}

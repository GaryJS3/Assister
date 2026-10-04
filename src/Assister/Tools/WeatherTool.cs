using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Assister.Contracts;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

public sealed class WeatherTool(HomeAssistantStateCache Cache, HttpClient Http, IConfiguration Configuration) : IAssisterTool
{
    public bool StateChanging => false;
    public LlmTool Definition => new(new("weather_forecast", "Retrieve a real forecast from the configured Home Assistant weather provider. Daily cannot answer night-specific questions; request twice_daily or hourly for those. If unsupported, explain the limitation.",
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"type":{"type":"string","enum":["daily","twice_daily","hourly"]}},"required":["type"],"additionalProperties":false}""")));
    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
    {
        var Snapshot = Cache.Snapshot();
        if (Snapshot.IsStale) { throw new InvalidOperationException(); }
        var Candidates = Snapshot.Entities.Where(Entity => Entity.Domain == "weather" && !Entity.IsUnavailable).ToArray();
        var Configured = Configuration["Weather:EntityId"]?.Trim();
        if (string.IsNullOrWhiteSpace(Configured)) { Configured = null; }
        var Entity = Configured is not null ? Candidates.SingleOrDefault(Item => Item.EntityId == Configured)
            : Candidates.Length == 1 ? Candidates[0] : null;
        if (Entity is null) { return "{\"error\":\"No single weather source configured. Set Weather:EntityId to select an available HA weather entity.\"}"; }
        using var Request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(Configuration["HomeAssistant:Url"]!), "api/services/weather/get_forecasts?return_response"));
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["HomeAssistant:Token"]);
        Request.Content = new StringContent(JsonSerializer.Serialize(new { entity_id = Entity.EntityId, type = Arguments.GetProperty("type").GetString() }), Encoding.UTF8, "application/json");
        using var Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        Response.EnsureSuccessStatusCode();
        await using var Stream = await Response.Content.ReadAsStreamAsync(CancellationToken);
        using var Buffer = new MemoryStream();
        var Block = new byte[4096];
        int Count;
        while ((Count = await Stream.ReadAsync(Block, CancellationToken)) > 0)
        {
            if (Buffer.Length + Count > 128 * 1024) { throw new InvalidDataException(); }
            Buffer.Write(Block, 0, Count);
        }
        using var Document = JsonDocument.Parse(Buffer.ToArray());
        var Forecast = Document.RootElement.GetProperty("service_response").GetProperty(Entity.EntityId).GetProperty("forecast");
        var Fields = new[] { "datetime", "condition", "temperature", "templow", "is_daytime", "precipitation_probability" };
        var Points = Forecast.EnumerateArray().Take(48).Select(Point => Point.EnumerateObject().Where(Field => Fields.Contains(Field.Name))
            .ToDictionary(Field => Field.Name, Field => Field.Value.Clone())).ToArray();
        var Attributes = Entity.State.GetProperty("attributes");
        return JsonSerializer.Serialize(new { source = Entity.Name, entity_id = Entity.EntityId, type = Arguments.GetProperty("type").GetString(),
            temperature_unit = Attributes.TryGetProperty("temperature_unit", out var Unit) ? Unit.GetString() : null,
            retrieved_at = DateTimeOffset.UtcNow, forecast = Points });
    }
}

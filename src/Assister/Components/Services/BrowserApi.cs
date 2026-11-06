using System.Text.Json;
using Microsoft.JSInterop;

namespace Assister.Components.Services;

/// <summary>Fetches from the user's browser so existing API cookies and browser-side authorization work unchanged.</summary>
public sealed class BrowserApi(IJSRuntime Javascript)
{
    public BrowserClock Clock { get; } = new();
    private Task? clockInitialization;
    public async Task InitializeClock()
    {
        var initialization = clockInitialization ??= InitializeClockCore();
        try { await initialization; }
        catch
        {
            if (ReferenceEquals(clockInitialization, initialization)) clockInitialization = null;
            throw;
        }
    }
    private async Task InitializeClockCore()
    {
        var zone = await Javascript.InvokeAsync<string>("assisterApi.timeZone");
        var locale = await Javascript.InvokeAsync<string>("assisterApi.locale");
        Clock.Configure(zone, locale);
    }
    public string Date(JsonElement value, string name) => Clock.Format(value.Text(name));
    public ValueTask<JsonElement> Get(string path) => Request("GET", path);
    public ValueTask<JsonElement> Post(string path, object? body = null, string? token = null) => Request("POST", path, body, token);
    public ValueTask<JsonElement> Put(string path, object? body = null, string? token = null) => Request("PUT", path, body, token);
    public ValueTask<JsonElement> Patch(string path, object? body = null, string? ifMatch = null) => Request("PATCH", path, body, ifMatch: ifMatch);
    public ValueTask<JsonElement> Delete(string path, string? token = null) => Request("DELETE", path, null, token);
    public async ValueTask<JsonElement> Request(string method, string path, object? body = null, string? token = null, string? ifMatch = null)
    {
        var result = await Read("assisterApi.requestStream", method, path, body, token, ifMatch);
        if (result.Prop("error").ValueKind == JsonValueKind.String)
            throw new BrowserApiException((int)result.Number("status"), result.Text("error"));
        return result.Prop("data");
    }
    public ValueTask<JsonElement> Upload(string path, string inputId, string token) =>
        Read("assisterApi.uploadStream", path, inputId, token);
    private async ValueTask<JsonElement> Read(string identifier, params object?[] arguments)
    {
        await InitializeClock();
        await using var reference = await Javascript.InvokeAsync<IJSStreamReference>(identifier, arguments);
        await using var stream = await reference.OpenReadStreamAsync(16 * 1024 * 1024);
        using var document = await JsonDocument.ParseAsync(stream);
        return document.RootElement.Clone();
    }
    public ValueTask<string?> Storage(string key) => Javascript.InvokeAsync<string?>("assisterApi.storage", key);
    public ValueTask SetStorage(string key, string? value) => Javascript.InvokeVoidAsync("assisterApi.setStorage", key, value);
    public ValueTask Copy(string value) => Javascript.InvokeVoidAsync("assisterApi.copy", value);
    public ValueTask<bool> Confirm(string message) => Javascript.InvokeAsync<bool>("assisterApi.confirm", message);
}

public sealed class BrowserApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Null-safe access to untyped API payloads. Keeps server-side page markup readable.</summary>
public static class JsonView
{
    public static JsonElement Prop(this JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result) ? result : default;
    public static string Text(this JsonElement value, string name, string fallback = "") => value.Prop(name).String(fallback);
    public static string String(this JsonElement value, string fallback = "") => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? fallback,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.ToString(),
        _ => fallback
    };
    public static bool Flag(this JsonElement value, string name) => value.Prop(name).ValueKind == JsonValueKind.True;
    public static double Number(this JsonElement value, string name) =>
        value.Prop(name).ValueKind == JsonValueKind.Number && value.Prop(name).TryGetDouble(out var n) ? n : 0;
    public static IEnumerable<JsonElement> Items(this JsonElement value) =>
        value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();
    public static string Pretty(this JsonElement value) =>
        value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "" :
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });

}

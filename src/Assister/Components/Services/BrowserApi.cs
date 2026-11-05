using System.Text.Json;
using Microsoft.JSInterop;

namespace Assister.Components.Services;

/// <summary>Fetches from the user's browser so existing API cookies and browser-side authorization work unchanged.</summary>
public sealed class BrowserApi(IJSRuntime Javascript)
{
    public ValueTask<JsonElement> Get(string path) => Request("GET", path);
    public ValueTask<JsonElement> Post(string path, object? body = null, string? token = null) => Request("POST", path, body, token);
    public ValueTask<JsonElement> Put(string path, object? body = null, string? token = null) => Request("PUT", path, body, token);
    public ValueTask<JsonElement> Patch(string path, object? body = null, string? ifMatch = null) => Javascript.InvokeAsync<JsonElement>("assisterApi.request", "PATCH", path, body, null, ifMatch);
    public ValueTask<JsonElement> Delete(string path, string? token = null) => Request("DELETE", path, null, token);
    public ValueTask<JsonElement> Request(string method, string path, object? body = null, string? token = null) =>
        Javascript.InvokeAsync<JsonElement>("assisterApi.request", method, path, body, token);
    public ValueTask<JsonElement> Upload(string path, string inputId, string token) =>
        Javascript.InvokeAsync<JsonElement>("assisterApi.upload", path, inputId, token);
    public ValueTask<string?> Storage(string key) => Javascript.InvokeAsync<string?>("assisterApi.storage", key);
    public ValueTask SetStorage(string key, string? value) => Javascript.InvokeVoidAsync("assisterApi.setStorage", key, value);
    public ValueTask Copy(string value) => Javascript.InvokeVoidAsync("assisterApi.copy", value);
    public ValueTask<bool> Confirm(string message) => Javascript.InvokeAsync<bool>("assisterApi.confirm", message);
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
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
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
    public static string Date(this JsonElement value, string name) =>
        DateTimeOffset.TryParse(value.Text(name), out var parsed) ? parsed.ToLocalTime().ToString("g") : "—";
}

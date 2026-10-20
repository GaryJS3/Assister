using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Assister.Llm;

public sealed record LanguageModelSettings(string BaseUrl, string Model, double Temperature = 0.2,
    int MaxTokens = 500, int TimeoutSeconds = 30, bool? EnableThinking = null,
    string? ApiKey = null, bool ClearApiKey = false);

public static class LanguageModelSettingsEndpoints
{
    public static void MapLanguageModelSettings(this WebApplication App, ConfigurationManager Configuration)
    {
        var DirectoryPath = Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(DirectoryPath);
        var FilePath = Path.Combine(DirectoryPath, "language-model-settings.json");
        var Source = new MemoryConfigurationSource();
        if (File.Exists(FilePath))
            Source.InitialData = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(FilePath));
        ((IConfigurationBuilder)Configuration).Add(Source);
        var Provider = ((IConfigurationRoot)Configuration).Providers.Last();
        var Gate = new SemaphoreSlim(1, 1);

        object Snapshot() => new
        {
            BaseUrl = Configuration["LanguageModel:BaseUrl"] ?? "",
            Model = Configuration["LanguageModel:Model"] ?? "",
            Temperature = Configuration.GetValue("LanguageModel:Temperature", 0.2),
            MaxTokens = Configuration.GetValue("LanguageModel:MaxTokens", 500),
            TimeoutSeconds = Configuration.GetValue("LanguageModel:TimeoutSeconds", 30),
            EnableThinking = Configuration.GetValue<bool?>("LanguageModel:EnableThinking"),
            HasApiKey = !string.IsNullOrWhiteSpace(Configuration["LanguageModel:ApiKey"])
        };

        App.MapGet("/api/settings/ai-server", (HttpResponse Response) =>
        {
            Response.Headers.CacheControl = "no-store";
            return Results.Ok(Snapshot());
        });
        App.MapPut("/api/settings/ai-server", async (LanguageModelSettings Settings, HttpRequest Request, CancellationToken Token) =>
        {
            // Browser writes must originate from this management UI.
            if (Request.Headers.Origin.Count > 0 && Request.Headers.Origin.ToString() != $"{Request.Scheme}://{Request.Host}")
                return Results.StatusCode(403);
            if (!Uri.TryCreate(Settings.BaseUrl?.Trim(), UriKind.Absolute, out var Url)
                || Url.Scheme is not ("http" or "https") || Url.UserInfo.Length > 0 || Url.Query.Length > 0 || Url.Fragment.Length > 0
                || string.IsNullOrWhiteSpace(Settings.Model) || Settings.Model.Length > 256
                || !double.IsFinite(Settings.Temperature) || Settings.Temperature is < 0 or > 2
                || Settings.MaxTokens is < 1 or > 4096 || Settings.TimeoutSeconds is < 1 or > 120
                || Settings.ApiKey?.Length > 4096 || Settings.ApiKey?.Any(char.IsControl) == true)
                return Results.BadRequest(new { Error = "Use an HTTP(S) base URL without credentials, query or fragment, a model, temperature 0–2, tokens 1–4096 and timeout 1–120 seconds. API keys cannot contain control characters." });

            await Gate.WaitAsync(Token);
            try
            {
                var Values = new Dictionary<string, string?>
                {
                    ["LanguageModel:BaseUrl"] = Url.AbsoluteUri.TrimEnd('/'),
                    ["LanguageModel:Model"] = Settings.Model.Trim(),
                    ["LanguageModel:Temperature"] = Settings.Temperature.ToString(CultureInfo.InvariantCulture),
                    ["LanguageModel:MaxTokens"] = Settings.MaxTokens.ToString(CultureInfo.InvariantCulture),
                    ["LanguageModel:TimeoutSeconds"] = Settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                    ["LanguageModel:EnableThinking"] = Settings.EnableThinking?.ToString(),
                    ["LanguageModel:ApiKey"] = Settings.ClearApiKey ? "" : string.IsNullOrWhiteSpace(Settings.ApiKey)
                        ? Configuration["LanguageModel:ApiKey"] : Settings.ApiKey
                };
                var TemporaryPath = FilePath + ".tmp";
                await File.WriteAllTextAsync(TemporaryPath, JsonSerializer.Serialize(Values), Token);
                File.Move(TemporaryPath, FilePath, overwrite: true);
                foreach (var Pair in Values) Provider.Set(Pair.Key, Pair.Value);
                return Results.Ok(Snapshot());
            }
            finally { Gate.Release(); }
        });
    }
}

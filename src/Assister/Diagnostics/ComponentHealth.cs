using System.Net.Http.Headers;
using Assister.Speech.Wyoming;

namespace Assister.Diagnostics;

public sealed record ComponentStatus(string Name, string Status, string Detail);
public sealed class ComponentHealth(IConfiguration Configuration, IHttpClientFactory Http) : BackgroundService
{
    private ComponentStatus[] Current =
    [new("Language model", "Checking", "Waiting for first probe"), new("Speech to text", "Checking", "Waiting for first probe"), new("Text to speech", "Checking", "Waiting for first probe")];
    public ComponentStatus[] Snapshot() => Volatile.Read(ref Current);
    protected override async Task ExecuteAsync(CancellationToken StoppingToken)
    {
        while (!StoppingToken.IsCancellationRequested)
        {
            var Checks = await Task.WhenAll(ProbeLlm(StoppingToken), ProbeSpeech("SpeechToText", "Speech to text", "asr", 10300, StoppingToken),
                ProbeSpeech("TextToSpeech", "Text to speech", "tts", 10200, StoppingToken));
            Volatile.Write(ref Current, Checks);
            await Task.Delay(TimeSpan.FromSeconds(15), StoppingToken);
        }
    }
    private async Task<ComponentStatus> ProbeLlm(CancellationToken Token)
    {
        var Url = Configuration["LanguageModel:BaseUrl"];
        var Model = Configuration["LanguageModel:Model"];
        if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(Model))
            return new("Language model", "NotConfigured", "Set LanguageModel:BaseUrl and Model");
        try
        {
            if (!Uri.TryCreate(Url.TrimEnd('/') + "/", UriKind.Absolute, out var Base) || Base.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(Base.UserInfo) || !string.IsNullOrEmpty(Base.Query) || !string.IsNullOrEmpty(Base.Fragment))
                return new("Language model", "Unavailable", "Invalid base URL");
            using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var Client = Http.CreateClient("diagnostics");
            using var Request = new HttpRequestMessage(HttpMethod.Get, new Uri(Base, "models"));
            if (!string.IsNullOrWhiteSpace(Configuration["LanguageModel:ApiKey"]))
                Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["LanguageModel:ApiKey"]);
            using var Response = await Client.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, Timeout.Token);
            return new("Language model", Response.IsSuccessStatusCode ? "Healthy" : "Unavailable",
                $"Models endpoint HTTP {(int)Response.StatusCode}; inference not tested. Request routing not wired.");
        }
        catch (Exception Error) when (Error is HttpRequestException or OperationCanceledException or ArgumentException or InvalidOperationException)
        { return new("Language model", "Unavailable", "Models endpoint probe failed or timed out"); }
    }
    private async Task<ComponentStatus> ProbeSpeech(string Section, string Name, string Capability, int DefaultPort, CancellationToken Token)
    {
        var Host = Configuration[$"{Section}:Host"];
        if (string.IsNullOrWhiteSpace(Host)) return new(Name, "NotConfigured", $"Set {Section}:Host and Port; voice pipeline not wired");
        var Port = int.TryParse(Configuration[$"{Section}:Port"], out var ConfiguredPort) ? ConfiguredPort : DefaultPort;
        try
        {
            using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var Connection = await WyomingConnection.ConnectAsync(new(Host, Port), Timeout.Token);
            var Info = await Connection.DescribeAsync(Timeout.Token);
            return new(Name, Info.Supports(Capability) ? "Healthy" : "Unavailable", "Live Wyoming capability check; voice pipeline not wired");
        }
        catch (Exception Error) when (Error is System.Net.Sockets.SocketException or IOException or OperationCanceledException or ArgumentException or InvalidOperationException)
        { return new(Name, "Unavailable", "Wyoming capability probe failed or timed out"); }
    }
}

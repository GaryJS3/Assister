using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assister.Contracts;

namespace Assister.Llm;

public sealed class OpenAiCompatibleLanguageModel(HttpClient Http, IConfiguration Configuration) : ILanguageModel
{
    private const int MaximumBytes = 262144;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<LlmResponse> CompleteAsync(LlmRequest Request, CancellationToken CancellationToken)
    {
        using var Timeout = CreateTimeout(CancellationToken);
        using var Message = CreateRequest(Request, false);
        using var Response = await Http.SendAsync(Message, HttpCompletionOption.ResponseHeadersRead, Timeout.Token);
        CheckStatus(Response);
        await using var Stream = await Response.Content.ReadAsStreamAsync(Timeout.Token);
        using var Buffer = new MemoryStream();
        var Bytes = new byte[4096];
        int Count;
        while ((Count = await Stream.ReadAsync(Bytes, Timeout.Token)) > 0)
        {
            if (Buffer.Length + Count > MaximumBytes) { throw InvalidResponse(); }
            Buffer.Write(Bytes, 0, Count);
        }
        var Envelope = Parse(Buffer.ToArray());
        var Choice = SingleChoice(Envelope);
        if (Choice.Message is null) { throw InvalidResponse(); }
        return BuildResponse(Choice.Message.Content, Choice.Message.ToolCalls ?? [], Choice.FinishReason);
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest Request,
        [EnumeratorCancellation] CancellationToken CancellationToken)
    {
        using var Timeout = CreateTimeout(CancellationToken);
        using var Message = CreateRequest(Request, true);
        using var Response = await Http.SendAsync(Message, HttpCompletionOption.ResponseHeadersRead, Timeout.Token);
        CheckStatus(Response);
        await using var Stream = await Response.Content.ReadAsStreamAsync(Timeout.Token);
        var Text = new StringBuilder();
        var Calls = new SortedDictionary<int, CallBuilder>();
        string? Finish = null;
        var EventData = new StringBuilder();
        await foreach (var Line in ReadLinesAsync(Stream, Timeout.Token))
        {
            if (Line.Length != 0)
            {
                if (Line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (EventData.Length > 0) { EventData.Append('\n'); }
                    EventData.Append(Line.AsSpan(5).TrimStart());
                }
                continue;
            }
            if (EventData.Length == 0) { continue; }
            var Data = EventData.ToString();
            EventData.Clear();
            if (Data == "[DONE]")
            {
                yield return new(Completed: BuildResponse(Text.Length == 0 ? null : Text.ToString(),
                    Calls.Values.Select(Call => Call.Build()).ToArray(), Finish));
                yield break;
            }
            var Envelope = Parse(Encoding.UTF8.GetBytes(Data));
            // Some servers emit a final usage-only event.
            if (Envelope.Choices is { Length: 0 } && Envelope.Error is null) { continue; }
            var Choice = SingleChoice(Envelope);
            if (Finish is not null) { throw InvalidResponse(); }
            if (Choice.Delta is not { } Delta) { throw InvalidResponse(); }
            if (Delta.Content is { } Content)
            {
                Text.Append(Content);
                yield return new(TextDelta: Content);
            }
            foreach (var Call in Delta.ToolCalls ?? [])
            {
                if (Call.Index is not (>= 0 and < 16)) { throw InvalidResponse(); }
                if (!Calls.TryGetValue(Call.Index.Value, out var Builder))
                {
                    Builder = new();
                    Calls.Add(Call.Index.Value, Builder);
                }
                Builder.Append(Call);
            }
            Finish = Choice.FinishReason;
        }
        throw InvalidResponse(); // EOF without [DONE] must not authorize partially generated tool calls.
    }

    private HttpRequestMessage CreateRequest(LlmRequest Request, bool Stream)
    {
        if (!Uri.TryCreate(Configuration["LanguageModel:BaseUrl"]?.TrimEnd('/') + "/", UriKind.Absolute, out var Base)
            || Base.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(Base.UserInfo)
            || !string.IsNullOrEmpty(Base.Query) || !string.IsNullOrEmpty(Base.Fragment)
            || string.IsNullOrWhiteSpace(Configuration["LanguageModel:Model"]))
        {
            throw new InvalidOperationException("Language model is not configured.");
        }
        if (Request.Messages.Count is < 1 or > 64 || Request.Tools?.Count > 16
            || Request.ToolChoice is not ("auto" or "none" or "required")
            || Request.Messages.Any(Message => Message.Role is not ("system" or "user" or "assistant" or "tool")))
        {
            throw new ArgumentException("Invalid language model request.");
        }
        var Payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Model = Configuration["LanguageModel:Model"],
            Temperature = Math.Clamp(Configuration.GetValue("LanguageModel:Temperature", 0.2), 0, 2),
            MaxTokens = Math.Clamp(Configuration.GetValue("LanguageModel:MaxTokens", 500), 1, 4096),
            Request.Messages,
            Tools = Request.Tools is { Count: > 0 } ? Request.Tools : null,
            ToolChoice = Request.Tools is { Count: > 0 } ? Request.ToolChoice : null,
            Stream
        }, Json);
        if (Payload.Length > MaximumBytes) { throw new ArgumentException("Language model request is too large."); }
        var Message = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, "chat/completions"));
        Message.Content = new ByteArrayContent(Payload); // Explicit content length for the local server.
        Message.Content.Headers.ContentType = new("application/json");
        if (!string.IsNullOrWhiteSpace(Configuration["LanguageModel:ApiKey"]))
        {
            Message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["LanguageModel:ApiKey"]);
        }
        return Message;
    }

    private CancellationTokenSource CreateTimeout(CancellationToken Token)
    {
        var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(Configuration.GetValue("LanguageModel:TimeoutSeconds", 30), 1, 120)));
        return Timeout;
    }

    private static void CheckStatus(HttpResponseMessage Response)
    {
        if (!Response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Language model returned HTTP {(int)Response.StatusCode}.");
        }
    }

    private static Envelope Parse(byte[] Data)
    {
        try { return JsonSerializer.Deserialize<Envelope>(Data, Json) ?? throw InvalidResponse(); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static Choice SingleChoice(Envelope Envelope)
    {
        if (Envelope.Error is not null || Envelope.Choices is not { Length: 1 } || Envelope.Choices[0].Index != 0)
        {
            throw InvalidResponse();
        }
        return Envelope.Choices[0];
    }

    private static LlmResponse BuildResponse(string? Content, IReadOnlyList<LlmToolCall> Calls, string? Finish)
    {
        if (Finish is not ("stop" or "tool_calls") || Calls.Count > 16
            || Calls.Any(Call => Call.Type != "function" || string.IsNullOrWhiteSpace(Call.Id)
                || Call.Id.Length > 128 || Call.Function is null || string.IsNullOrWhiteSpace(Call.Function.Name)
                || Call.Function.Name.Length > 128 || Call.Function.Arguments is null || Call.Function.Arguments.Length > 16384)
            || Calls.Select(Call => Call.Id).Distinct().Count() != Calls.Count
            || (Finish == "tool_calls") != (Calls.Count > 0)
            || (Calls.Count == 0 && string.IsNullOrWhiteSpace(Content)))
        {
            throw InvalidResponse();
        }
        return new(Content, Calls, Finish);
    }

    private static InvalidOperationException InvalidResponse() => new("Language model returned an invalid or incomplete response.");

    private static async IAsyncEnumerable<string> ReadLinesAsync(Stream Stream,
        [EnumeratorCancellation] CancellationToken Token)
    {
        var Buffer = new byte[4096];
        using var Line = new MemoryStream();
        var Total = 0;
        int Count;
        while ((Count = await Stream.ReadAsync(Buffer, Token)) > 0)
        {
            Total += Count;
            if (Total > MaximumBytes) { throw InvalidResponse(); }
            for (var Index = 0; Index < Count; Index++)
            {
                if (Buffer[Index] == '\n')
                {
                    yield return Encoding.UTF8.GetString(Line.ToArray()).TrimEnd('\r');
                    Line.SetLength(0);
                }
                else
                {
                    Line.WriteByte(Buffer[Index]);
                    if (Line.Length > 32768) { throw InvalidResponse(); }
                }
            }
        }
        if (Line.Length > 0) { yield return Encoding.UTF8.GetString(Line.ToArray()).TrimEnd('\r'); }
    }

    private sealed class CallBuilder
    {
        private readonly StringBuilder Id = new();
        private readonly StringBuilder Name = new();
        private readonly StringBuilder Arguments = new();
        private string? Type;
        public void Append(ToolDelta Delta)
        {
            Id.Append(Delta.Id);
            Name.Append(Delta.Function?.Name);
            Arguments.Append(Delta.Function?.Arguments);
            if (Delta.Type is not null)
            {
                if (Type is not null && Type != Delta.Type) { throw InvalidResponse(); }
                Type = Delta.Type;
            }
        }
        public LlmToolCall Build() => new(Id.ToString(), new(Name.ToString(), Arguments.ToString()), Type ?? "");
    }

    private sealed record Envelope(Choice[]? Choices, JsonElement? Error);
    private sealed record Choice(int Index, LlmMessage? Message, Delta? Delta, string? FinishReason);
    private sealed record Delta(string? Content, ToolDelta[]? ToolCalls);
    private sealed record ToolDelta(int? Index, string? Id, string? Type, FunctionDelta? Function);
    private sealed record FunctionDelta(string? Name, string? Arguments);
}

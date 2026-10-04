using System.Text;
using System.Text.Json;
using Assister.Contracts;
using Assister.Diagnostics;
using Assister.Persistence;

namespace Assister.Tools;

public sealed record ToolExecutionContext(UserRequest Request, HashSet<string> ObservedEntities, Guid TraceId = default);
public interface IAssisterTool
{
    LlmTool Definition { get; }
    bool StateChanging { get; }
    Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken);
}

public sealed class ToolRegistry(IEnumerable<IAssisterTool> Tools)
{
    public IReadOnlyDictionary<string, IAssisterTool> All { get; } = Tools.ToDictionary(Tool => Tool.Definition.Function.Name);
}

public sealed class ToolBroker(ToolRegistry Registry, LocalStore? Store = null)
{
    public async Task<string> ExecuteAsync(LlmToolCall Call, IReadOnlySet<string> Selected, ToolExecutionContext Context,
        CancellationToken CancellationToken)
    {
        using var Trace = RunTracing.Start("Tool broker", "Validate selected tool arguments, apply a timeout and bound the result.");
        Trace.Detail("tool", Call.Function.Name);
        async Task Audit(string Outcome)
        {
            if (Store is not null)
            {
                await Store.ExecuteAsync("INSERT INTO ToolAudit(ConversationId,SatelliteId,Tool,Outcome,CreatedAt,TraceId) VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                    CancellationToken, Context.Request.ConversationId?.ToString(), Context.Request.SatelliteId,
                    Call.Function.Name[..Math.Min(Call.Function.Name.Length, 128)], Outcome, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Context.TraceId.ToString());
                await Store.ExecuteAsync("DELETE FROM ToolAudit WHERE Id <= (SELECT MAX(Id)-5000 FROM ToolAudit)", CancellationToken);
            }
        }
        if (!Selected.Contains(Call.Function.Name) || !Registry.All.TryGetValue(Call.Function.Name, out var Tool))
        {
            Trace.Complete("rejected");
            await Audit("rejected");
            return "{\"error\":\"Tool was not selected for this request.\"}";
        }
        try
        {
            if (Encoding.UTF8.GetByteCount(Call.Function.Arguments) > 8192) { throw new InvalidDataException(); }
            using var Arguments = JsonDocument.Parse(Call.Function.Arguments, new JsonDocumentOptions { MaxDepth = 16 });
            Validate(Arguments.RootElement, Tool.Definition.Function.Parameters);
            await Audit("started");
            using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
            Timeout.CancelAfter(TimeSpan.FromSeconds(12));
            var Result = await Tool.ExecuteAsync(Arguments.RootElement, Context, Timeout.Token);
            if (Encoding.UTF8.GetByteCount(Result) > 16384) { throw new InvalidDataException(); }
            Trace.Complete();
            await Audit("succeeded");
            return Result;
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception Error) when (Error is JsonException or InvalidDataException or InvalidOperationException or HttpRequestException or OperationCanceledException or KeyNotFoundException or FormatException or ArgumentException)
        {
            Trace.Complete("failed");
            await Audit("failed");
            return JsonSerializer.Serialize(new { error = Tool.StateChanging
                ? "Control was rejected or completion could not be confirmed. Do not retry automatically."
                : "Tool request was invalid or the data source is unavailable." });
        }
    }

    // Only the schema vocabulary used by the fixed registry is accepted; nested objects are validated recursively.
    public static void Validate(JsonElement Value, JsonElement Schema)
    {
        var Type = Schema.GetProperty("type").GetString();
        var Valid = Type switch
        {
            "object" => Value.ValueKind == JsonValueKind.Object,
            "string" => Value.ValueKind == JsonValueKind.String,
            "integer" => Value.ValueKind == JsonValueKind.Number && Value.TryGetInt32(out _),
            "array" => Value.ValueKind == JsonValueKind.Array,
            _ => false
        };
        if (!Valid) { throw new InvalidDataException("Invalid argument type."); }
        if (Schema.TryGetProperty("enum", out var Choices) && !Choices.EnumerateArray().Any(Item => Item.GetRawText() == Value.GetRawText()))
        { throw new InvalidDataException("Invalid argument value."); }
        if (Type == "object")
        {
            var Properties = Schema.GetProperty("properties");
            var Names = new HashSet<string>();
            foreach (var Property in Value.EnumerateObject())
            {
                if (!Names.Add(Property.Name) || !Properties.TryGetProperty(Property.Name, out var PropertySchema)) { throw new InvalidDataException(); }
                Validate(Property.Value, PropertySchema);
            }
            if (Schema.TryGetProperty("required", out var Required) && Required.EnumerateArray().Any(Item => !Names.Contains(Item.GetString()!)))
            { throw new InvalidDataException(); }
        }
        if (Type == "string" && (Value.GetString()!.Length is < 1 or > 256)) { throw new InvalidDataException(); }
        if (Type == "integer")
        {
            var Number = Value.GetInt32();
            if (Schema.TryGetProperty("minimum", out var Minimum) && Number < Minimum.GetInt32()
                || Schema.TryGetProperty("maximum", out var Maximum) && Number > Maximum.GetInt32()) { throw new InvalidDataException(); }
        }
        if (Type == "array")
        {
            if (Value.GetArrayLength() is < 1 or > 10) { throw new InvalidDataException(); }
            foreach (var Item in Value.EnumerateArray()) { Validate(Item, Schema.GetProperty("items")); }
        }
    }
}

using System.Text.Json;
using System.Text.RegularExpressions;
using Assister.Contracts;
using Assister.Persistence;

namespace Assister.Tools;

public sealed class MemoryTool(string Name, LocalStore Store) : IAssisterTool
{
    public bool StateChanging => Name != "memory_search";
    public LlmTool Definition => new(new(Name, Name switch
    {
        "memory_store" => "Store a short fact only when the user explicitly asks to remember it.",
        "memory_delete" => "Delete one memory by its ID only when the user explicitly asks to forget it.",
        _ => "Search only memories relevant to the user's question, using up to eight words."
    }, JsonSerializer.Deserialize<JsonElement>(Name switch
    {
        "memory_store" => """{"type":"object","properties":{"content":{"type":"string"},"subject":{"type":"string"}},"required":["content","subject"],"additionalProperties":false}""",
        "memory_delete" => """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false}""",
        _ => """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"],"additionalProperties":false}"""
    })));
    public async Task<string> ExecuteAsync(JsonElement Arguments, ToolExecutionContext Context, CancellationToken CancellationToken)
    {
        if (Name == "memory_store")
        {
            if (!Context.Request.Message.Contains("remember", StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException(); }
            var Id = Guid.NewGuid().ToString();
            await Store.ExecuteAsync("INSERT INTO Memories VALUES($p0,$p1,$p2,$p3,$p4)", CancellationToken,
                Id, Arguments.GetProperty("content").GetString(), Arguments.GetProperty("subject").GetString(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Context.Request.ConversationId?.ToString());
            return JsonSerializer.Serialize(new { id = Id, status = "stored" });
        }
        if (Name == "memory_delete")
        {
            if (!Context.Request.Message.Contains("forget", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(Arguments.GetProperty("id").GetString(), out var Id)) { throw new InvalidOperationException(); }
            var Count = await Store.ExecuteAsync("DELETE FROM Memories WHERE Id=$p0", CancellationToken, Id.ToString());
            return JsonSerializer.Serialize(new { deleted = Count });
        }
        var Words = Regex.Matches(Arguments.GetProperty("query").GetString()!, @"[\p{L}\p{N}]+").Select(Match => Match.Value).Take(8).ToArray();
        if (Words.Length == 0) { throw new InvalidDataException(); }
        var Query = string.Join(" OR ", Words.Select(Word => "\"" + Word + "\""));
        var Rows = await Store.QueryAsync("SELECT Id,Content,Subject FROM MemorySearch WHERE MemorySearch MATCH $p0 ORDER BY rank LIMIT 5", CancellationToken, Query);
        return JsonSerializer.Serialize(Rows.Select(Row => new { id = Row[0], content = Row[1], subject = Row[2] }));
    }
}

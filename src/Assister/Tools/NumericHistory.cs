using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Assister.Modules.HomeAssistant;

namespace Assister.Tools;

internal static class NumericHistory
{
    private sealed record Reading(double Value, DateTimeOffset? Timestamp);

    public static async Task<string> ReadAsync(JsonElement Arguments, HomeAssistantEntity[] Entities,
        DateTimeOffset Start, DateTimeOffset End, HttpClient Http, IConfiguration Configuration, CancellationToken CancellationToken)
    {
        int Number(string Name, int Default) => Arguments.TryGetProperty(Name, out var Value) ? Value.GetInt32() : Default;
        bool Flag(string Name, bool Default) => Arguments.TryGetProperty(Name, out var Value) ? Value.GetBoolean() : Default;
        var Timestamps = Flag("include_timestamps", true);
        var Samples = Flag("include_samples", false);
        var Top = Number("top_count", 0);
        var Bottom = Number("bottom_count", 0);
        var Offset = Number("sample_offset", 0);
        var Limit = Number("sample_limit", 20);
        if ((long)(Top + Bottom + (Samples ? Limit : 0)) * Entities.Length > 50)
        {
            return JsonSerializer.Serialize(new { error = "Request at most 50 detailed readings across all entities; reduce counts or request fewer entities.", code = "history_detail_limit" });
        }
        var MaxBytes = (long)Math.Clamp(Configuration.GetValue("HomeAssistant:HistoryMaxResponseMiB", 16), 1, 64) * 1024 * 1024;
        var Ids = Entities.Select(Entity => Entity.EntityId).ToArray();
        var Path = $"api/history/period/{Uri.EscapeDataString(Start.ToString("O"))}?filter_entity_id={Uri.EscapeDataString(string.Join(',', Ids))}&end_time={Uri.EscapeDataString(End.ToString("O"))}&minimal_response&no_attributes&significant_changes_only";
        using var Request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(Configuration["HomeAssistant:Url"]!), Path));
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Configuration["HomeAssistant:Token"]);
        using var Response = await Http.SendAsync(Request, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        Response.EnsureSuccessStatusCode();
        string TooLarge() => JsonSerializer.Serialize(new { error = "History exceeds the configured response limit. Request smaller time ranges and combine their counts, sums and extrema; do not change timestamp formatting.", code = "history_response_too_large", maximum_bytes = MaxBytes });
        if (Response.Content.Headers.ContentLength > MaxBytes) { return TooLarge(); }
        await using var Stream = await Response.Content.ReadAsStreamAsync(CancellationToken);
        using var Buffer = new MemoryStream();
        var Block = new byte[8192];
        int Count;
        while ((Count = await Stream.ReadAsync(Block, CancellationToken)) > 0)
        {
            if (Buffer.Length + Count > MaxBytes) { return TooLarge(); }
            Buffer.Write(Block, 0, Count);
        }
        Buffer.Position = 0;
        using var Document = await JsonDocument.ParseAsync(Buffer, cancellationToken: CancellationToken);
        var ById = new Dictionary<string, List<Reading>>();
        foreach (var Series in Document.RootElement.EnumerateArray())
        {
            if (Series.GetArrayLength() == 0) { continue; }
            var Id = Series[0].GetProperty("entity_id").GetString()!;
            if (!Ids.Contains(Id)) { continue; }
            if (!ById.TryGetValue(Id, out var Readings)) { ById[Id] = Readings = []; }
            foreach (var Point in Series.EnumerateArray())
            {
                if (!double.TryParse(Point.GetProperty("state").GetString(), CultureInfo.InvariantCulture, out var Value) || !double.IsFinite(Value)) { continue; }
                DateTimeOffset? Timestamp = null;
                if ((Point.TryGetProperty("last_updated", out var Time) || Point.TryGetProperty("last_changed", out Time))
                    && DateTimeOffset.TryParse(Time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var Parsed))
                {
                    // HA can include the state carried into the requested range.
                    if (Parsed > End) { continue; }
                    Timestamp = Parsed < Start ? Start : Parsed;
                }
                Readings.Add(new(Value, Timestamp));
            }
        }
        object Detail(Reading Reading) => Timestamps ? new { value = Reading.Value, timestamp = Reading.Timestamp } : (object)Reading.Value;
        var Results = new List<object>();
        foreach (var Entity in Entities)
        {
            CancellationToken.ThrowIfCancellationRequested();
            var Values = ById.GetValueOrDefault(Entity.EntityId, []).OrderBy(Reading => Reading.Timestamp ?? DateTimeOffset.MaxValue).ToArray();
            var Sorted = Values.OrderBy(Reading => Reading.Value).ThenBy(Reading => Reading.Timestamp ?? DateTimeOffset.MaxValue).ToArray();
            var Descending = Values.OrderByDescending(Reading => Reading.Value).ThenBy(Reading => Reading.Timestamp ?? DateTimeOffset.MaxValue).ToArray();
            var Result = new Dictionary<string, object?>
            {
                ["entity_id"] = Entity.EntityId, ["start"] = Start, ["end"] = End,
                ["numeric_samples"] = Values.Length,
                ["unit"] = Entity.State.GetProperty("attributes").TryGetProperty("unit_of_measurement", out var Unit) ? Unit.GetString() : null,
                ["minimum"] = Sorted.Length > 0 ? Sorted[0].Value : null,
                ["maximum"] = Sorted.Length > 0 ? Descending[0].Value : null,
                ["sum"] = Values.Sum(Reading => Reading.Value),
                ["sample_mean"] = Values.Length > 0 ? Values.Average(Reading => Reading.Value) : null,
                ["median"] = Sorted.Length > 0 ? Sorted[Sorted.Length / 2].Value / 2 + Sorted[(Sorted.Length - 1) / 2].Value / 2 : null,
                ["note"] = "Statistics cover all numeric state-change samples, not time-weighted. Missing/unavailable values are excluded. Extrema ties use earliest timestamps. Carried-in states are timestamped at range start; absent timestamps remain null."
            };
            if (Timestamps)
            {
                Result["minimum_at"] = Sorted.FirstOrDefault()?.Timestamp;
                Result["maximum_at"] = Descending.FirstOrDefault()?.Timestamp;
            }
            if (Top > 0) { Result["top"] = Descending.Take(Top).Select(Detail).ToArray(); }
            if (Bottom > 0) { Result["bottom"] = Sorted.Take(Bottom).Select(Detail).ToArray(); }
            if (Samples)
            {
                Result["samples"] = Values.Skip(Offset).Take(Limit).Select(Detail).ToArray();
                Result["sample_offset"] = Offset;
                Result["next_sample_offset"] = (long)Offset + Limit < Values.Length ? Offset + Limit : (int?)null;
            }
            Results.Add(Result);
        }
        return JsonSerializer.Serialize(Results);
    }
}

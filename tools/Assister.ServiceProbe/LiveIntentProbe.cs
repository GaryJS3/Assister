using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assister.Contracts;

internal static class LiveIntentProbe
{
    public static async Task<int> RunAsync(string? EntityId, bool IncludeLlm = false)
    {
        if (EntityId is null || !Regex.IsMatch(EntityId, @"^light\.[a-z0-9_]+$"))
        {
            Console.WriteLine("Use --direct-intents --light light.authorized_entity. This mode changes the specified light and restores it.");
            return 1;
        }

        var Results = new List<object>();
        using var Home = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("HomeAssistant__Url")!), Timeout = TimeSpan.FromSeconds(15) };
        Home.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("HomeAssistant__Token"));
        using var Assister = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:8080"), Timeout = TimeSpan.FromSeconds(100) };
        var Originals = new List<OriginalLight>();
        var Changed = false;
        var Failures = 0;
        try
        {
            var All = await StatesAsync(Home);
            var Visited = new HashSet<string>();
            void Capture(string Id)
            {
                if (!Visited.Add(Id))
                {
                    return;
                }
                if (Visited.Count > 64 || !Id.StartsWith("light.", StringComparison.Ordinal) || !All.TryGetValue(Id, out var State))
                {
                    throw new InvalidDataException("Cannot capture light group safely.");
                }
                var Attributes = State.GetProperty("attributes");
                if (Attributes.TryGetProperty("entity_id", out var Members) && Members.ValueKind == JsonValueKind.Array && Members.GetArrayLength() > 0)
                {
                    foreach (var Member in Members.EnumerateArray())
                    {
                        Capture(Member.GetString()!);
                    }
                    return;
                }
                var Power = State.GetProperty("state").GetString();
                if (Power is not ("on" or "off"))
                {
                    throw new InvalidDataException("Cannot test an unavailable light.");
                }
                var Brightness = Attributes.TryGetProperty("brightness", out var Value) && Value.ValueKind == JsonValueKind.Number ? Value.GetInt32() : (int?)null;
                Originals.Add(new(Id, Power, Brightness));
            }
            Capture(EntityId);
            // Keep a durable recovery record before touching a device; it contains no credentials.
            var Backup = Path.Combine(Environment.GetEnvironmentVariable("Assister__DataPath") ?? "/data", $"live-light-backup-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json");
            await File.WriteAllTextAsync(Backup, JsonSerializer.Serialize(Originals));
            Results.Add(new
            {
                Check = "Original light state captured",
                Status = "Passed",
                EntityId,
                MemberCount = Originals.Count,
                Backup
            });
            var Status = await GetAsync(Assister, "api/status");
            var InitialEvents = Status.GetProperty("homeAssistantCache").GetProperty("stateEventCount").GetInt64();
            var Steps = new List<(string Message, string Power, int? Brightness, string Handler)>
            {
                ($"turn {EntityId} off", "off", null, "direct-intent"),
                ($"turn {EntityId} on", "on", null, "direct-intent"),
                ($"set {EntityId} to 50 percent", "on", 128, "direct-intent")
            };
            if (IncludeLlm)
            {
                var Name = All[EntityId].GetProperty("attributes").GetProperty("friendly_name").GetString()!;
                Steps.Add((EntityId == "light.living_room_lights" ? "Set living room light to 100." : $"Set {Name} to 100 percent", "on", 255, "direct-intent"));
                Steps.Add(($"Dim {Name} to 40 percent", "on", 102, "language-model"));
                Steps.Add(($"Could you make {Name} fully bright?", "on", 255, "language-model"));
                Steps.Add(($"Could you switch {Name} off?", "off", null, "language-model"));
            }
            foreach (var Step in Steps)
            {
                Changed = true;
                var Result = await MessageAsync(Assister, Step.Message);
                if (Result.Outcome != "succeeded" || Result.HandledBy != Step.Handler
                    || Step.Handler == "direct-intent" && !Result.EntityIds.SequenceEqual(new[] { EntityId }))
                {
                    throw new InvalidDataException("Control did not succeed through the expected request path.");
                }
                if (Step.Handler == "language-model")
                {
                    var Trace = await GetAsync(Assister, $"api/diagnostics/runs/{Result.TraceId}");
                    var Controls = Trace.GetProperty("steps").EnumerateArray().Where(Item => Item.GetProperty("name").GetString() == "ha_control").ToArray();
                    if (Controls.Length != 1 || Controls[0].GetProperty("input").GetProperty("entity_id").GetString() != EntityId
                        || Controls[0].GetProperty("output").GetProperty("status").GetString() != "completed")
                    {
                        throw new InvalidDataException("A matching successful LLM control tool call was not confirmed.");
                    }
                }
                var Confirmed = false;
                for (var Attempt = 0; Attempt < 40; Attempt++)
                {
                    var Actual = await GetAsync(Home, $"api/states/{EntityId}");
                    var Cached = await MessageAsync(Assister, $"what is the state of {EntityId}");
                    var Power = Actual.GetProperty("state").GetString();
                    var BrightnessOk = Step.Brightness is null || (Actual.GetProperty("attributes").TryGetProperty("brightness", out var Brightness)
                        && Brightness.ValueKind == JsonValueKind.Number && Math.Abs(Brightness.GetInt32() - Step.Brightness.Value) <= 1);
                    var MembersOk = true;
                    foreach (var Original in Originals)
                    {
                        var Member = await GetAsync(Home, $"api/states/{Original.EntityId}");
                        MembersOk &= Member.GetProperty("state").GetString() == Step.Power
                            && (Step.Brightness is null || Member.GetProperty("attributes").TryGetProperty("brightness", out var MemberBrightness)
                                && MemberBrightness.TryGetInt32(out var Number) && Math.Abs(Number - Step.Brightness.Value) <= 2);
                    }
                    if (Power == Step.Power && BrightnessOk && MembersOk && Cached.Outcome == "succeeded" && Cached.Response.Contains($" is {Step.Power}.", StringComparison.Ordinal))
                    {
                        Confirmed = true;
                        break;
                    }
                    await Task.Delay(250);
                }
                if (!Confirmed)
                {
                    throw new InvalidDataException("Physical state or cached subscription update was not confirmed.");
                }
                Results.Add(new
                {
                    Check = Step.Message,
                    Status = "Passed",
                    Result.HandledBy,
                    Result.DurationMilliseconds,
                    LiveStateAndCache = "Confirmed"
                });
            }
            Status = await GetAsync(Assister, "api/status");
            var FinalEvents = Status.GetProperty("homeAssistantCache").GetProperty("stateEventCount").GetInt64();
            if (FinalEvents <= InitialEvents)
            {
                throw new InvalidDataException("No state events received.");
            }
            Results.Add(new
            {
                Check = "Persistent state subscription",
                Status = "Passed",
                ReceivedEvents = FinalEvents - InitialEvents
            });
        }
        catch (Exception Error)
        {
            Failures++;
            Results.Add(new
            {
                Check = "Live direct intents",
                Status = "Failed",
                FailureType = Error.GetType().Name
            });
        }
        finally
        {
            if (Changed)
            {
                foreach (var Original in Originals)
                {
                    try
                    {
                        var Data = new Dictionary<string, object> { ["entity_id"] = Original.EntityId };
                        if (Original.Brightness is not null)
                        {
                            Data["brightness"] = Original.Brightness.Value;
                        }
                        if (Original.Power == "on" || Original.Brightness is not null)
                        {
                            await ServiceAsync(Home, "turn_on", Data);
                        }
                        if (Original.Power == "off")
                        {
                            await ServiceAsync(Home, "turn_off", new Dictionary<string, object> { ["entity_id"] = Original.EntityId });
                        }
                        var Restored = false;
                        for (var Attempt = 0; Attempt < 40; Attempt++)
                        {
                            var State = await GetAsync(Home, $"api/states/{Original.EntityId}");
                            var BrightnessMatches = Original.Brightness is null || (State.GetProperty("attributes").TryGetProperty("brightness", out var Brightness)
                                && Brightness.ValueKind == JsonValueKind.Number && Brightness.GetInt32() == Original.Brightness);
                            if (State.GetProperty("state").GetString() == Original.Power && BrightnessMatches)
                            {
                                Restored = true;
                                break;
                            }
                            await Task.Delay(250);
                        }
                        if (!Restored)
                        {
                            throw new InvalidDataException("Original light settings not confirmed.");
                        }
                        Results.Add(new
                        {
                            Check = "Restore original light",
                            Status = "Passed",
                            Original.EntityId,
                            Original.Power,
                            Original.Brightness
                        });
                    }
                    catch (Exception Error)
                    {
                        Failures++;
                        Results.Add(new
                        {
                            Check = "Restore original light",
                            Status = "Failed",
                            Original.EntityId,
                            FailureType = Error.GetType().Name
                        });
                    }
                }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Results,
            Failures
        }, new JsonSerializerOptions { WriteIndented = true }));
        return Failures == 0 ? 0 : 1;
    }

    private static async Task<RequestResult> MessageAsync(HttpClient Client, string Message)
    {
        using var Content = new StringContent(JsonSerializer.Serialize(new UserRequest(Message)), Encoding.UTF8, "application/json");
        using var Response = await Client.PostAsync("api/test/message", Content);
        Response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<RequestResult>(await Response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task<JsonElement> GetAsync(HttpClient Client, string Path)
    {
        using var Response = await Client.GetAsync(Path);
        Response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<JsonElement>(await Response.Content.ReadAsStringAsync());
    }

    private static async Task<Dictionary<string, JsonElement>> StatesAsync(HttpClient Client) =>
        (await GetAsync(Client, "api/states")).EnumerateArray().ToDictionary(State => State.GetProperty("entity_id").GetString()!, State => State);

    private static async Task ServiceAsync(HttpClient Client, string Service, Dictionary<string, object> Data)
    {
        using var Content = new StringContent(JsonSerializer.Serialize(Data), Encoding.UTF8, "application/json");
        using var Response = await Client.PostAsync($"api/services/light/{Service}", Content);
        Response.EnsureSuccessStatusCode();
    }

    private sealed record OriginalLight(string EntityId, string Power, int? Brightness);
}

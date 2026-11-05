using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assister.Modules.GoogleCalendar;

public sealed class GoogleCalendarClient(CalendarStore Store, IHttpClientFactory Clients, ILogger<GoogleCalendarClient>? Logger = null)
{
    private readonly SemaphoreSlim Gate = new(1);
    private readonly Dictionary<string, (string Value, DateTimeOffset Expires)> Tokens = [];
    private async Task<string> Access(CalendarAccount Account, CancellationToken Ct)
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (Tokens.TryGetValue(Account.Id, out var Cached) && Cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2)) return Cached.Value;
            using var Key = JsonDocument.Parse(Store.Key(Account));
            static string Encode(byte[] Data) => Convert.ToBase64String(Data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var Header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
            var Payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = Account.Email, scope = "https://www.googleapis.com/auth/calendar", aud = "https://oauth2.googleapis.com/token", iat = Now, exp = Now + 3600 }));
            using var Rsa = RSA.Create();
            Rsa.ImportFromPem(Key.RootElement.GetProperty("private_key").GetString());
            var Unsigned = Header + "." + Payload;
            var Assertion = Unsigned + "." + Encode(Rsa.SignData(Encoding.UTF8.GetBytes(Unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            using var Response = await Clients.CreateClient("google-calendar").PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = Assertion }), Ct);
            if (!Response.IsSuccessStatusCode) throw new InvalidOperationException("Google authentication failed. Check the service account key and enabled Calendar API.");
            var Data = await Response.Content.ReadFromJsonAsync<JsonElement>(Ct);
            var Value = Data.GetProperty("access_token").GetString()!;
            Tokens[Account.Id] = (Value, DateTimeOffset.UtcNow.AddSeconds(Data.GetProperty("expires_in").GetInt32()));
            return Value;
        }
        finally { Gate.Release(); }
    }
    public async Task<JsonObject> Send(string AccountId, HttpMethod Method, string Path, JsonObject? Body, CancellationToken Ct, string? Etag = null)
    {
        using var Request = new HttpRequestMessage(Method, "https://www.googleapis.com/calendar/v3/" + Path);
        Request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Access(Store.Get(AccountId), Ct));
        if (Etag is not null) Request.Headers.TryAddWithoutValidation("If-Match", Etag);
        if (Body is not null) Request.Content = JsonContent.Create(Body);
        using var Response = await Clients.CreateClient("google-calendar").SendAsync(Request, Ct);
        if (!Response.IsSuccessStatusCode)
        {
            var Message = await FailureMessage(Response, Method, Path, Ct);
            Logger?.LogWarning("{CalendarFailure}", Message);
            throw new InvalidOperationException(Message);
        }
        return Response.StatusCode == System.Net.HttpStatusCode.NoContent ? new JsonObject { ["status"] = "deleted" } : (await Response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }
    public static async Task<string> FailureMessage(HttpResponseMessage Response, HttpMethod Method, string Path, CancellationToken Ct)
    {
        // Google returns the reason in the JSON body. Never expose the raw body, which may include account data.
        var Reasons = new List<string>();
        try
        {
            var Body = await Response.Content.ReadFromJsonAsync<JsonObject>(Ct);
            if (Body?["error"] is JsonObject Error)
                foreach (var Collection in new[] { "errors", "details" })
                    if (Error[Collection] is JsonArray Items)
                        foreach (var Item in Items.OfType<JsonObject>())
                            if (Item["reason"] is JsonValue Value && Value.TryGetValue<string>(out var Reason)
                                && Reason.Length <= 80 && System.Text.RegularExpressions.Regex.IsMatch(Reason, @"^[A-Za-z0-9_]+$")) Reasons.Add(Reason);
        }
        catch (Exception Error) when (Error is System.Text.Json.JsonException or InvalidOperationException or NotSupportedException) { }
        var Disabled = Reasons.Any(R => R is "accessNotConfigured" or "SERVICE_DISABLED" or "serviceDisabled");
        var Hint = Disabled
            ? "Enable the Google Calendar API in the Google Cloud project that owns this service account: https://console.cloud.google.com/apis/library/calendar-json.googleapis.com . Wait a few minutes after enabling it, then fetch calendars again."
            : Reasons.Any(R => R is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" or "RATE_LIMIT_EXCEEDED")
                ? "Google has limited Calendar API requests. Wait before retrying and check the project's Calendar API quota."
            : Reasons.Any(R => R is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT")
                ? "Google rejected the Calendar authorization scope. Check service account and organization access policies."
            : (int)Response.StatusCode == 412
                ? "This event changed since it was fetched. Refresh the event and review the changes before trying again."
            : Path.StartsWith("users/me/calendarList", StringComparison.Ordinal) && Method == HttpMethod.Get
                ? "Google denied calendar-list access. Check that the Google Calendar API is enabled in the service account's Cloud project and that organization policies allow it. Calendar sharing alone does not explain every 403."
                : "Check that the calendar is shared with the service account email and grants the required read or edit permission. Refresh before retrying changes.";
        var Operation = Path.StartsWith("users/me/calendarList", StringComparison.Ordinal) ? "calendar list" : "calendar events";
        var ReasonText = Reasons.Count == 0 ? "" : " (" + string.Join(", ", Reasons.Distinct().Take(4)) + ")";
        return $"Google Calendar returned {(int)Response.StatusCode} while accessing {Operation}{ReasonText}. {Hint}";
    }
    public static string Escape(string Id) => Uri.EscapeDataString(Id);
    public static string EventsPath(string CalendarId) => "calendars/" + Escape(CalendarId) + "/events";
    public async Task<JsonArray> Discover(string AccountId, CancellationToken Ct)
    {
        var Items = new JsonArray(); string? Next = null;
        do
        {
            var Page = await Send(AccountId, HttpMethod.Get, "users/me/calendarList?maxResults=250" + (Next is null ? "" : "&pageToken=" + Escape(Next)), null, Ct);
            foreach (var Item in Page["items"]!.AsArray()) Items.Add(Item!.DeepClone());
            Next = Page["nextPageToken"]?.GetValue<string>();
        } while (Next is not null && Items.Count < 1000);
        Store.Update(AccountId, Account => Account with { Calendars = Items.Select(Item => Account.Calendars.FirstOrDefault(C => C.Id == Item!["id"]!.GetValue<string>()) ?? new CalendarSubscription(Item!["id"]!.GetValue<string>(), Item["summary"]?.GetValue<string>() ?? "Calendar")).ToArray() });
        return Items;
    }
    public CalendarSubscription Subscribed(string AccountId, string CalendarId) => Store.Get(AccountId).Calendars.FirstOrDefault(C => C.Id == CalendarId && C.Enabled) ?? throw new ArgumentException("Select this calendar subscription first.");
    public Task<JsonObject> Events(string AccountId, string CalendarId, DateTimeOffset From, DateTimeOffset To, string? Page, CancellationToken Ct, int PageSize = 100)
    {
        Subscribed(AccountId, CalendarId);
        if (To <= From || To - From > TimeSpan.FromDays(366)) throw new ArgumentException("Choose a range of at most one year.");
        return Send(AccountId, HttpMethod.Get, EventsPath(CalendarId) + "?singleEvents=true&orderBy=startTime&maxResults=" + PageSize + "&timeMin=" + Escape(From.ToString("O")) + "&timeMax=" + Escape(To.ToString("O")) + (Page is null ? "" : "&pageToken=" + Escape(Page)), null, Ct);
    }
    public static void ValidateEvent(JsonObject Body, bool Create)
    {
        var Allowed = new[] { "summary", "description", "location", "start", "end", "reminders" };
        if (Body.Count == 0 || Body.Any(P => !Allowed.Contains(P.Key))) throw new ArgumentException("Unsupported event field.");
        if (Create && (Body["summary"] is null || Body["start"] is null || Body["end"] is null)) throw new ArgumentException("Supply title, start and end.");
        if (Body["summary"] is { } Title && (string.IsNullOrWhiteSpace(Title.GetValue<string>()) || Title.GetValue<string>().Length > 256)) throw new ArgumentException("Invalid title.");
        foreach (var Field in new[] { "start", "end" })
        {
            if (!Body.ContainsKey(Field)) continue;
            if (Body[Field] is not JsonObject Time || Time.Any(P => P.Key is not ("date" or "dateTime" or "timeZone")) || (Time["date"] is null) == (Time["dateTime"] is null)
                || !(Time["date"] is not null && DateOnly.TryParseExact(Time["date"]!.GetValue<string>(), "yyyy-MM-dd", out _) || Time["dateTime"] is not null && System.Text.RegularExpressions.Regex.IsMatch(Time["dateTime"]!.GetValue<string>(), @"(Z|[+-]\d{2}:\d{2})$") && DateTimeOffset.TryParse(Time["dateTime"]!.GetValue<string>(), out _))) throw new ArgumentException("Use an all-day date or timestamp with UTC offset.");
        }
        if (Body["start"] is JsonObject Start && Body["end"] is JsonObject End)
        {
            if ((Start["date"] is null) != (End["date"] is null)) throw new ArgumentException("Start and end must both be dates or both be timestamps.");
            if (Start["date"] is not null ? DateOnly.Parse(End["date"]!.GetValue<string>()) <= DateOnly.Parse(Start["date"]!.GetValue<string>())
                : DateTimeOffset.Parse(End["dateTime"]!.GetValue<string>()) <= DateTimeOffset.Parse(Start["dateTime"]!.GetValue<string>())) throw new ArgumentException("End must be after start.");
        }
        foreach (var Field in new[] { "description", "location" })
            if (Body[Field] is { } Value && Value.GetValue<string>().Length > 8000) throw new ArgumentException("Event field is too long.");
        if (Body.ContainsKey("reminders"))
        {
            if (Body["reminders"] is not JsonObject Reminders || Reminders.Any(P => P.Key is not ("useDefault" or "overrides")) || Reminders["useDefault"] is null) throw new ArgumentException("Invalid reminders.");
            var UseDefault = Reminders["useDefault"]!.GetValue<bool>();
            if (Reminders["overrides"] is { } Overrides)
            {
                if (UseDefault || Overrides is not JsonArray Array || Array.Count > 5) throw new ArgumentException("Use defaults or at most five notification overrides.");
                foreach (var Item in Array)
                    if (Item is not JsonObject Reminder || Reminder.Count != 2 || Reminder["method"]?.GetValue<string>() is not ("popup" or "email") || Reminder["minutes"] is null || Reminder["minutes"]!.GetValue<int>() is < 0 or > 40320) throw new ArgumentException("Invalid notification method or minutes.");
            }
        }
    }
}

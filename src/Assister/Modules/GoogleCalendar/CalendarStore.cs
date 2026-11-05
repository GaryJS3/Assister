using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Assister.Modules.GoogleCalendar;

public sealed record CalendarRichRecipient(string ClientId, string Owner);
public sealed record CalendarSubscription(string Id, string Name, bool Enabled = false, string SatelliteId = "", bool Announce = false,
    string[]? SatelliteIds = null, CalendarRichRecipient[]? RichClients = null,
    bool AllSatellites = false, bool AllRichClients = false, CalendarNotificationSettings? Notifications = null)
{
    // Old saved subscriptions retain their single target until explicitly saved in the new editor.
    [System.Text.Json.Serialization.JsonIgnore]
    public string[] SatelliteTargets => SatelliteIds ?? (string.IsNullOrWhiteSpace(SatelliteId) ? [] : [SatelliteId]);
}
public sealed record CalendarAccount(string Id, string Name, string Email, string ProtectedKey, CalendarSubscription[] Calendars);
public sealed class CalendarStore
{
    private readonly object Gate = new();
    private readonly string PathName;
    private readonly IDataProtector Protector;
    private CalendarAccount[] Accounts;
    public CalendarStore(IConfiguration Configuration)
    {
        var DirectoryName = Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(DirectoryName);
        PathName = Path.Combine(DirectoryName, "google-calendars.json");
        Protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DirectoryName, "calendar-protection-keys"))).CreateProtector("Assister.GoogleCalendar.v1");
        Accounts = File.Exists(PathName) ? JsonSerializer.Deserialize<CalendarAccount[]>(File.ReadAllText(PathName))! : [];
    }
    public CalendarAccount[] Snapshot() { lock (Gate) return Accounts.ToArray(); }
    public CalendarAccount Get(string Id) => Snapshot().SingleOrDefault(A => A.Id == Id) ?? throw new ArgumentException("Unknown calendar account.");
    public string Key(CalendarAccount Account) => Protector.Unprotect(Account.ProtectedKey);
    public CalendarAccount Add(string Name, string Credential)
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || string.IsNullOrWhiteSpace(Credential) || Credential.Length > 32768) throw new ArgumentException("Invalid account name or credential.");
        using var Json = JsonDocument.Parse(Credential);
        var Root = Json.RootElement;
        if (Root.GetProperty("type").GetString() != "service_account") throw new ArgumentException("Upload a service account JSON key.");
        var Email = Root.GetProperty("client_email").GetString()!;
        using var Rsa = System.Security.Cryptography.RSA.Create();
        Rsa.ImportFromPem(Root.GetProperty("private_key").GetString());
        var Account = new CalendarAccount(Guid.NewGuid().ToString("N"), Name.Trim(), Email, Protector.Protect(Credential), []);
        lock (Gate) Save([.. Accounts, Account]);
        return Account;
    }
    public void Update(string Id, Func<CalendarAccount, CalendarAccount> Change)
    { lock (Gate) { var Existing = Get(Id); Save(Accounts.Select(A => A.Id == Id ? Change(Existing) : A).ToArray()); } }
    public void Remove(string Id) { lock (Gate) Save(Accounts.Where(A => A.Id != Id).ToArray()); }
    private void Save(CalendarAccount[] Value)
    {
        File.WriteAllText(PathName + ".tmp", JsonSerializer.Serialize(Value));
        File.Move(PathName + ".tmp", PathName, true);
        Accounts = Value;
    }
}

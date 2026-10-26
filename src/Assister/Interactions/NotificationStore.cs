using System.Text.Json;
using Assister.Contracts;
using Microsoft.Data.Sqlite;

namespace Assister.Interactions;

// Independent of interaction lifetime: scheduled notices survive completed requests and restarts.
public sealed class NotificationStore : IDisposable
{
    private readonly object Gate = new();
    private readonly SqliteConnection Database;
    private readonly TimeProvider Clock;
    private readonly ClientSignals? Signals;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public NotificationStore(IConfiguration Configuration, TimeProvider? Clock = null, ClientSignals? Signals = null)
    {
        this.Signals = Signals;
        this.Clock = Clock ?? TimeProvider.System;
        var Folder = Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(Folder);
        Database = new(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Folder, "notifications.db"), Pooling = false }.ToString());
        Database.Open();
        using var Schema = Command("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS NotificationRoutes(Route TEXT PRIMARY KEY, Owner TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Notifications(
                Id TEXT PRIMARY KEY, Owner TEXT NOT NULL, IdempotencyKey TEXT NOT NULL,
                Request TEXT NOT NULL, Payload TEXT NOT NULL, UNIQUE(Owner,IdempotencyKey));
            CREATE INDEX IF NOT EXISTS IX_Notifications_Owner ON Notifications(Owner);
            CREATE TABLE IF NOT EXISTS NotificationReceipts(
                NotificationId TEXT NOT NULL, ClientId TEXT NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY(NotificationId,ClientId));
            """);
        Schema.ExecuteNonQuery();
    }

    public void RegisterRoute(string Route, string Owner)
    {
        lock (Gate)
        {
            using var Insert = Command("INSERT OR IGNORE INTO NotificationRoutes VALUES($p0,$p1)", Route, Owner);
            Insert.ExecuteNonQuery();
        }
    }
    public string? RouteOwner(string Route)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Owner FROM NotificationRoutes WHERE Route=$p0", Route);
            return Query.ExecuteScalar() as string;
        }
    }

    public ClientNotification Create(string Owner, CreateNotification Request)
    {
        var Now = Clock.GetUtcNow();
        if (Request.Kind is not ("timer" or "alarm" or "reminder" or "announcement" or "notification")
            || string.IsNullOrWhiteSpace(Request.Text) || Request.Text.Length > 2000
            || string.IsNullOrWhiteSpace(Request.IdempotencyKey) || Request.IdempotencyKey.Length > 128
            || Request.Importance is not ("notify" or "high-importance" or "acknowledge" or "alarm")
            || Request.TargetClientId is { Length: > 128 }
            || Request.TargetGroup is not null && (string.IsNullOrWhiteSpace(Request.TargetGroup) || Request.TargetGroup.Length > 64 || Request.TargetClientId is not null))
            throw new ArgumentException("invalid_notification");
        lock (Gate)
        {
            var Encoded = JsonSerializer.Serialize(Request, Json);
            using var Existing = Command("SELECT Request,Payload FROM Notifications WHERE Owner=$p0 AND IdempotencyKey=$p1", Owner, Request.IdempotencyKey);
            using (var Reader = Existing.ExecuteReader())
                if (Reader.Read())
                {
                    if (Reader.GetString(0) != Encoded) throw new InvalidOperationException("notification_conflict");
                    return Read(Reader.GetString(1));
                }
            var Due = Request.DueAt ?? Now;
            if (Due > Now.AddDays(365)) throw new ArgumentException("invalid_notification_schedule");
            var Expires = Request.ExpiresAt ?? Due.AddDays(1);
            if (Expires <= Due || Expires <= Now || Expires > Due.AddDays(30))
                throw new ArgumentException("invalid_notification_schedule");
            Prune(Now);
            using var Count = Command("SELECT COUNT(*) FROM Notifications WHERE Owner=$p0", Owner);
            if ((long)Count.ExecuteScalar()! >= 1000) throw new InvalidOperationException("notification_quota");
            var Item = new ClientNotification(Guid.NewGuid(), Request.Kind, Request.Text, Now, Due, Expires,
                Request.Importance, Request.TargetClientId, "active", Request.TargetGroup);
            using var Insert = Command("INSERT INTO Notifications VALUES($p0,$p1,$p2,$p3,$p4)",
                Item.Id.ToString(), Owner, Request.IdempotencyKey, Encoded, JsonSerializer.Serialize(Item, Json));
            Insert.ExecuteNonQuery();
            return Item;
        }
    }

    public ClientNotification[] List(string Owner, string ClientId, bool DueOnly = false)
    {
        // Resolve membership before taking the notification lock; device signalling uses its own lock.
        var Groups = Signals?.Clients(Owner).FirstOrDefault(Client => Client.ClientId == ClientId)?.Groups ?? [];
        lock (Gate)
        {
            var Now = Clock.GetUtcNow();
            using var Query = Command("SELECT Payload FROM Notifications WHERE Owner=$p0", Owner);
            using var Reader = Query.ExecuteReader();
            var Items = new List<ClientNotification>();
            while (Reader.Read())
            {
                var Item = Read(Reader.GetString(0));
                if ((Item.TargetClientId is null || Item.TargetClientId == ClientId)
                    && (Item.TargetGroup is null || Groups.Contains(Item.TargetGroup, StringComparer.OrdinalIgnoreCase))
                    && (!DueOnly || Item.Status == "active" && Item.DueAt <= Now && Item.ExpiresAt > Now))
                    Items.Add(Item);
            }
            return Items.OrderBy(Item => Item.DueAt).ThenBy(Item => Item.Id).ToArray();
        }
    }

    public bool Cancel(string Owner, string ClientId, Guid Id)
    {
        lock (Gate)
        {
            var Item = List(Owner, ClientId).FirstOrDefault(Item => Item.Id == Id);
            if (Item is null) return false;
            using var Update = Command("UPDATE Notifications SET Payload=$p0 WHERE Id=$p1", JsonSerializer.Serialize(Item with { Status = "cancelled" }, Json), Id.ToString());
            Update.ExecuteNonQuery();
            return true;
        }
    }

    public NotificationReceipt? Acknowledge(string Owner, string ClientId, Guid Id, string State)
    {
        if (State is not ("received" or "displayed" or "acknowledged" or "dismissed" or "ignored"))
            throw new ArgumentException("invalid_notification_receipt");
        lock (Gate)
        {
            if (!List(Owner, ClientId, true).Any(Item => Item.Id == Id)) return null;
            using var Existing = Command("SELECT Payload FROM NotificationReceipts WHERE NotificationId=$p0 AND ClientId=$p1", Id.ToString(), ClientId);
            var Previous = Existing.ExecuteScalar() as string;
            if (Previous is not null && JsonSerializer.Deserialize<NotificationReceipt>(Previous, Json) is { } Receipt && Receipt.State == State) return Receipt;
            var Result = new NotificationReceipt(Id, ClientId, State, Clock.GetUtcNow());
            using var Insert = Command("INSERT OR REPLACE INTO NotificationReceipts VALUES($p0,$p1,$p2)", Id.ToString(), ClientId, JsonSerializer.Serialize(Result, Json));
            Insert.ExecuteNonQuery();
            return Result;
        }
    }

    private void Prune(DateTimeOffset Now)
    {
        // Keep expired/cancelled items for 30 days of retry deduplication, then release quota.
        using var Delete = Command("DELETE FROM Notifications WHERE julianday(json_extract(Payload,'$.expiresAt')) < julianday($p0)", Now.AddDays(-30).ToString("O"));
        Delete.ExecuteNonQuery();
        using var Receipts = Command("DELETE FROM NotificationReceipts WHERE NotificationId NOT IN (SELECT Id FROM Notifications)");
        Receipts.ExecuteNonQuery();
    }
    private static ClientNotification Read(string Payload) => JsonSerializer.Deserialize<ClientNotification>(Payload, Json)!;
    private SqliteCommand Command(string Sql, params object[] Values)
    {
        var Result = Database.CreateCommand();
        Result.CommandText = Sql;
        for (var Index = 0; Index < Values.Length; Index++) Result.Parameters.AddWithValue("$p" + Index, Values[Index]);
        return Result;
    }
    public void Dispose() => Database.Dispose();
}

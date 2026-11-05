using System.Text.Json;
using Assister.Contracts;

namespace Assister.Interactions;

public sealed record SatelliteGroupMembership(string SatelliteId, string Owner, IReadOnlyList<string> Groups);
public sealed record SatelliteGroupsInput(string Owner, IReadOnlyList<string> Groups);
public sealed record SatelliteNotification(string SatelliteId, string Owner, ClientNotification Notification);

public sealed partial class NotificationStore
{
    private void InitializeSatelliteGroups()
    {
        using var Schema = Command("""
            CREATE TABLE IF NOT EXISTS SatelliteDeviceGroups(
                SatelliteId TEXT NOT NULL, Owner TEXT NOT NULL, GroupsJson TEXT NOT NULL, PRIMARY KEY(SatelliteId,Owner));
            CREATE TABLE IF NOT EXISTS SatelliteNotificationDeliveries(
                NotificationId TEXT NOT NULL, SatelliteId TEXT NOT NULL, Resolved INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(NotificationId,SatelliteId));
            """);
        Schema.ExecuteNonQuery();
    }

    public SatelliteGroupMembership[] SatelliteGroups()
    {
        lock (Gate)
        {
            using var Query = Command("SELECT SatelliteId,Owner,GroupsJson FROM SatelliteDeviceGroups ORDER BY Owner,SatelliteId");
            using var Reader = Query.ExecuteReader();
            var Items = new List<SatelliteGroupMembership>();
            while (Reader.Read()) Items.Add(new(Reader.GetString(0), Reader.GetString(1), JsonSerializer.Deserialize<string[]>(Reader.GetString(2), Json)!));
            return Items.ToArray();
        }
    }

    public void SetSatelliteGroups(string SatelliteId, string Owner, IReadOnlyList<string>? Groups)
    {
        if (string.IsNullOrWhiteSpace(Owner) || Owner.Length > 128 || string.IsNullOrWhiteSpace(SatelliteId) || SatelliteId.Length > 128
            || Groups is null || Groups.Count > 32 || Groups.Any(Name => string.IsNullOrWhiteSpace(Name) || Name.Length > 64))
            throw new ArgumentException("Supply an owner and at most 32 group names of 1–64 characters.");
        var Names = Groups.Select(Name => Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (Gate)
        {
            using var Write = Names.Length == 0
                ? Command("DELETE FROM SatelliteDeviceGroups WHERE SatelliteId=$p0 AND Owner=$p1", SatelliteId, Owner)
                : Command("INSERT OR REPLACE INTO SatelliteDeviceGroups VALUES($p0,$p1,$p2)", SatelliteId, Owner, JsonSerializer.Serialize(Names, Json));
            Write.ExecuteNonQuery();
        }
    }

    public void RemoveSatelliteGroups(string SatelliteId)
    {
        lock (Gate)
        {
            using var Delete = Command("DELETE FROM SatelliteDeviceGroups WHERE SatelliteId=$p0", SatelliteId);
            Delete.ExecuteNonQuery();
        }
    }

    public ClientNotification[] PendingForSatellite(string SatelliteId)
    {
        lock (Gate)
        {
            var Memberships = SatelliteGroups().Where(Item => Item.SatelliteId == SatelliteId).ToArray();
            using var Query = Command("""
                SELECT Owner,Payload FROM Notifications WHERE Id NOT IN
                    (SELECT NotificationId FROM SatelliteNotificationDeliveries WHERE SatelliteId=$p0)
                """, SatelliteId);
            using var Reader = Query.ExecuteReader();
            var Items = new List<ClientNotification>();
            var Now = Clock.GetUtcNow();
            while (Reader.Read())
            {
                var Item = Read(Reader.GetString(1));
                if (Item.Status == "active" && Item.DueAt <= Now && Item.ExpiresAt > Now && Item.TargetClientId is null
                    && Item.TargetGroup is { } Group && Memberships.Any(Membership => Membership.Owner == Reader.GetString(0)
                        && Membership.Groups.Contains(Group, StringComparer.OrdinalIgnoreCase))) Items.Add(Item);
            }
            return Items.OrderBy(Item => Item.DueAt).ThenBy(Item => Item.Id).ToArray();
        }
    }

    public void SatelliteDelivered(Guid NotificationId, string SatelliteId)
    {
        lock (Gate)
        {
            using var Write = Command("INSERT OR IGNORE INTO SatelliteNotificationDeliveries(NotificationId,SatelliteId) VALUES($p0,$p1)", NotificationId.ToString(), SatelliteId);
            Write.ExecuteNonQuery();
        }
    }

    public SatelliteNotification[] ResolvedSatelliteNotifications()
    {
        lock (Gate)
        {
            var Memberships = SatelliteGroups();
            using var Query = Command("""
                SELECT d.SatelliteId,n.Owner,n.Payload FROM SatelliteNotificationDeliveries d
                JOIN Notifications n ON n.Id=d.NotificationId WHERE d.Resolved=0
                """);
            using var Reader = Query.ExecuteReader();
            var Items = new List<SatelliteNotification>();
            while (Reader.Read())
            {
                var Item = Read(Reader.GetString(2));
                var Id = Reader.GetString(0); var Owner = Reader.GetString(1);
                if (Item.Status != "active" || Item.ExpiresAt <= Clock.GetUtcNow()
                    || !Memberships.Any(Member => Member.SatelliteId == Id && Member.Owner == Owner
                        && Member.Groups.Contains(Item.TargetGroup!, StringComparer.OrdinalIgnoreCase))) Items.Add(new(Id, Owner, Item));
            }
            return Items.ToArray();
        }
    }

    public void ResolveSatelliteNotification(Guid Id, string SatelliteId)
    {
        lock (Gate)
        {
            using var Write = Command("UPDATE SatelliteNotificationDeliveries SET Resolved=1 WHERE NotificationId=$p0 AND SatelliteId=$p1", Id.ToString(), SatelliteId);
            Write.ExecuteNonQuery();
        }
    }
}

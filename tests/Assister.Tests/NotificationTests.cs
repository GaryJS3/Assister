using Assister.Contracts;
using Assister.Interactions;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class NotificationTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void SchedulesSurviveRestartAndReceiptsNeverBlockOtherClients()
    {
        var Folder = Path.Combine(Path.GetTempPath(), "assister-notifications-" + Guid.NewGuid());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Folder }).Build();
        var Time = new Clock();
        Guid Id;
        try
        {
            using (var Store = new NotificationStore(Config, Time))
            {
                var Request = new CreateNotification("timer", "Tea is ready", "tea", Time.Now.AddMinutes(3));
                var Item = Store.Create("gary", Request);
                Id = Item.Id;
                Assert.Equal(Item, Store.Create("gary", Request));
                Assert.Throws<InvalidOperationException>(() => Store.Create("gary", Request with { Text = "Changed" }));
                Assert.Empty(Store.List("gary", "phone", true));
            }
            Time.Now = Time.Now.AddMinutes(3);
            using (var Store = new NotificationStore(Config, Time))
            {
                Assert.Equal(Id, Assert.Single(Store.List("gary", "phone", true)).Id);
                var Receipt = Store.Acknowledge("gary", "phone", Id, "ignored");
                Assert.Equal(Receipt, Store.Acknowledge("gary", "phone", Id, "ignored"));
                Assert.Single(Store.List("gary", "phone", true));
                Assert.Single(Store.List("gary", "desktop", true));
                Assert.Empty(Store.List("other", "phone", true));
                Assert.Null(Store.Acknowledge("other", "phone", Id, "acknowledged"));
                Assert.False(Store.Cancel("other", "phone", Id));
                Assert.True(Store.Cancel("gary", "phone", Id));
                Assert.Empty(Store.List("gary", "desktop", true));
                Assert.Null(Store.Acknowledge("gary", "phone", Id, "displayed"));
            }
        }
        finally { Directory.Delete(Folder, true); }
    }

    [Theory]
    [InlineData("timer")]
    [InlineData("alarm")]
    [InlineData("reminder")]
    [InlineData("announcement")]
    [InlineData("notification")]
    public void TargetingExpiryAndValidation(string Kind)
    {
        var Folder = Path.Combine(Path.GetTempPath(), "assister-notifications-" + Guid.NewGuid());
        var Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Assister:DataPath"] = Folder }).Build();
        var Time = new Clock();
        try
        {
            using var Store = new NotificationStore(Config, Time);
            var Request = new CreateNotification(Kind, "Hello", "key", ExpiresAt: Time.Now.AddMinutes(1), TargetClientId: "phone");
            var Item = Store.Create("gary", Request);
            Assert.Single(Store.List("gary", "phone", true));
            Assert.Empty(Store.List("gary", "desktop"));
            Assert.False(Store.Cancel("gary", "desktop", Item.Id));
            Assert.Throws<ArgumentException>(() => Store.Acknowledge("gary", "phone", Item.Id, "played"));
            Assert.Throws<ArgumentException>(() => Store.Create("gary", Request with { IdempotencyKey = "bad", Kind = "invalid" }));
            Assert.Throws<ArgumentException>(() => Store.Create("gary", Request with { IdempotencyKey = "bad", ExpiresAt = Time.Now }));
            Assert.Throws<ArgumentException>(() => Store.Create("gary", Request with { IdempotencyKey = "bad", DueAt = DateTimeOffset.MaxValue, ExpiresAt = null }));
            Time.Now = Time.Now.AddMinutes(1);
            Assert.Empty(Store.List("gary", "phone", true));
            Assert.Null(Store.Acknowledge("gary", "phone", Item.Id, "acknowledged"));
            Assert.Equal(Item, Store.Create("gary", Request));
        }
        finally { Directory.Delete(Folder, true); }
    }
}

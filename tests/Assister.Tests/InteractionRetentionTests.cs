using Assister.Contracts;
using Assister.Interactions;
using Microsoft.Extensions.Configuration;

namespace Assister.Tests;

public sealed class InteractionRetentionTests : IDisposable
{
    private readonly string DataPath = Path.Combine(Path.GetTempPath(), "assister-retention-" + Guid.NewGuid());
    private readonly TestClock Clock = new();
    private InteractionStore Open(params (string Key, string Value)[] Settings)
    {
        var Values = Settings.ToDictionary(Item => Item.Key, Item => (string?)Item.Value);
        Values["Assister:DataPath"] = DataPath;
        return new(new ConfigurationBuilder().AddInMemoryCollection(Values).Build(), Clock);
    }

    [Fact]
    public void UploadQuotaUsesBytesAndExpiredUploadsReleaseSpace()
    {
        using var Store = Open(("RichClients:MaxAttachmentMiB", "1"));
        // More than 100 ordinary voice uploads must not exhaust a lifetime count.
        for (var Index = 0; Index < 110; Index++)
            Store.SaveAttachment("owner", "Voice.pcm", "audio/pcm", "microphone", new byte[3200], null);
        Store.SaveAttachment("owner", "large.txt", "text/plain", "user", new byte[600000], "text");
        Assert.Equal("attachment_quota_exceeded", Assert.Throws<InvalidOperationException>(() =>
            Store.SaveAttachment("owner", "large.txt", "text/plain", "user", new byte[600000], "text")).Message);
        // Quotas are isolated by owner.
        Store.SaveAttachment("other", "large.txt", "text/plain", "user", new byte[600000], "text");
        Clock.Advance(TimeSpan.FromHours(25));
        // Upload retries cleanup itself when full, even before the scheduled sweep.
        Store.SaveAttachment("owner", "large.txt", "text/plain", "user", new byte[600000], "text");
    }

    [Fact]
    public void AudioExpiresBeforeHistoryAndActiveInputIsPreserved()
    {
        using var Store = Open();
        var Conversation = Store.CreateConversation("owner");
        var Audio = Store.SaveAttachment("owner", "Voice.pcm", "audio/pcm", "microphone", new byte[3200], null);
        var ActiveAudio = Store.SaveAttachment("owner", "Voice.pcm", "audio/pcm", "microphone", new byte[3200], null);
        var Text = Store.SaveAttachment("owner", "note.txt", "text/plain", "user", "hello"u8.ToArray(), "hello");
        var Done = Store.Submit(Conversation.Id, "owner", new("voice", "done", [Text.Id], Audio.Id));
        Store.SaveAudio(Done.Id, new byte[44]);
        Store.SaveSegment(Done.Id, 0, new byte[44], new(new byte[2], 16000, 2, 1));
        Assert.NotNull(Store.Segment(Done.Id, 0));
        Store.Append(Done.Id, "interaction.completed", new { }, "completed");
        var Active = Store.Submit(Conversation.Id, "owner", new("voice", "active", AudioAttachmentId: ActiveAudio.Id));
        Clock.Advance(TimeSpan.FromHours(25));
        Store.Prune();
        Assert.Null(Store.Audio(Done.Id));
        Assert.Null(Store.Segment(Done.Id, 0));
        Assert.Null(Store.Attachment(Audio.Id, "owner"));
        Assert.NotNull(Store.Get(Done.Id));
        Assert.Single(Store.Documents(Done.Id));
        Assert.NotNull(Store.InputAudio(Active.Id));
        Clock.Advance(TimeSpan.FromDays(31));
        Store.Prune();
        Assert.Null(Store.Get(Done.Id));
        Assert.Empty(Store.Events(Done.Id, 0));
        Assert.Null(Store.Attachment(Text.Id, "owner"));
        Assert.NotNull(Store.Get(Active.Id));
        Assert.NotNull(Store.InputAudio(Active.Id));
        Assert.True(Store.OwnsConversation(Conversation.Id, "owner"));
    }

    [Fact]
    public void CountRetentionRemovesAllDependentRowsAndPersistsAfterReopen()
    {
        Guid OldId;
        Guid RecentId;
        using (var Store = Open(("RichClients:MaxInteractions", "1")))
        {
            var Conversation = Store.CreateConversation("owner");
            OldId = Store.Submit(Conversation.Id, "owner", new("old", "old")).Id;
            Store.SelectContext(OldId, new("input", "user_input", "user", "Input", "old", new { }, 0));
            Store.SaveAudio(OldId, new byte[44]);
            Store.SaveSegment(OldId, 0, new byte[44], new(new byte[2], 16000, 2, 1));
            Store.Playback(OldId, "client", new("started", Guid.NewGuid()));
            Store.Append(OldId, "interaction.completed", new { }, "completed");
            Clock.Advance(TimeSpan.FromSeconds(1));
            RecentId = Store.Submit(Conversation.Id, "owner", new("recent", "recent")).Id;
            Store.Append(RecentId, "interaction.completed", new { }, "completed");
            Store.Prune();
            Assert.Null(Store.Get(OldId));
            Assert.Empty(Store.Context(OldId));
            Assert.Null(Store.Audio(OldId));
            Assert.Null(Store.Segment(OldId, 0));
            Assert.NotNull(Store.Get(RecentId));
        }
        using var Reopened = Open(("RichClients:MaxInteractions", "1"));
        Assert.Null(Reopened.Get(OldId));
        Assert.NotNull(Reopened.Get(RecentId));
    }

    [Fact]
    public void UnusedUploadsCanBeDeletedOnlyByTheirOwner()
    {
        using var Store = Open();
        var Conversation = Store.CreateConversation("owner");
        var Text = Store.SaveAttachment("owner", "note.txt", "text/plain", "user", "hi"u8.ToArray(), "hi");
        Assert.False(Store.DeleteAttachment(Text.Id, "other"));
        Assert.True(Store.DeleteAttachment(Text.Id, "owner"));
        var Audio = Store.SaveAttachment("owner", "Voice.pcm", "audio/pcm", "microphone", new byte[2], null);
        Store.Submit(Conversation.Id, "owner", new("voice", "active", AudioAttachmentId: Audio.Id));
        Assert.False(Store.DeleteAttachment(Audio.Id, "owner"));
    }

    [Fact]
    public void LongStreamsResolveRoutingAndPlaybackDuplicatesAreAtomic()
    {
        using var Store = Open();
        var Conversation = Store.CreateConversation("owner");
        var Item = Store.Submit(Conversation.Id, "owner", new("hello", "long"));
        for (var Index = 0; Index < 300; Index++) Store.Append(Item.Id, "response.delta", new { text = "a" });
        var RoutingId = Guid.NewGuid();
        Store.Append(Item.Id, "response.completed", new { routingConversationId = RoutingId });
        Store.SaveAudio(Item.Id, new byte[44]);
        Store.Append(Item.Id, "interaction.completed", new { }, "completed");
        Assert.DoesNotContain(Store.Events(Item.Id, 0), Event => Event.Type == "response.completed");
        Assert.Equal(RoutingId, Store.RoutingConversation(Item.Id));
        var Report = new PlaybackReport("started", Guid.NewGuid());
        Assert.True(Store.Playback(Item.Id, "client", Report, out var Started));
        Assert.True(Started);
        Assert.True(Store.Playback(Item.Id, "client", Report with { State = "completed" }, out var Completed));
        Assert.True(Completed);
        Assert.True(Store.Playback(Item.Id, "client", Report with { State = "completed" }, out var Duplicate));
        Assert.False(Duplicate);
    }

    public void Dispose()
    {
        using var Connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(DataPath, "interactions.db")}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(Connection);
        if (Directory.Exists(DataPath)) Directory.Delete(DataPath, recursive: true);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan By) => Now += By;
    }
}

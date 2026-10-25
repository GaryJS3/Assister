using System.Text.Json;
using Assister.Contracts;
using Microsoft.Data.Sqlite;

namespace Assister.Interactions;

// A single serialized SQLite writer gives event sequence allocation and snapshots one commit boundary.
// This follows the diagnostic-store pattern, but protocol data has its own lifetime and schema.
public sealed class InteractionStore : IDisposable
{
    private readonly object Gate = new();
    private readonly SqliteConnection Database;
    private readonly Assister.Diagnostics.DiagnosticSanitizer Sanitizer;
    private readonly TimeProvider Clock;
    private readonly int RetentionDays;
    private readonly int MaxInteractions;
    private readonly int AudioRetentionHours;
    private readonly long MaxAttachmentBytes;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public InteractionStore(IConfiguration Configuration, TimeProvider? Clock = null)
    {
        this.Clock = Clock ?? TimeProvider.System;
        RetentionDays = Math.Clamp(Configuration.GetValue("RichClients:RetentionDays", 30), 1, 365);
        MaxInteractions = Math.Clamp(Configuration.GetValue("RichClients:MaxInteractions", 10000), 1, 100000);
        AudioRetentionHours = Math.Clamp(Configuration.GetValue("RichClients:AudioRetentionHours", 24), 1, 168);
        MaxAttachmentBytes = Math.Clamp(Configuration.GetValue("RichClients:MaxAttachmentMiB", 100), 1, 1024) * 1024L * 1024;
        Sanitizer = new(Configuration);
        var Path = System.IO.Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(Path);
        Database = new($"Data Source={System.IO.Path.Combine(Path, "interactions.db")}");
        Database.Open();
        using var Command = Database.CreateCommand();
        Command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS ClientConversations(Id TEXT PRIMARY KEY, Owner TEXT NOT NULL, Title TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Interactions(Id TEXT PRIMARY KEY, ConversationId TEXT NOT NULL REFERENCES ClientConversations(Id), Owner TEXT NOT NULL,
                IdempotencyKey TEXT NOT NULL, Input TEXT NOT NULL, Response TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL,
                CreatedAt TEXT NOT NULL, LastSequence INTEGER NOT NULL DEFAULT 0, RunId TEXT, CancelRequested INTEGER NOT NULL DEFAULT 0,
                UNIQUE(Owner,IdempotencyKey));
            CREATE TABLE IF NOT EXISTS InteractionEvents(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), Sequence INTEGER NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY(InteractionId,Sequence));
            CREATE INDEX IF NOT EXISTS IX_Interactions_Conversation ON Interactions(ConversationId,CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_Interactions_RunId ON Interactions(RunId);
            CREATE TABLE IF NOT EXISTS ClientAttachments(Id TEXT PRIMARY KEY, Owner TEXT NOT NULL, Metadata TEXT NOT NULL, Content BLOB NOT NULL, ExtractedText TEXT);
            CREATE TABLE IF NOT EXISTS InteractionAttachments(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), AttachmentId TEXT NOT NULL REFERENCES ClientAttachments(Id), Position INTEGER NOT NULL,
                PRIMARY KEY(InteractionId,AttachmentId));
            CREATE INDEX IF NOT EXISTS IX_InteractionAttachments_Attachment ON InteractionAttachments(AttachmentId);
            CREATE TABLE IF NOT EXISTS InteractionContexts(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), ContextId TEXT NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY(InteractionId,ContextId));
            CREATE TABLE IF NOT EXISTS InteractionOptions(InteractionId TEXT PRIMARY KEY REFERENCES Interactions(Id), Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS InteractionAudio(InteractionId TEXT PRIMARY KEY REFERENCES Interactions(Id), Content BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS InteractionAudioSegments(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), SegmentIndex INTEGER NOT NULL, Content BLOB NOT NULL, PRIMARY KEY(InteractionId,SegmentIndex));
            CREATE TABLE IF NOT EXISTS PlaybackReports(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), ClientId TEXT NOT NULL, PlaybackId TEXT NOT NULL, State TEXT NOT NULL, PRIMARY KEY(InteractionId,ClientId,PlaybackId));
            """;
        Command.ExecuteNonQuery();
    }

    // Active work and its uploads are never expired. SQLite reuses the freed pages.
    public void Prune()
    {
        lock (Gate)
        {
            var Now = Clock.GetUtcNow();
            using var Transaction = Database.BeginTransaction();
            void Execute(string Sql, params (string, object?)[] Values)
            {
                using var Cleanup = Command(Sql, Values);
                Cleanup.Transaction = Transaction;
                Cleanup.ExecuteNonQuery();
            }
            Execute("CREATE TEMP TABLE IF NOT EXISTS ExpiredInteractions(Id TEXT PRIMARY KEY); DELETE FROM ExpiredInteractions;");
            Execute("""
                INSERT INTO ExpiredInteractions
                SELECT Id FROM (
                    SELECT Id, CreatedAt, ROW_NUMBER() OVER (ORDER BY CreatedAt DESC, Id DESC) AS Position
                    FROM Interactions WHERE Status IN ('completed','failed','cancelled')
                ) WHERE julianday(CreatedAt) < julianday($cutoff) OR Position > $maximum
                """, ("$cutoff", Now.AddDays(-RetentionDays).ToString("O")), ("$maximum", MaxInteractions));
            foreach (var Table in new[] { "InteractionEvents", "InteractionAttachments", "InteractionContexts", "InteractionOptions", "InteractionAudio", "InteractionAudioSegments", "PlaybackReports" })
                Execute($"DELETE FROM {Table} WHERE InteractionId IN (SELECT Id FROM ExpiredInteractions)");
            Execute("DELETE FROM Interactions WHERE Id IN (SELECT Id FROM ExpiredInteractions)");
            Execute("""
                DELETE FROM InteractionAudio WHERE InteractionId IN (
                    SELECT Id FROM Interactions WHERE Status IN ('completed','failed','cancelled') AND julianday(CreatedAt) < julianday($cutoff))
                """, ("$cutoff", Now.AddHours(-AudioRetentionHours).ToString("O")));
            Execute("DELETE FROM InteractionAudioSegments WHERE InteractionId IN (SELECT Id FROM Interactions WHERE Status IN ('completed','failed','cancelled') AND julianday(CreatedAt) < julianday($cutoff))",
                ("$cutoff", Now.AddHours(-AudioRetentionHours).ToString("O")));
            Execute("""
                DELETE FROM ClientAttachments
                WHERE julianday(json_extract(Metadata,'$.createdAt')) < julianday($cutoff)
                    AND NOT EXISTS (SELECT 1 FROM InteractionAttachments WHERE AttachmentId=ClientAttachments.Id)
                    AND Id NOT IN (
                        SELECT json_extract(O.Payload,'$.audioAttachmentId') FROM InteractionOptions O JOIN Interactions I ON I.Id=O.InteractionId
                        WHERE I.Status NOT IN ('completed','failed','cancelled')
                            AND json_extract(O.Payload,'$.audioAttachmentId') IS NOT NULL)
                """, ("$cutoff", Now.AddHours(-AudioRetentionHours).ToString("O")));
            Execute("""
                DELETE FROM ClientConversations WHERE julianday(CreatedAt) < julianday($cutoff)
                    AND NOT EXISTS (SELECT 1 FROM Interactions WHERE ConversationId=ClientConversations.Id)
                """, ("$cutoff", Now.AddDays(-RetentionDays).ToString("O")));
            Execute("DELETE FROM ExpiredInteractions");
            Transaction.Commit();
        }
    }
    private SqliteCommand Command(string Sql, params (string, object?)[] Values)
    {
        var Command = Database.CreateCommand();
        Command.CommandText = Sql;
        foreach (var (Name, Value) in Values) Command.Parameters.AddWithValue(Name, Value ?? DBNull.Value);
        return Command;
    }
    public ClientConversation CreateConversation(string Owner)
    {
        lock (Gate)
        {
            var Item = new ClientConversation(Guid.NewGuid(), "New conversation", Clock.GetUtcNow());
            using var Insert = Command("INSERT INTO ClientConversations VALUES($id,$owner,$title,$created)",
                ("$id", Item.Id.ToString()), ("$owner", Owner), ("$title", Item.Title), ("$created", Item.CreatedAt.ToString("O")));
            Insert.ExecuteNonQuery();
            return Item;
        }
    }
    public IReadOnlyList<ClientConversation> Conversations(string Owner)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id,Title,CreatedAt FROM ClientConversations WHERE Owner=$owner ORDER BY CreatedAt DESC LIMIT 100", ("$owner", Owner));
            using var Reader = Query.ExecuteReader();
            var Items = new List<ClientConversation>();
            while (Reader.Read()) Items.Add(new(Guid.Parse(Reader.GetString(0)), Reader.GetString(1), DateTimeOffset.Parse(Reader.GetString(2))));
            return Items;
        }
    }
    public string? ConversationOwner(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Owner FROM ClientConversations WHERE Id=$id", ("$id", Id.ToString()));
            return Query.ExecuteScalar() as string;
        }
    }

    public bool OwnsConversation(Guid Id, string Owner)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT 1 FROM ClientConversations WHERE Id=$id AND Owner=$owner", ("$id", Id.ToString()), ("$owner", Owner));
            return Query.ExecuteScalar() is not null;
        }
    }
    public InteractionSnapshot Submit(Guid ConversationId, string Owner, SubmitInteraction Request, string? ClientId = null)
    {
        lock (Gate)
        {
            using var Previous = Command("SELECT Id FROM Interactions WHERE Owner=$owner AND IdempotencyKey=$key", ("$owner", Owner), ("$key", Request.IdempotencyKey));
            if (Previous.ExecuteScalar() is string Existing)
            {
                var Item = Get(Guid.Parse(Existing))!;
                if (Item.ConversationId != ConversationId || Options(Item.Id).Message != Request.Message) throw new InvalidOperationException("idempotency_conflict");
                if (!Attachments(Item.Id).Select(Attachment => Attachment.Id).SequenceEqual(Request.AttachmentIds ?? [])) throw new InvalidOperationException("idempotency_conflict");
                var SavedOptions = Options(Item.Id);
                if (SavedOptions.AudioAttachmentId != Request.AudioAttachmentId || SavedOptions.Speak != Request.Speak || SavedOptions.StreamSpeech != Request.StreamSpeech) throw new InvalidOperationException("idempotency_conflict");
                return Item;
            }
            using var Count = Command("SELECT COUNT(*) FROM Interactions WHERE Status IN ('created','transcribing','running','responding')");
            if ((long)Count.ExecuteScalar()! >= 64) throw new InvalidOperationException("queue_full");
            if (!OwnsConversation(ConversationId, Owner)) throw new InvalidOperationException("conversation_not_found");
            var AttachmentIds = Request.AttachmentIds ?? [];
            if (AttachmentIds.Count > 8 || AttachmentIds.Distinct().Count() != AttachmentIds.Count) throw new InvalidOperationException("invalid_attachments");
            var TextSize = 0;
            foreach (var AttachmentId in AttachmentIds)
            {
                var Uploaded = Attachment(AttachmentId, Owner) ?? throw new InvalidOperationException("attachment_not_found");
                if (Uploaded.Metadata.Processing != "ready") throw new InvalidOperationException("attachment_processing_unsupported");
                TextSize += Uploaded.Text?.Length ?? 0;
            }
            if (TextSize > 12000) throw new InvalidOperationException("attachment_context_too_large");
            if (Request.AudioAttachmentId is { } AudioId)
            {
                var Audio = Attachment(AudioId, Owner) ?? throw new InvalidOperationException("attachment_not_found");
                if (Audio.Metadata.MimeType != "audio/pcm" || Audio.Content.Length % 2 != 0) throw new InvalidOperationException("invalid_audio");
            }
            var Id = Guid.NewGuid();
            using var Transaction = Database.BeginTransaction();
            using var Insert = Command("INSERT INTO Interactions(Id,ConversationId,Owner,IdempotencyKey,Input,Status,CreatedAt) VALUES($id,$conversation,$owner,$key,$input,'created',$created)",
                ("$id", Id.ToString()), ("$conversation", ConversationId.ToString()), ("$owner", Owner), ("$key", Request.IdempotencyKey), ("$input", Request.Message), ("$created", Clock.GetUtcNow().ToString("O")));
            Insert.Transaction = Transaction;
            Insert.ExecuteNonQuery();
            using var SaveOptions = Command("INSERT INTO InteractionOptions VALUES($id,$payload)", ("$id", Id.ToString()), ("$payload", JsonSerializer.Serialize(Request, Json)));
            SaveOptions.Transaction = Transaction;
            SaveOptions.ExecuteNonQuery();
            for (var Position = 0; Position < AttachmentIds.Count; Position++)
            {
                using var Bind = Command("INSERT INTO InteractionAttachments VALUES($interaction,$attachment,$position)",
                    ("$interaction", Id.ToString()), ("$attachment", AttachmentIds[Position].ToString()), ("$position", Position));
                Bind.Transaction = Transaction;
                Bind.ExecuteNonQuery();
            }
            AppendCore(Id, "interaction.created", new { input = Request.Message, clientId = ClientId, attachmentIds = AttachmentIds }, Transaction);
            using var Title = Command("UPDATE ClientConversations SET Title=$title WHERE Id=$id AND Title='New conversation'", ("$title", Request.Message[..Math.Min(80, Request.Message.Length)]), ("$id", ConversationId.ToString()));
            Title.Transaction = Transaction;
            Title.ExecuteNonQuery();
            Transaction.Commit();
            return Get(Id)!;
        }
    }
    public InteractionSnapshot? Get(Guid Id, string? Owner = null)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id,ConversationId,Status,Input,Response,CreatedAt,LastSequence,RunId,CancelRequested FROM Interactions WHERE Id=$id AND ($owner IS NULL OR Owner=$owner)", ("$id", Id.ToString()), ("$owner", Owner));
            using var Reader = Query.ExecuteReader();
            return Reader.Read() ? new(Guid.Parse(Reader.GetString(0)), Guid.Parse(Reader.GetString(1)), Reader.GetString(2), Reader.GetString(3), Reader.GetString(4), DateTimeOffset.Parse(Reader.GetString(5)), Reader.GetInt64(6), Reader.IsDBNull(7) ? null : Guid.Parse(Reader.GetString(7)), Reader.GetBoolean(8)) : null;
        }
    }
    public InteractionSnapshot? ForRun(Guid RunId, string Owner)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id FROM Interactions WHERE RunId=$run AND Owner=$owner", ("$run", RunId.ToString()), ("$owner", Owner));
            return Query.ExecuteScalar() is string Id ? Get(Guid.Parse(Id), Owner) : null;
        }
    }
    public (long? InputSize, long? OutputSize, bool OutputCaptured) AudioSizes(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("""
                SELECT (SELECT length(A.Content) FROM ClientAttachments A JOIN InteractionOptions O
                            ON A.Id=json_extract(O.Payload,'$.audioAttachmentId') WHERE O.InteractionId=$id),
                    (SELECT length(Content) FROM InteractionAudio WHERE InteractionId=$id),
                    EXISTS(SELECT 1 FROM InteractionEvents WHERE InteractionId=$id AND json_extract(Payload,'$.type')='tts.audio')
                """, ("$id", Id.ToString()));
            using var Reader = Query.ExecuteReader();
            Reader.Read();
            return (Reader.IsDBNull(0) ? null : Reader.GetInt64(0), Reader.IsDBNull(1) ? null : Reader.GetInt64(1), Reader.GetBoolean(2));
        }
    }
    public IReadOnlyList<InteractionSnapshot> History(Guid ConversationId)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id FROM Interactions WHERE ConversationId=$id ORDER BY CreatedAt DESC LIMIT 100", ("$id", ConversationId.ToString()));
            var Ids = new List<Guid>();
            using (var Reader = Query.ExecuteReader()) while (Reader.Read()) Ids.Add(Guid.Parse(Reader.GetString(0)));
            return Ids.Select(Id => Get(Id)!).Reverse().ToArray();
        }
    }
    public IReadOnlyList<InteractionEvent> Events(Guid Id, long AfterSequence)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Payload FROM InteractionEvents WHERE InteractionId=$id AND Sequence>$after ORDER BY Sequence LIMIT 256", ("$id", Id.ToString()), ("$after", AfterSequence));
            using var Reader = Query.ExecuteReader();
            var Items = new List<InteractionEvent>();
            while (Reader.Read()) Items.Add(JsonSerializer.Deserialize<InteractionEvent>(Reader.GetString(0), Json)!);
            return Items;
        }
    }
    public Guid? RoutingConversation(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("""
                SELECT json_extract(Payload,'$.data.routingConversationId') FROM InteractionEvents
                WHERE InteractionId=$id AND json_extract(Payload,'$.type')='response.completed'
                ORDER BY Sequence DESC LIMIT 1
                """, ("$id", Id.ToString()));
            return Query.ExecuteScalar() is string Value && Guid.TryParse(Value, out var Conversation) ? Conversation : null;
        }
    }
    private void AppendCore(Guid Id, string Type, object Data, SqliteTransaction Transaction, string? Status = null, string? Response = null, Guid? RunId = null)
    {
        // Close unfinished model text streams on terminal paths, including recovery after a server restart.
        if (Type is "interaction.failed" or "interaction.cancelled" or "interaction.completed")
        {
            using var History = Command("SELECT Payload FROM InteractionEvents WHERE InteractionId=$id ORDER BY Sequence", ("$id", Id.ToString()));
            History.Transaction = Transaction;
            var Open = new Dictionary<(Guid StepId, string Prefix), JsonElement>();
            using (var Reader = History.ExecuteReader())
            {
                while (Reader.Read())
                {
                    var Item = JsonSerializer.Deserialize<InteractionEvent>(Reader.GetString(0), Json)!;
                    if (Item.Type is "reasoning.started" or "model.output.started")
                        Open[(Item.Data.GetProperty("stepId").GetGuid(), Item.Type[..^8])] = Item.Data;
                    if (Item.Type is "reasoning.completed" or "model.output.completed")
                        Open.Remove((Item.Data.GetProperty("stepId").GetGuid(), Item.Type[..^10]));
                }
            }
            foreach (var Item in Open)
                AppendCore(Id, Item.Key.Prefix + ".completed", new
                {
                    stepId = Item.Key.StepId,
                    modelRound = Item.Value.GetProperty("modelRound").GetInt32(),
                    status = Type == "interaction.cancelled" ? "cancelled" : "failed",
                    truncated = false
                }, Transaction);
        }
        using var Query = Command("SELECT ConversationId,LastSequence FROM Interactions WHERE Id=$id", ("$id", Id.ToString()));
        Query.Transaction = Transaction;
        Guid ConversationId;
        long Sequence;
        using (var Reader = Query.ExecuteReader())
        {
            if (!Reader.Read()) throw new InvalidOperationException("interaction_not_found");
            ConversationId = Guid.Parse(Reader.GetString(0));
            Sequence = Reader.GetInt64(1) + 1;
        }
        var Event = new InteractionEvent(Sequence, Guid.NewGuid(), Id, ConversationId, DateTimeOffset.UtcNow, Type, JsonSerializer.SerializeToElement(Data, Json));
        using var Insert = Command("INSERT INTO InteractionEvents VALUES($id,$sequence,$payload)", ("$id", Id.ToString()), ("$sequence", Event.Sequence), ("$payload", JsonSerializer.Serialize(Event, Json)));
        Insert.Transaction = Transaction;
        Insert.ExecuteNonQuery();
        using var Update = Command("UPDATE Interactions SET LastSequence=$sequence,Status=COALESCE($status,Status),Response=COALESCE($response,Response),RunId=COALESCE($run,RunId) WHERE Id=$id", ("$sequence", Event.Sequence), ("$status", Status), ("$response", Response), ("$run", RunId?.ToString()), ("$id", Id.ToString()));
        Update.Transaction = Transaction;
        Update.ExecuteNonQuery();
    }
    public void Append(Guid Id, string Type, object Data, string? Status = null, string? Response = null, Guid? RunId = null)
    {
        lock (Gate)
        {
            var Item = Get(Id);
            if (Item?.Status is "completed" or "failed" or "cancelled") return;
            if (Type == "interaction.completed" && Item?.CancelRequested == true)
            { Type = "interaction.cancelled"; Status = "cancelled"; Data = new { }; }
            using var Transaction = Database.BeginTransaction();
            AppendCore(Id, Type, Data, Transaction, Status, Response, RunId);
            Transaction.Commit();
        }
    }
    public bool Cancel(Guid Id)
    {
        lock (Gate)
        {
            var Item = Get(Id);
            if (Item is null || Item.CancelRequested || Item.Status is "completed" or "failed" or "cancelled") return false;
            using var Transaction = Database.BeginTransaction();
            using var Update = Command("UPDATE Interactions SET CancelRequested=1 WHERE Id=$id", ("$id", Id.ToString()));
            Update.Transaction = Transaction;
            Update.ExecuteNonQuery();
            AppendCore(Id, "interaction.cancel_requested", new { }, Transaction);
            Transaction.Commit();
            return true;
        }
    }
    public Guid[] Pending()
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id FROM Interactions WHERE Status='created' ORDER BY CreatedAt LIMIT 64");
            using var Reader = Query.ExecuteReader();
            var Ids = new List<Guid>();
            while (Reader.Read()) Ids.Add(Guid.Parse(Reader.GetString(0)));
            return Ids.ToArray();
        }
    }
    public void Recover()
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Id FROM Interactions WHERE Status IN ('created','transcribing','running','responding')");
            var Ids = new List<Guid>();
            using (var Reader = Query.ExecuteReader()) while (Reader.Read()) Ids.Add(Guid.Parse(Reader.GetString(0)));
            foreach (var Id in Ids) Append(Id, "interaction.failed", new ProtocolError("server_restarted", "The server restarted. Execution was not repeated.", true), "failed");
        }
    }
    public void Dispose() { lock (Gate) Database.Dispose(); }

    public SubmitInteraction Options(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Payload FROM InteractionOptions WHERE InteractionId=$id", ("$id", Id.ToString()));
            return Query.ExecuteScalar() is string Payload ? JsonSerializer.Deserialize<SubmitInteraction>(Payload, Json)! : new(Get(Id)?.Input ?? "", "legacy");
        }
    }
    public byte[]? InputAudio(Guid Id)
    {
        lock (Gate)
        {
            if (Options(Id).AudioAttachmentId is not { } AudioId) return null;
            using var Query = Command("SELECT Owner FROM Interactions WHERE Id=$id", ("$id", Id.ToString()));
            return Attachment(AudioId, (string)Query.ExecuteScalar()!)?.Content;
        }
    }
    public void SaveTranscript(Guid Id, string Text)
    {
        lock (Gate)
        {
            using var Transaction = Database.BeginTransaction();
            using var Update = Command("UPDATE Interactions SET Input=$text WHERE Id=$id", ("$text", Text), ("$id", Id.ToString()));
            Update.Transaction = Transaction;
            Update.ExecuteNonQuery();
            AppendCore(Id, "stt.final", new { text = Text }, Transaction, "running");
            Transaction.Commit();
        }
    }
    public void SaveAudio(Guid Id, byte[] Content)
    {
        lock (Gate)
        {
            if (Get(Id)?.CancelRequested == true) throw new OperationCanceledException();
            using var Transaction = Database.BeginTransaction();
            using var Insert = Command("INSERT INTO InteractionAudio VALUES($id,$content)", ("$id", Id.ToString()), ("$content", Content));
            Insert.Transaction = Transaction;
            Insert.ExecuteNonQuery();
            AppendCore(Id, "tts.audio", new { url = $"/api/client/interactions/{Id}/audio", mimeType = "audio/wav", size = Content.Length }, Transaction);
            Transaction.Commit();
        }
    }
    public byte[]? Audio(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Content FROM InteractionAudio WHERE InteractionId=$id", ("$id", Id.ToString()));
            return Query.ExecuteScalar() as byte[];
        }
    }
    public bool Playback(Guid Id, string ClientId, PlaybackReport Report) => Playback(Id, ClientId, Report, out _);

    public void SaveSegment(Guid Id, int Index, byte[] Content, AudioChunk Format)
    {
        lock (Gate)
        {
            if (Get(Id) is not { CancelRequested: false, Status: not ("completed" or "failed" or "cancelled") }) throw new OperationCanceledException();
            using var Transaction = Database.BeginTransaction();
            using var Insert = Command("INSERT INTO InteractionAudioSegments VALUES($id,$index,$content)", ("$id", Id.ToString()), ("$index", Index), ("$content", Content));
            Insert.Transaction = Transaction;
            Insert.ExecuteNonQuery();
            AppendCore(Id, "tts.segment", new SpeechSegment(Index, $"/api/client/interactions/{Id}/audio/segments/{Index}", Format.SampleRate, Format.SampleWidth, Format.Channels, Content.Length), Transaction);
            Transaction.Commit();
        }
    }
    public byte[]? Segment(Guid Id, int Index)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Content FROM InteractionAudioSegments WHERE InteractionId=$id AND SegmentIndex=$index", ("$id", Id.ToString()), ("$index", Index));
            return Query.ExecuteScalar() as byte[];
        }
    }

    public bool Playback(Guid Id, string ClientId, PlaybackReport Report, out bool Changed)
    {
        lock (Gate)
        {
            Changed = false;
            if ((Audio(Id) is null && Segment(Id, 0) is null) || Report.PlaybackId == Guid.Empty || Report.State is not ("started" or "completed" or "stopped" or "failed")) return false;
            if (Report.State == "completed" && Audio(Id) is null) return false;
            using var Query = Command("SELECT State FROM PlaybackReports WHERE InteractionId=$id AND ClientId=$client AND PlaybackId=$playback", ("$id", Id.ToString()), ("$client", ClientId), ("$playback", Report.PlaybackId.ToString()));
            var Previous = Query.ExecuteScalar() as string;
            if (Previous == Report.State) return true;
            if (Previous is null && Report.State is not ("started" or "failed") || Previous is not null && Previous != "started") return false;
            using var Transaction = Database.BeginTransaction();
            using var Write = Command("INSERT INTO PlaybackReports VALUES($id,$client,$playback,$state) ON CONFLICT(InteractionId,ClientId,PlaybackId) DO UPDATE SET State=excluded.State", ("$id", Id.ToString()), ("$client", ClientId), ("$playback", Report.PlaybackId.ToString()), ("$state", Report.State));
            Write.Transaction = Transaction;
            Write.ExecuteNonQuery();
            AppendCore(Id, "playback." + Report.State, new { clientId = ClientId, playbackId = Report.PlaybackId }, Transaction);
            Transaction.Commit();
            Changed = true;
            return true;
        }
    }

    public ClientAttachment SaveAttachment(string Owner, string Name, string MimeType, string Source, byte[] Content, string? Text, string? ClientId = null)
    {
        lock (Gate)
        {
            using var Count = Command("SELECT COALESCE(SUM(length(Content)),0) FROM ClientAttachments WHERE Owner=$owner", ("$owner", Owner));
            if ((long)Count.ExecuteScalar()! + Content.LongLength > MaxAttachmentBytes)
            {
                Prune();
                if ((long)Count.ExecuteScalar()! + Content.LongLength > MaxAttachmentBytes) throw new InvalidOperationException("attachment_quota_exceeded");
            }
            var Item = new ClientAttachment(Guid.NewGuid(), Name, MimeType, Content.Length, Source, Clock.GetUtcNow(), Text is null ? "stored_only" : "ready", ClientId);
            using var Insert = Command("INSERT INTO ClientAttachments VALUES($id,$owner,$metadata,$content,$text)", ("$id", Item.Id.ToString()), ("$owner", Owner),
                ("$metadata", JsonSerializer.Serialize(Item, Json)), ("$content", Content), ("$text", Text));
            Insert.ExecuteNonQuery();
            return Item;
        }
    }
    public bool DeleteAttachment(Guid Id, string Owner)
    {
        lock (Gate)
        {
            using var Delete = Command("""
                DELETE FROM ClientAttachments WHERE Id=$id AND Owner=$owner
                    AND NOT EXISTS (SELECT 1 FROM InteractionAttachments WHERE AttachmentId=$id)
                    AND NOT EXISTS (SELECT 1 FROM InteractionOptions WHERE json_extract(Payload,'$.audioAttachmentId')=$id)
                """, ("$id", Id.ToString()), ("$owner", Owner));
            return Delete.ExecuteNonQuery() > 0;
        }
    }
    public (ClientAttachment Metadata, byte[] Content, string? Text)? Attachment(Guid Id, string Owner)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Metadata,Content,ExtractedText FROM ClientAttachments WHERE Id=$id AND Owner=$owner", ("$id", Id.ToString()), ("$owner", Owner));
            using var Reader = Query.ExecuteReader();
            return Reader.Read() ? (JsonSerializer.Deserialize<ClientAttachment>(Reader.GetString(0), Json)!, (byte[])Reader[1], Reader.IsDBNull(2) ? null : Reader.GetString(2)) : null;
        }
    }
    public IReadOnlyList<ClientAttachment> Attachments(Guid InteractionId)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT A.Metadata FROM InteractionAttachments B JOIN ClientAttachments A ON B.AttachmentId=A.Id WHERE B.InteractionId=$id ORDER BY B.Position", ("$id", InteractionId.ToString()));
            using var Reader = Query.ExecuteReader();
            var Items = new List<ClientAttachment>();
            while (Reader.Read()) Items.Add(JsonSerializer.Deserialize<ClientAttachment>(Reader.GetString(0), Json)!);
            return Items;
        }
    }
    public IReadOnlyList<InputDocument> Documents(Guid InteractionId)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT A.Metadata,A.ExtractedText FROM InteractionAttachments B JOIN ClientAttachments A ON B.AttachmentId=A.Id WHERE B.InteractionId=$id ORDER BY B.Position", ("$id", InteractionId.ToString()));
            using var Reader = Query.ExecuteReader();
            var Items = new List<InputDocument>();
            while (Reader.Read())
            {
                var Metadata = JsonSerializer.Deserialize<ClientAttachment>(Reader.GetString(0), Json)!;
                if (!Reader.IsDBNull(1)) Items.Add(new(Metadata.Id, Metadata.Name, Reader.GetString(1), Metadata.ClientId));
            }
            return Items;
        }
    }
    public IReadOnlyList<InteractionContext> Context(Guid Id)
    {
        lock (Gate)
        {
            using var Query = Command("SELECT Payload FROM InteractionContexts WHERE InteractionId=$id ORDER BY ContextId", ("$id", Id.ToString()));
            using var Reader = Query.ExecuteReader();
            var Items = new List<InteractionContext>();
            while (Reader.Read()) Items.Add(JsonSerializer.Deserialize<InteractionContext>(Reader.GetString(0), Json)!);
            return Items;
        }
    }
    public void SelectContext(Guid Id, ContextSelection Selection)
    {
        lock (Gate)
        {
            if (Get(Id)?.Status is "completed" or "cancelled" or "failed") return;
            var Existing = Context(Id).FirstOrDefault(Item => Item.Id == Selection.Key);
            if (Existing?.ModelRounds.Contains(Selection.ModelRound) == true) return;
            var Provenance = Sanitizer.Payload(Selection.Provenance, Detailed: false, Budget: 4096);
            var Item = new InteractionContext(Selection.Key, Id, Selection.Type, Selection.Source, Sanitizer.Text(Selection.Name, 128),
                Sanitizer.Text(Selection.Content), Provenance.Value ?? JsonSerializer.SerializeToElement(new { }),
                (Existing?.ModelRounds ?? []).Append(Selection.ModelRound).Distinct().Order().ToArray(), Selection.Content.Length > 4096 || Provenance.Truncated);
            using var Transaction = Database.BeginTransaction();
            using var Insert = Command("INSERT INTO InteractionContexts VALUES($id,$context,$payload) ON CONFLICT(InteractionId,ContextId) DO UPDATE SET Payload=excluded.Payload",
                ("$id", Id.ToString()), ("$context", Item.Id), ("$payload", JsonSerializer.Serialize(Item, Json)));
            Insert.Transaction = Transaction;
            Insert.ExecuteNonQuery();
            AppendCore(Id, Existing is null ? "context.added" : "context.updated", new { Item.Id, Item.Type, Item.Source, Item.Name, Item.Provenance, Item.ModelRounds, Item.Truncated }, Transaction);
            Transaction.Commit();
        }
    }
}

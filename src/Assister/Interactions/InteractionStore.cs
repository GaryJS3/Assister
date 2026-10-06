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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public InteractionStore(IConfiguration Configuration)
    {
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
            CREATE TABLE IF NOT EXISTS ClientAttachments(Id TEXT PRIMARY KEY, Owner TEXT NOT NULL, Metadata TEXT NOT NULL, Content BLOB NOT NULL, ExtractedText TEXT);
            CREATE TABLE IF NOT EXISTS InteractionAttachments(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), AttachmentId TEXT NOT NULL REFERENCES ClientAttachments(Id), Position INTEGER NOT NULL,
                PRIMARY KEY(InteractionId,AttachmentId));
            CREATE TABLE IF NOT EXISTS InteractionContexts(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), ContextId TEXT NOT NULL, Payload TEXT NOT NULL,
                PRIMARY KEY(InteractionId,ContextId));
            CREATE TABLE IF NOT EXISTS InteractionOptions(InteractionId TEXT PRIMARY KEY REFERENCES Interactions(Id), Payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS InteractionAudio(InteractionId TEXT PRIMARY KEY REFERENCES Interactions(Id), Content BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS PlaybackReports(InteractionId TEXT NOT NULL REFERENCES Interactions(Id), ClientId TEXT NOT NULL, PlaybackId TEXT NOT NULL, State TEXT NOT NULL, PRIMARY KEY(InteractionId,ClientId,PlaybackId));
            """;
        Command.ExecuteNonQuery();
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
            var Item = new ClientConversation(Guid.NewGuid(), "New conversation", DateTimeOffset.UtcNow);
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
                if (SavedOptions.AudioAttachmentId != Request.AudioAttachmentId || SavedOptions.Speak != Request.Speak) throw new InvalidOperationException("idempotency_conflict");
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
                ("$id", Id.ToString()), ("$conversation", ConversationId.ToString()), ("$owner", Owner), ("$key", Request.IdempotencyKey), ("$input", Request.Message), ("$created", DateTimeOffset.UtcNow.ToString("O")));
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
    private void AppendCore(Guid Id, string Type, object Data, SqliteTransaction Transaction, string? Status = null, string? Response = null, Guid? RunId = null)
    {
        // Close unfinished thinking on terminal paths, including recovery after a server restart.
        if (Type is "interaction.failed" or "interaction.cancelled" or "interaction.completed")
        {
            using var History = Command("SELECT Payload FROM InteractionEvents WHERE InteractionId=$id ORDER BY Sequence", ("$id", Id.ToString()));
            History.Transaction = Transaction;
            var Open = new Dictionary<Guid, JsonElement>();
            using (var Reader = History.ExecuteReader())
            {
                while (Reader.Read())
                {
                    var Item = JsonSerializer.Deserialize<InteractionEvent>(Reader.GetString(0), Json)!;
                    if (Item.Type == "reasoning.started")
                        Open[Item.Data.GetProperty("stepId").GetGuid()] = Item.Data;
                    if (Item.Type == "reasoning.completed")
                        Open.Remove(Item.Data.GetProperty("stepId").GetGuid());
                }
            }
            foreach (var Item in Open)
                AppendCore(Id, "reasoning.completed", new
                {
                    stepId = Item.Key,
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
    public bool Playback(Guid Id, string ClientId, PlaybackReport Report)
    {
        lock (Gate)
        {
            if (Audio(Id) is null || Report.PlaybackId == Guid.Empty || Report.State is not ("started" or "completed" or "stopped" or "failed")) return false;
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
            return true;
        }
    }

    public ClientAttachment SaveAttachment(string Owner, string Name, string MimeType, string Source, byte[] Content, string? Text, string? ClientId = null)
    {
        lock (Gate)
        {
            using var Count = Command("SELECT COUNT(*) FROM ClientAttachments WHERE Owner=$owner", ("$owner", Owner));
            if ((long)Count.ExecuteScalar()! >= 100) throw new InvalidOperationException("attachment_quota_exceeded");
            var Item = new ClientAttachment(Guid.NewGuid(), Name, MimeType, Content.Length, Source, DateTimeOffset.UtcNow, Text is null ? "stored_only" : "ready", ClientId);
            using var Insert = Command("INSERT INTO ClientAttachments VALUES($id,$owner,$metadata,$content,$text)", ("$id", Item.Id.ToString()), ("$owner", Owner),
                ("$metadata", JsonSerializer.Serialize(Item, Json)), ("$content", Content), ("$text", Text));
            Insert.ExecuteNonQuery();
            return Item;
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

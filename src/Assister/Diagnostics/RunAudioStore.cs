using System.Runtime.CompilerServices;
using Assister.Contracts;
using Microsoft.Data.Sqlite;

namespace Assister.Diagnostics;

public sealed record RunAudioClip(string Direction, string State, string? Url = null, long Size = 0,
    bool Partial = false, DateTimeOffset? ExpiresAt = null);

// Binary audio is kept outside diagnostic JSON and has its own short, bounded lifetime.
public sealed class RunAudioStore : IDisposable
{
    public const int MaximumPcmBytes = 8 * 1024 * 1024;
    private readonly object Gate = new();
    private readonly SqliteConnection Database;
    private readonly TimeProvider Clock;
    private readonly ILogger<RunAudioStore> Logger;
    private readonly bool Enabled;
    private readonly int Hours;
    private readonly int Days;
    private readonly int MaxClips;
    private readonly long MaxBytes;

    public RunAudioStore(IConfiguration Configuration, ILogger<RunAudioStore> Logger, TimeProvider? Clock = null)
    {
        this.Logger = Logger;
        this.Clock = Clock ?? TimeProvider.System;
        Enabled = Configuration.GetValue("Diagnostics:CaptureAudio", true);
        Hours = Math.Clamp(Configuration.GetValue("Diagnostics:AudioRetentionHours", Configuration.GetValue("RichClients:AudioRetentionHours", 24)), 1, 168);
        Days = Math.Clamp(Configuration.GetValue("Diagnostics:RetentionDays", 7), 1, 30);
        MaxClips = 2 * Math.Clamp(Configuration.GetValue("Diagnostics:MaxRuns", 1000), 1, 1000);
        MaxBytes = Math.Clamp(Configuration.GetValue("Diagnostics:MaxAudioMiB", 256), 16, 1024) * 1024L * 1024;
        var Path = System.IO.Path.GetFullPath(Configuration["Assister:DataPath"] ?? "data");
        Directory.CreateDirectory(Path);
        Database = new(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(Path, "run-audio.db"),
            Pooling = false }.ToString());
        Database.Open();
        using var Command = Database.CreateCommand();
        Command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS RunAudio(RunId TEXT NOT NULL, Direction TEXT NOT NULL, State TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL, Partial INTEGER NOT NULL, Content BLOB,
                PRIMARY KEY(RunId,Direction));
            """;
        Command.ExecuteNonQuery();
        Prune();
    }

    public static bool ValidDirection(string Direction) => Direction is "input" or "output";

    public static byte[] Wave(ReadOnlySpan<byte> Pcm, int Rate, int Width, int Channels)
    {
        using var Stream = new MemoryStream();
        using var Writer = new BinaryWriter(Stream);
        Writer.Write("RIFF"u8); Writer.Write(Pcm.Length + 36 + (Pcm.Length & 1)); Writer.Write("WAVEfmt "u8); Writer.Write(16);
        Writer.Write((short)1); Writer.Write((short)Channels); Writer.Write(Rate); Writer.Write(Rate * Width * Channels);
        Writer.Write((short)(Width * Channels)); Writer.Write((short)(Width * 8)); Writer.Write("data"u8); Writer.Write(Pcm.Length);
        Writer.Write(Pcm); if ((Pcm.Length & 1) != 0) Writer.Write((byte)0);
        return Stream.ToArray();
    }

    public async IAsyncEnumerable<AudioChunk> ObserveAsync(Guid RunId, string Direction, IAsyncEnumerable<AudioChunk> Source,
        [EnumeratorCancellation] CancellationToken Token)
    {
        if (!Enabled || RunId == Guid.Empty)
        {
            await foreach (var Chunk in Source.WithCancellation(Token)) yield return Chunk;
            yield break;
        }
        using var Pcm = new MemoryStream();
        AudioChunk? Format = null;
        var State = "available";
        var Complete = false;
        try
        {
            await foreach (var Chunk in Source.WithCancellation(Token))
            {
                if (State == "available")
                {
                    Format ??= Chunk;
                    if (Chunk.SampleRate is < 8000 or > 96000 || Chunk.SampleWidth is < 1 or > 4 || Chunk.Channels is < 1 or > 2
                        || Chunk.SampleRate != Format.SampleRate || Chunk.SampleWidth != Format.SampleWidth || Chunk.Channels != Format.Channels
                        || Chunk.Pcm.Length % (Chunk.SampleWidth * Chunk.Channels) != 0) State = "unsupported-format";
                    else if (Pcm.Length + Chunk.Pcm.Length > MaximumPcmBytes) State = "too-large";
                    else Pcm.Write(Chunk.Pcm.Span);
                    if (State != "available") Pcm.SetLength(0);
                }
                yield return Chunk;
            }
            Complete = true;
        }
        finally
        {
            var Content = State == "available" && Pcm.Length > 0 && Format is not null
                ? Wave(Pcm.GetBuffer().AsSpan(0, (int)Pcm.Length), Format.SampleRate, Format.SampleWidth, Format.Channels) : null;
            Save(RunId, Direction, Content is null && State == "available" ? "not-captured" : State, Content, !Complete);
        }
    }

    private void Save(Guid RunId, string Direction, string State, byte[]? Content, bool Partial)
    {
        try
        {
            lock (Gate)
            {
                using var Command = Database.CreateCommand();
                Command.CommandText = "INSERT OR REPLACE INTO RunAudio VALUES($id,$direction,$state,$created,$expires,$partial,$content)";
                Command.Parameters.AddWithValue("$id", RunId.ToString()); Command.Parameters.AddWithValue("$direction", Direction);
                Command.Parameters.AddWithValue("$state", State); Command.Parameters.AddWithValue("$created", Clock.GetUtcNow().ToUnixTimeSeconds());
                Command.Parameters.AddWithValue("$expires", Clock.GetUtcNow().AddHours(Hours).ToUnixTimeSeconds());
                Command.Parameters.AddWithValue("$partial", Partial); Command.Parameters.AddWithValue("$content", (object?)Content ?? DBNull.Value);
                Command.ExecuteNonQuery();
                Prune();
            }
        }
        catch (SqliteException) { Logger.LogWarning("Run audio could not be retained for {RunId}/{Direction}.", RunId, Direction); }
    }

    public RunAudioClip Info(Guid RunId, string Direction)
    {
        lock (Gate)
        {
            using var Command = Database.CreateCommand();
            Command.CommandText = "SELECT State,ExpiresAt,Partial,length(Content) FROM RunAudio WHERE RunId=$id AND Direction=$direction";
            Command.Parameters.AddWithValue("$id", RunId.ToString()); Command.Parameters.AddWithValue("$direction", Direction);
            using var Reader = Command.ExecuteReader();
            if (!Reader.Read()) return new(Direction, Enabled ? "not-captured" : "disabled");
            var Expires = DateTimeOffset.FromUnixTimeSeconds(Reader.GetInt64(1));
            var State = Reader.GetString(0);
            if (State == "available" && (Expires <= Clock.GetUtcNow() || Reader.IsDBNull(3))) State = "expired";
            return new(Direction, State, State == "available" ? $"/api/diagnostics/runs/{RunId}/audio/{Direction}" : null,
                Reader.IsDBNull(3) ? 0 : Reader.GetInt64(3), Reader.GetBoolean(2), Expires);
        }
    }

    public byte[]? Get(Guid RunId, string Direction)
    {
        lock (Gate)
        {
            using var Command = Database.CreateCommand();
            Command.CommandText = "SELECT Content FROM RunAudio WHERE RunId=$id AND Direction=$direction AND State='available' AND ExpiresAt>$now";
            Command.Parameters.AddWithValue("$id", RunId.ToString()); Command.Parameters.AddWithValue("$direction", Direction);
            Command.Parameters.AddWithValue("$now", Clock.GetUtcNow().ToUnixTimeSeconds());
            return Command.ExecuteScalar() as byte[];
        }
    }

    public void Prune()
    {
        lock (Gate)
        {
            using var Command = Database.CreateCommand();
            Command.CommandText = """
                UPDATE RunAudio SET Content=NULL,State='expired' WHERE Content IS NOT NULL AND ExpiresAt <= $now;
                DELETE FROM RunAudio WHERE CreatedAt < $cutoff OR rowid NOT IN
                    (SELECT rowid FROM RunAudio ORDER BY CreatedAt DESC,rowid DESC LIMIT $max);
                WITH Sizes AS (SELECT rowid,SUM(length(Content)) OVER (ORDER BY CreatedAt DESC,rowid DESC) AS Total
                    FROM RunAudio WHERE Content IS NOT NULL)
                UPDATE RunAudio SET Content=NULL,State='expired' WHERE rowid IN (SELECT rowid FROM Sizes WHERE Total > $bytes);
                """;
            Command.Parameters.AddWithValue("$now", Clock.GetUtcNow().ToUnixTimeSeconds());
            Command.Parameters.AddWithValue("$cutoff", Clock.GetUtcNow().AddDays(-Days).ToUnixTimeSeconds());
            Command.Parameters.AddWithValue("$max", MaxClips); Command.Parameters.AddWithValue("$bytes", MaxBytes);
            Command.ExecuteNonQuery();
        }
    }
    public void Dispose() { lock (Gate) Database.Dispose(); }
}

public sealed class RunAudioRetentionService(RunAudioStore Audio, ILogger<RunAudioRetentionService> Logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken Token)
    {
        while (!Token.IsCancellationRequested)
        {
            try { Audio.Prune(); }
            catch (SqliteException) { Logger.LogWarning("Run audio cleanup failed; cleanup will be retried."); }
            await Task.Delay(TimeSpan.FromMinutes(5), Token);
        }
    }
}

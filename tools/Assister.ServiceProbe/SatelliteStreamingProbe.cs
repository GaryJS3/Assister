using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Assister.Contracts;
using Assister.Satellites;
using Assister.Speech.Wyoming;
using Assister.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

internal static class SatelliteStreamingProbe
{
    // Real configured TTS + real HTTP consumption. Does not claim device or acoustic timing.
    public static async Task<int> RunAsync()
    {
        var Config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var Provider = new WyomingTextToSpeechProvider(new(Config["TextToSpeech:Host"]!, Config.GetValue("TextToSpeech:Port", 10200)));
        using var Store = new VoiceAudioStore();
        var Builder = WebApplication.CreateBuilder();
        Builder.Logging.ClearProviders(); Builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var App = Builder.Build();
        App.MapGet("/audio/{id:guid}", async (Guid Id, HttpContext Context) =>
        {
            Context.Response.ContentType = "audio/wav";
            if (Store.Get(Id) is { } Data) await Context.Response.Body.WriteAsync(Data, Context.RequestAborted);
            else if (Store.GetStream(Id) is { } Stream) await Stream.WriteAsync(Context.Response.Body, Context.RequestAborted);
            else Context.Response.StatusCode = 404;
        });
        await App.StartAsync();
        using var Http = new HttpClient { BaseAddress = new(App.Urls.Single()) };
        var Results = new List<object>();
        foreach (var Phrase in new[] {
            "This is a satellite streaming speed test.",
            "This is a satellite streaming speed test. The first sentence can play while the next sentence is prepared. Audio is converted continuously without breaking sample boundaries. Playback completion still requires the matching device acknowledgement." })
        for (var Sample = 0; Sample < 10; Sample++)
        foreach (var Streaming in Sample % 2 == 0 ? new[] { false, true } : new[] { true, false })
        {
            using var Timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var Clock = Stopwatch.StartNew(); double? FirstPcm = null, SynthesisComplete = null;
            async IAsyncEnumerable<string> Sentences()
            { foreach (var Sentence in new SentenceBuffer().Append(Phrase, Final: true)) yield return Sentence; await Task.CompletedTask; }
            async IAsyncEnumerable<AudioChunk> Source([EnumeratorCancellation] CancellationToken Token = default)
            {
                var Audio = Streaming ? Provider.SynthesizeStreamAsync(Sentences(), new(Config["TextToSpeech:Voice"]), Token)
                    : Provider.SynthesizeAsync(Phrase, new(Config["TextToSpeech:Voice"]), Token);
                await foreach (var Chunk in Audio.WithCancellation(Token)) { FirstPcm ??= Clock.Elapsed.TotalMilliseconds; yield return Chunk; }
                SynthesisComplete = Clock.Elapsed.TotalMilliseconds;
            }
            var Id = Streaming ? Store.AddStream(Source(), Timeout.Token) : Store.Add(await VoiceAudioStore.Wave48kAsync(Source(), Timeout.Token));
            try
            {
                using var Response = await Http.GetAsync($"/audio/{Id}", HttpCompletionOption.ResponseHeadersRead, Timeout.Token);
                Response.EnsureSuccessStatusCode();
                await using var Body = await Response.Content.ReadAsStreamAsync(Timeout.Token);
                var Header = new byte[44]; await Body.ReadExactlyAsync(Header, Timeout.Token);
                var Buffer = new byte[8192];
                var Count = await Body.ReadAsync(Buffer, Timeout.Token);
                if (Count == 0) throw new InvalidDataException("No PCM received.");
                var FirstHttpPcm = Clock.Elapsed.TotalMilliseconds;
                var BeforeSynthesisComplete = SynthesisComplete is null;
                long Bytes = Count;
                while ((Count = await Body.ReadAsync(Buffer, Timeout.Token)) > 0) Bytes += Count;
                if (Streaming) await Store.GetStream(Id)!.Completion.WaitAsync(Timeout.Token);
                Results.Add(new { scenario = Phrase.Length < 100 ? "short" : "long", sample = Sample, streaming = Streaming,
                    firstSynthesizedPcmMs = FirstPcm, synthesisCompleteMs = SynthesisComplete, firstHttpPcmMs = FirstHttpPcm,
                    firstHttpPcmBeforeSynthesisComplete = BeforeSynthesisComplete, completeMs = Clock.Elapsed.TotalMilliseconds,
                    pcmBytes = Bytes, audioDurationMs = Bytes / 96.0 });
            }
            finally { Store.Remove(Id); }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { measurement = "Configured Wyoming TTS to first HTTP PCM; excludes model, microphone, tones, controller, speaker and acoustic onset", results = Results }, new JsonSerializerOptions { WriteIndented = true }));
        await App.StopAsync();
        return 0;
    }
}

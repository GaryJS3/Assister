using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Assister.Voice;

public sealed record TurnCompletionScore(double Probability, double FeatureMilliseconds, double InferenceMilliseconds);
public interface ITurnCompletionClassifier
{
    Task<TurnCompletionScore> ScoreAsync(ReadOnlyMemory<short> Pcm, CancellationToken Token);
}

public sealed class SmartTurnClassifier(IConfiguration Configuration) : ITurnCompletionClassifier, IDisposable
{
    private readonly SemaphoreSlim Gate = new(1);
    private InferenceSession? Session;
    public async Task<TurnCompletionScore> ScoreAsync(ReadOnlyMemory<short> Pcm, CancellationToken Token)
    {
        // One CPU inference at a time; saturation uses the caller's bounded timeout/fallback.
        await Gate.WaitAsync(Token);
        try
        {
            return await Task.Run(() =>
            {
                Token.ThrowIfCancellationRequested();
                if (Session is null)
                {
                    using var Options = new Microsoft.ML.OnnxRuntime.SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1,
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    Session = new(Configuration["Voice:Endpointing:ModelPath"] ?? Path.Combine(AppContext.BaseDirectory, "models", "smart-turn-v3.2-cpu.onnx"), Options);
                }
                var Clock = Stopwatch.StartNew();
                var Features = SmartTurnFeatures.Extract(Pcm.Span, Token);
                var FeatureMs = Clock.Elapsed.TotalMilliseconds;
                using var Run = new RunOptions();
                using var Abort = Token.Register(() => Run.Terminate = true);
                using var Input = OrtValue.CreateTensorValueFromMemory(Features, new long[] { 1, 80, 800 });
                Clock.Restart();
                using var Outputs = Session.Run(Run, new[] { "input_features" }, new[] { Input }, Session.OutputNames);
                var Probability = Outputs[0].GetTensorDataAsSpan<float>()[0];
                Token.ThrowIfCancellationRequested();
                if (!float.IsFinite(Probability) || Probability is < 0 or > 1) throw new InvalidDataException("Invalid completion probability.");
                return new TurnCompletionScore(Probability, FeatureMs, Clock.Elapsed.TotalMilliseconds);
            }, Token);
        }
        finally { Gate.Release(); }
    }
    public void Dispose() { Session?.Dispose(); Gate.Dispose(); }
}

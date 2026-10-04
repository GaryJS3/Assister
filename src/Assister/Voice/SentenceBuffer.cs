using System.Text;

namespace Assister.Voice;

// Keep punctuation until the next character arrives, so decimal points and split deltas stay intact.
public sealed class SentenceBuffer
{
    private readonly StringBuilder Pending = new();
    public IReadOnlyList<string> Append(string Delta, bool Final = false)
    {
        Pending.Append(Delta);
        var Sentences = new List<string>();
        while (Pending.Length > 0)
        {
            var End = -1;
            for (var Index = 0; Index + 1 < Pending.Length; Index++)
            {
                if (Pending[Index] is '.' or '!' or '?' && char.IsWhiteSpace(Pending[Index + 1]))
                { End = Index + 1; break; }
            }
            if (End < 0 && Pending.Length >= 512)
            {
                End = 512;
                for (var Index = 511; Index >= 256; Index--)
                    if (char.IsWhiteSpace(Pending[Index])) { End = Index; break; }
            }
            if (End < 0 && Final) { End = Pending.Length; }
            if (End < 0) { break; }
            var Sentence = Pending.ToString(0, End).Trim();
            Pending.Remove(0, End);
            if (Sentence.Length > 0) { Sentences.Add(Sentence); }
        }
        return Sentences;
    }
}

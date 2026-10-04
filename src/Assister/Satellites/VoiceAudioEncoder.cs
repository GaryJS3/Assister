using System.Diagnostics;

namespace Assister.Satellites;

public static class VoiceAudioEncoder
{
    public static async Task<byte[]> FlacAsync(byte[] Wave, CancellationToken Token)
    {
        using var Timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var Process = new Process
        {
            StartInfo = new("ffmpeg")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            }
        };
        foreach (var Argument in new[] { "-hide_banner", "-loglevel", "error", "-i", "pipe:0", "-ar", "48000", "-ac", "1", "-sample_fmt", "s16", "-f", "flac", "pipe:1" })
        { Process.StartInfo.ArgumentList.Add(Argument); }
        if (!Process.Start()) { throw new IOException("Audio encoder could not start."); }
        try
        {
            var Error = Process.StandardError.ReadToEndAsync(Timeout.Token);
            var Output = ReadOutputAsync();
            await Process.StandardInput.BaseStream.WriteAsync(Wave, Timeout.Token);
            Process.StandardInput.Close();
            await Process.WaitForExitAsync(Timeout.Token);
            var Data = await Output;
            await Error;
            if (Process.ExitCode != 0 || !Data.AsSpan().StartsWith("fLaC"u8)) { throw new IOException("Audio encoding failed."); }
            return Data;
        }
        finally { if (!Process.HasExited) { Process.Kill(entireProcessTree: true); } }

        async Task<byte[]> ReadOutputAsync()
        {
            using var Output = new MemoryStream();
            var Buffer = new byte[8192];
            int Count;
            while ((Count = await Process.StandardOutput.BaseStream.ReadAsync(Buffer, Timeout.Token)) != 0)
            {
                if (Output.Length + Count > 8 * 1024 * 1024) { throw new IOException("Encoded audio exceeds the limit."); }
                Output.Write(Buffer, 0, Count);
            }
            return Output.ToArray();
        }
    }
}

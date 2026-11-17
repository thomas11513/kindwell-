using System.ComponentModel;
using System.Diagnostics;

public sealed class SocialVideos : IDisposable
{
    private readonly SocialImages images;
    private readonly SemaphoreSlim slot = new(1, 1);

    public SocialVideos(SocialImages images) => this.images = images;

    public async Task<byte[]?> GenerateAsync(SocialImageData fundraiser, string story, Uri publicOrigin, CancellationToken cancellationToken)
    {
        if (!await slot.WaitAsync(0, cancellationToken)) return null;
        var directory = Path.Combine(Path.GetTempPath(), "kindwell-video-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var cards = await images.GenerateVideoCardsAsync(fundraiser, story, publicOrigin, cancellationToken);
            var main = Path.Combine(directory, "main.png");
            var description = Path.Combine(directory, "story.png");
            var music = Path.Combine(directory, "music.wav");
            var output = Path.Combine(directory, "video.mp4");
            await File.WriteAllBytesAsync(main, cards.Main, cancellationToken);
            await File.WriteAllBytesAsync(description, cards.Story, cancellationToken);
            await File.WriteAllBytesAsync(music, CreateMusic(cancellationToken), cancellationToken);

            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                "-loop", "1", "-framerate", "24", "-i", main,
                "-loop", "1", "-framerate", "24", "-i", description,
                "-i", music,
                "-filter_complex", "[0:v][1:v]hstack=inputs=2,crop=1080:1920:x='if(lt(t,5),0,if(lt(t,5.5),540*(1-cos(PI*(t-5)/0.5)),if(lt(t,10.5),1080,if(lt(t,11),540*(1+cos(PI*(t-10.5)/0.5)),0))))':y=0,format=yuv420p[v]",
                "-map", "[v]", "-map", "2:a", "-t", "13", "-c:a", "aac", "-b:a", "128k", "-ac", "2",
                "-af", "afade=t=in:st=0:d=1,afade=t=out:st=11.5:d=1.5",
                "-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency",
                "-crf", "26", "-threads", "2", "-movflags", "+faststart", output
            }) start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            try
            {
                if (!process.Start()) throw new IOException("Could not start the video encoder.");
            }
            catch (Win32Exception issue) { throw new IOException("Could not start the video encoder.", issue); }
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw;
            }
            var error = await errors;
            if (process.ExitCode != 0) throw new IOException("Video encoder failed: " + error);
            if (new FileInfo(output).Length > 20_000_000) throw new InvalidDataException("Generated video exceeds the size limit.");
            return await File.ReadAllBytesAsync(output, cancellationToken);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            finally { slot.Release(); }
        }
    }

    private static byte[] CreateMusic(CancellationToken cancellationToken)
    {
        const int sampleRate = 44100;
        const int sampleCount = sampleRate * 13;
        int[] melody = [72, 76, 79, 74, 76, 72, 69, 67, 69, 72, 76, 74, 72, 69, 67, 64, 67, 71, 74, 79, 76, 74, 72, 67];
        int[][] chords = [[60, 64, 67], [57, 60, 64], [53, 57, 60], [55, 59, 62]];
        static double Frequency(int note) => 440 * Math.Pow(2, (note - 69) / 12d);
        var melodyFrequencies = melody.Select(Frequency).ToArray();
        var chordFrequencies = chords.Select(chord => chord.Select(Frequency).ToArray()).ToArray();
        using var buffer = new MemoryStream(44 + sampleCount * 2);
        using var writer = new BinaryWriter(buffer);
        writer.Write("RIFF"u8);
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(sampleCount * 2);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            if (sample % sampleRate == 0) cancellationToken.ThrowIfCancellationRequested();
            var time = (double)sample / sampleRate;
            var value = 0d;
            var currentNote = (int)(time / 0.54);
            for (var note = Math.Max(0, currentNote - 3); note <= Math.Min(currentNote, melody.Length - 1); note++)
            {
                var age = time - note * 0.54;
                var envelope = Math.Min(1, age / 0.025) * Math.Exp(-2.6 * age);
                var phase = 2 * Math.PI * melodyFrequencies[note] * age;
                value += 0.15 * envelope * (Math.Sin(phase) + 0.25 * Math.Sin(phase * 2) + 0.08 * Math.Sin(phase * 3));
            }
            for (var chord = 0; chord < chords.Length; chord++)
            {
                var age = time - chord * 3.25;
                if (age < 0 || age > 3.75) continue;
                var envelope = Math.Min(1, age / 0.6) * Math.Min(1, (3.75 - age) / 0.5);
                foreach (var frequency in chordFrequencies[chord])
                {
                    var phase = 2 * Math.PI * frequency * time;
                    value += 0.035 * envelope * (Math.Sin(phase) + 0.55 * Math.Sin(phase * 1.003) + 0.2 * Math.Sin(phase * 2));
                }
            }
            writer.Write((short)(Math.Clamp(value, -1, 1) * short.MaxValue));
        }
        return buffer.ToArray();
    }

    public void Dispose() => slot.Dispose();
}

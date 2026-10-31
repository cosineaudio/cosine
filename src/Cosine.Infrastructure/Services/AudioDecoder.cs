using System.Buffers;
using System.Diagnostics;
using Cosine.Audio;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Decodes an excerpt of an audio file to the mono float PCM the analyzer expects, by driving
/// the ffmpeg that cosine already depends on for transcoding.
/// </summary>
public sealed class AudioDecoder
{
    /// <summary>
    /// How much audio to analyse. Long enough to cover several texture windows and average over
    /// a chorus and a verse; short enough that a large library finishes in hours rather than days.
    /// </summary>
    public const int ExcerptSeconds = 120;

    /// <summary>
    /// Shortest decode worth analysing. Below a second there are too few frames for the
    /// fluctuation pattern or any delta statistic to mean anything, and the vector that comes
    /// out is noise wearing the shape of a feature. Real skits and album interludes start
    /// around four seconds, so nothing legitimate is anywhere near this.
    /// </summary>
    public const double MinimumSeconds = 1.0;

    /// <summary>
    /// Fraction of the requested excerpt that has to arrive before a partial decode is treated
    /// as usable rather than as a damaged file.
    /// </summary>
    private const double MinimumFractionOfRequest = 0.1;

    private readonly string _ffmpegPath;

    public AudioDecoder(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>
    /// Reads a mono 22.05 kHz excerpt from the middle of <paramref name="filePath"/>.
    /// </summary>
    /// <param name="durationMs">
    /// Track duration if known, used to centre the excerpt. Intros, outros and lead-in silence
    /// are the least representative parts of a track, so starting at zero would systematically
    /// bias the analysis. When unknown, decoding starts at the beginning.
    /// </param>
    /// <returns>The samples, or null when ffmpeg could not decode the file.</returns>
    public async Task<float[]?> DecodeExcerptAsync(string filePath, long? durationMs, CancellationToken ct = default)
    {
        var startSeconds = 0.0;
        if (durationMs is > 0)
        {
            var totalSeconds = durationMs.Value / 1000.0;
            if (totalSeconds > ExcerptSeconds)
                startSeconds = (totalSeconds - ExcerptSeconds) / 2.0;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in BuildArgs(filePath, startSeconds))
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain stderr concurrently or ffmpeg blocks once the pipe fills.
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        float[] samples;
        try
        {
            samples = await ReadSamplesAsync(process.StandardOutput.BaseStream, ct);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        await process.WaitForExitAsync(CancellationToken.None);
        var stderr = await stderrTask;

        // A non-zero exit with samples in hand usually means a truncated or slightly damaged
        // file that still decoded most of the way; that is worth analysing. What is not worth
        // analysing is a fraction of what was asked for - a 192-second track that yields 14
        // milliseconds is a damaged file, and testing only for "nothing at all" let exactly
        // that through and indexed the resulting vector.
        // The proportional floor only applies when the duration is actually known. A track whose
        // tags gave no duration would otherwise be measured against the full 120s request, and a
        // legitimately short file would be thrown away on the strength of missing metadata.
        var decodedSeconds = (double)samples.Length / AudioAnalyzer.SampleRate;
        var floorSeconds = MinimumSeconds;
        var requestedSeconds = 0.0;

        if (durationMs is > 0)
        {
            requestedSeconds = Math.Min(ExcerptSeconds, durationMs.Value / 1000.0);
            floorSeconds = Math.Max(MinimumSeconds, requestedSeconds * MinimumFractionOfRequest);
        }

        if (decodedSeconds < floorSeconds)
            throw new InvalidOperationException(
                $"ffmpeg decoded only {decodedSeconds:F2}s"
                + (requestedSeconds > 0 ? $" of the {requestedSeconds:F0}s requested" : string.Empty)
                + $" from '{filePath}' (exit {process.ExitCode}): {stderr.Trim()}");

        return samples;
    }

    /// <param name="startSeconds">Seek target, placed before -i so ffmpeg seeks rather than decodes-and-discards.</param>
    private static IEnumerable<string> BuildArgs(string filePath, double startSeconds)
    {
        yield return "-v";
        yield return "error";
        yield return "-nostdin";

        if (startSeconds > 0)
        {
            yield return "-ss";
            yield return startSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
        }

        yield return "-t";
        yield return ExcerptSeconds.ToString();
        yield return "-i";
        yield return filePath;

        yield return "-ac";
        yield return "1";                                   // downmix to mono
        yield return "-ar";
        yield return AudioAnalyzer.SampleRate.ToString();    // resample to 22050 Hz
        yield return "-f";
        yield return "f32le";                                // raw 32-bit float samples
        yield return "-";
    }

    private static async Task<float[]> ReadSamplesAsync(Stream stdout, CancellationToken ct)
    {
        // An excerpt is a known, bounded size; over-allocating slightly beats growing a list of
        // millions of floats one element at a time.
        var capacity = AudioAnalyzer.SampleRate * ExcerptSeconds;
        var samples = new List<float>(capacity);

        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var carry = 0;
            while (true)
            {
                var read = await stdout.ReadAsync(buffer.AsMemory(carry, buffer.Length - carry), ct);
                if (read == 0) break;

                var available = carry + read;
                var whole = available / sizeof(float);

                for (var i = 0; i < whole; i++)
                    samples.Add(BitConverter.ToSingle(buffer, i * sizeof(float)));

                // A read can split a sample across chunks; keep the tail for the next pass.
                carry = available - whole * sizeof(float);
                if (carry > 0)
                    Array.Copy(buffer, whole * sizeof(float), buffer, 0, carry);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return samples.ToArray();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or we cannot signal it; nothing useful to do either way.
        }
    }
}

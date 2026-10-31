using System.Diagnostics;
using Cosine.Core.Interfaces;
using Cosine.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Scales artwork with the ffmpeg binary the app already requires for transcoding, rather than
/// pulling in an image codec (SkiaSharp needs native assets the Alpine image does not carry;
/// ImageSharp carries a commercial-use licence). A process spawn per resize is affordable
/// because <see cref="ArtworkService"/> caches the result per size - the cost is paid once per
/// (image, size), not per request.
/// </summary>
public class FfmpegImageResizer : IImageResizer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IOptions<TranscodingOptions> _options;
    private readonly ILogger<FfmpegImageResizer> _logger;

    public FfmpegImageResizer(IOptions<TranscodingOptions> options, ILogger<FfmpegImageResizer> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<byte[]?> ResizeAsync(byte[] source, int maxEdge, CancellationToken ct = default)
    {
        if (source.Length == 0 || maxEdge <= 0)
            return null;

        // Alpha survives a PNG round trip; anything else is cheaper as JPEG.
        var (codec, quality) = ImageFormats.HasTransparency(source)
            ? ("png", (string?)null)
            : ("mjpeg", "3");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.Value.FfmpegPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        // ArgumentList, not a concatenated string: cover blobs are attacker-influenced only
        // indirectly, but manual quoting is how ffmpeg option injection happens.
        var args = process.StartInfo.ArgumentList;
        args.Add("-hide_banner");
        args.Add("-loglevel"); args.Add("error");
        args.Add("-i"); args.Add("pipe:0");
        args.Add("-frames:v"); args.Add("1");            // an animated GIF is one still here
        args.Add("-vf"); args.Add(ScaleFilter(maxEdge));
        args.Add("-f"); args.Add("image2");
        args.Add("-c:v"); args.Add(codec);
        if (quality is not null) { args.Add("-q:v"); args.Add(quality); }
        args.Add("pipe:1");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            process.Start();

            var stdout = new MemoryStream();
            // stderr must be drained concurrently or ffmpeg blocks on a full pipe.
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var copyTask = process.StandardOutput.BaseStream.CopyToAsync(stdout, timeout.Token);

            await process.StandardInput.BaseStream.WriteAsync(source, timeout.Token);
            await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
            process.StandardInput.Close();

            await copyTask;
            await process.WaitForExitAsync(timeout.Token);

            if (process.ExitCode != 0 || stdout.Length == 0)
            {
                _logger.LogDebug("Artwork resize to {MaxEdge}px failed (exit {Exit}): {Error}",
                    maxEdge, process.ExitCode, await stderrTask);
                return null;
            }

            return stdout.ToArray();
        }
        catch (Exception ex)
        {
            // Never fail an image request over this - the caller serves the original.
            _logger.LogWarning(ex, "Artwork resize to {MaxEdge}px errored", maxEdge);
            TryKill(process);
            return null;
        }
        finally
        {
            process.Dispose();
        }
    }

    /// <summary>
    /// Fits the image inside a <c>maxEdge</c> square without ever enlarging it: the target box
    /// is clamped to the source's own dimensions first, so an already-small cover passes
    /// through at its native size. <c>force_divisible_by=2</c> keeps yuvj420p JPEG happy.
    /// The commas are escaped for ffmpeg's filtergraph parser, not for a shell - there is none.
    /// </summary>
    public static string ScaleFilter(int maxEdge) =>
        $"scale=w=min(iw\\,{maxEdge}):h=min(ih\\,{maxEdge})"
        + ":force_original_aspect_ratio=decrease:force_divisible_by=2";

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone, or never started.
        }
    }
}

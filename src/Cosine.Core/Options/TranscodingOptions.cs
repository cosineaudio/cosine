namespace Cosine.Core.Options;

public class TranscodingOptions
{
    public const string Section = "Transcoding";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string CachePath { get; set; } = string.Empty;
    public long MaxCacheBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB

    /// <summary>
    /// How many transcodes may be in flight at once. A slot is held for the whole song (ffmpeg
    /// blocks on the stdout pipe once the listener's buffer fills, so it costs a process and a
    /// pipe, not a core), which is why the default is a multiple of the core count rather than
    /// the core count itself. 0 = auto.
    /// </summary>
    public int MaxConcurrentTranscodes { get; set; }

    /// <summary>
    /// How long a request waits for a free transcode slot before failing. 0 = default (15s).
    /// </summary>
    public int TranscodeQueueTimeoutSeconds { get; set; }
    public Dictionary<string, int> DefaultBitrates { get; set; } = new()
    {
        ["mp3"] = 192,
        ["opus"] = 128,
        ["aac"] = 192,
        ["flac"] = 0,
        ["ogg"] = 192,
        ["wav"] = 0
    };
}

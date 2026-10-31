namespace Cosine.Core.Interfaces;

public interface IImageResizer
{
    /// <summary>
    /// Scales <paramref name="source"/> down so neither edge exceeds <paramref name="maxEdge"/>,
    /// preserving the aspect ratio. Never upscales. Returns null when the bytes could not be
    /// decoded as an image - callers should serve the original rather than fail the request.
    /// </summary>
    Task<byte[]?> ResizeAsync(byte[] source, int maxEdge, CancellationToken ct = default);
}

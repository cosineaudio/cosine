using System.Text.Json.Serialization;

namespace Cosine.Api.Generated;

/// <summary>
/// Hand-written companions to the nswag-generated types in <c>SubsonicControllers.cs</c>.
/// Regenerating never touches this file - nswag writes only <c>SubsonicControllers.cs</c>.
/// </summary>
public partial class Child
{
    /// <summary>
    /// The media file size in bytes. Set this instead of <see cref="Size"/>: the spec types
    /// <c>Child.size</c> as int32, so the generated property truncates (silently, unchecked)
    /// for files over 2 GB. Widening it in <c>tools/openapi/specs/opensubsonic-1.16.1.json</c> is not an
    /// option - the spec is upstream and stays untouched - so the widening happens in the
    /// serializer instead: <c>SubsonicOutputFormatter</c> emits <c>size</c> from this
    /// property, falling back to <see cref="Size"/> when it was left unset.
    /// </summary>
    [JsonIgnore]
    public long? FileSize { get; set; }
}

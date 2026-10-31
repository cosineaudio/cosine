namespace Cosine.Api.WebApi.Dtos;

/// <summary>
/// A linkable mention of an artist inside another object. The id is what
/// <c>GET /api/artists/{id}</c> takes; the name is the artist's canonical name, not a
/// per-track credited spelling - it has to match what the artist's own page shows.
/// </summary>
public sealed record ArtistRef(Guid Id, string Name);

/// <summary>A linkable mention of an album inside another object.</summary>
public sealed record AlbumRef(Guid Id, string Name);

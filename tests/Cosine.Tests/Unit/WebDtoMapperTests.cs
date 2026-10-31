using Cosine.Api.WebApi.Mapping;
using Cosine.Core.Entities;

namespace Cosine.Tests.Unit;

/// <summary>
/// The web API's entity → DTO mapping. What matters here is not field shuffling but the
/// contracts the client builds on: artwork ids resolve against /api/artwork, durations stay
/// in milliseconds, the credit renders the way the release printed it, and mapping never
/// aliases the (cache-shared) entity into the response.
/// </summary>
public class WebDtoMapperTests
{
    private static Track MakeTrack()
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = "Pouya", SortName = "pouya" };
        var album = new Album
        {
            Id = Guid.NewGuid(), Name = "Underground Underdog", ArtistId = artist.Id, Artist = artist
        };
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = "Suicidal Thoughts in the Back of the Cadillac",
            AlbumId = album.Id, Album = album,
            ArtistId = artist.Id, Artist = artist,
            TrackNumber = 3, DiscNumber = 1, Year = 2016,
            DurationMs = 214_000, BitRate = 320, FileSize = 3_000_000_000,
            ContentType = "audio/flac", Format = "flac",
        };
    }

    [Fact]
    public void MapsTheCoreFields()
    {
        var track = MakeTrack();

        var dto = WebDtoMapper.ToDto(track);

        Assert.Equal(track.Id, dto.Id);
        Assert.Equal(track.Title, dto.Title);
        Assert.Equal(track.ArtistId, dto.Artist!.Id);
        Assert.Equal("Pouya", dto.Artist.Name);
        Assert.Equal(track.AlbumId, dto.Album!.Id);
        Assert.Equal(214_000, dto.DurationMs);
        // Past int.MaxValue on purpose - the Subsonic spec's int32 `size` truncated this.
        Assert.Equal(3_000_000_000, dto.FileSize);
    }

    [Fact]
    public void ArtworkIdIsTheAlbumId()
    {
        var track = MakeTrack();

        Assert.Equal(track.AlbumId, WebDtoMapper.ToDto(track).ArtworkId);
    }

    [Fact]
    public void TracksWithoutCreditRowsFallBackToThePrimaryArtist()
    {
        var dto = WebDtoMapper.ToDto(MakeTrack());

        Assert.Equal("Pouya", dto.DisplayArtist);
        var only = Assert.Single(dto.Artists);
        Assert.Equal("Pouya", only.Name);
    }

    [Fact]
    public void CreditsAssembleWithTheReleasesOwnJoinPhrases()
    {
        var track = MakeTrack();
        var guest = new Artist { Id = Guid.NewGuid(), Name = "Fat Nick" };
        track.TrackArtists =
        [
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = track.ArtistId, Artist = track.Artist,
                Ordinal = 0, CreditedAs = "Pouya", JoinPhrase = " & "
            },
            new TrackArtist
            {
                TrackId = track.Id, ArtistId = guest.Id, Artist = guest,
                Ordinal = 1, CreditedAs = "Fat Nick"
            },
        ];

        var dto = WebDtoMapper.ToDto(track);

        Assert.Equal("Pouya & Fat Nick", dto.DisplayArtist);
        Assert.Equal(["Pouya", "Fat Nick"], dto.Artists.Select(a => a.Name));
        Assert.Equal([track.ArtistId, guest.Id], dto.Artists.Select(a => a.Id));
    }

    [Fact]
    public void PrefersTheRecordingMusicBrainzId()
    {
        var track = MakeTrack();
        track.MusicBrainzId = "release-track-id";
        track.MusicBrainzRecordingId = "recording-id";

        // The recording id is the only one MusicBrainz can look up.
        Assert.Equal("recording-id", WebDtoMapper.ToDto(track).MusicBrainzId);

        track.MusicBrainzRecordingId = null;
        Assert.Equal("release-track-id", WebDtoMapper.ToDto(track).MusicBrainzId);
    }

    [Fact]
    public void ToleratesUnloadedNavigations()
    {
        var track = MakeTrack();
        track.Artist = null!;
        track.Album = null!;

        var dto = WebDtoMapper.ToDto(track);

        // The refs still carry the FK ids - the name is what needed the navigation.
        Assert.Equal(track.ArtistId, dto.Artist!.Id);
        Assert.Equal(track.AlbumId, dto.Album!.Id);
    }

    [Fact]
    public void AlbumSummaryCarriesItsArtistAndArtworkId()
    {
        var track = MakeTrack();
        var album = track.Album;
        album.TrackCount = 12;
        album.DurationMs = 2_400_000;

        var dto = WebDtoMapper.ToSummaryDto(album);

        Assert.Equal(album.Id, dto.ArtworkId);
        Assert.Equal(album.ArtistId, dto.Artist!.Id);
        Assert.Equal(12, dto.TrackCount);
        Assert.Equal(2_400_000, dto.DurationMs);
    }

    [Fact]
    public void AlbumDetailMapsItsTracks()
    {
        var track = MakeTrack();
        var album = track.Album;
        album.Tracks = [track];

        var dto = WebDtoMapper.ToDetailDto(album);

        Assert.Equal(track.Id, Assert.Single(dto.Tracks).Id);
    }

    [Fact]
    public void ArtistDetailCountsAndMapsItsAlbums()
    {
        var track = MakeTrack();
        var artist = track.Artist;
        artist.Albums = [track.Album];

        var dto = WebDtoMapper.ToDetailDto(artist);

        Assert.Equal(1, dto.AlbumCount);
        Assert.Equal(artist.Id, dto.ArtworkId);
        Assert.Equal(track.Album.Id, Assert.Single(dto.Albums).Id);
    }

    /// <summary>
    /// The invariant the whole overlay design rests on: mapping constructs fresh objects and
    /// never writes user data. The entities here come out of a cache shared across users.
    /// </summary>
    [Fact]
    public void MappingNeverWritesUserDataAndUserRatingStartsNull()
    {
        var track = MakeTrack();

        var dto = WebDtoMapper.ToDto(track);
        var albumDto = WebDtoMapper.ToSummaryDto(track.Album);

        Assert.Null(dto.UserRating);
        Assert.Null(albumDto.UserRating);
    }
}

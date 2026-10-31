using System.Net;
using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cosine.Tests.Unit;

/// <summary>
/// #45: the artist-image fetch walks every image-less artist sequentially behind deliberate
/// rate-limit delays, and it runs after *every* scan. Without a negative cache, artists neither
/// MusicBrainz nor Deezer can match were re-queried against both APIs forever.
/// </summary>
public class ArtistImageServiceTests
{
    private static ArtistImageService Create(TestServiceProvider sp, CountingHandler handler)
        => new(sp.ScopeFactory, new StubHttpClientFactory(handler), sp.Cache,
            NullLogger<ArtistImageService>.Instance);

    private static async Task<Artist> SeedArtistAsync(TestServiceProvider sp, string name)
    {
        var artist = new Artist { Id = Guid.NewGuid(), Name = name, SortName = name.ToLower() };
        sp.DbContext.Artists.Add(artist);
        await sp.DbContext.SaveChangesAsync();
        return artist;
    }

    [Fact]
    public async Task UnmatchableArtists_AreNotRetriedOnTheNextRun()
    {
        using var sp = new TestServiceProvider();
        await sp.Get<SettingsService>().SetAsync("artwork.fetch_artist_images", "true");
        var artist = await SeedArtistAsync(sp, "Nobody In Particular");

        var handler = new CountingHandler();
        var service = Create(sp, handler);

        Assert.Equal(0, await service.FetchMissingImagesAsync());
        Assert.True(handler.Calls > 0);
        Assert.NotNull(await sp.Cache.GetAsync(ArtistImageService.MissKey(artist.Id)));

        var afterFirstRun = handler.Calls;
        Assert.Equal(0, await service.FetchMissingImagesAsync());

        // The second run contacted nobody: the miss marker stands in for both lookups.
        Assert.Equal(afterFirstRun, handler.Calls);
    }

    [Fact]
    public async Task FetchingDisabled_ContactsNobody()
    {
        using var sp = new TestServiceProvider();
        await SeedArtistAsync(sp, "Someone");

        var handler = new CountingHandler();

        Assert.Equal(0, await Create(sp, handler).FetchMissingImagesAsync());
        Assert.Equal(0, handler.Calls);
    }

    /// <summary>Answers every lookup with a 404, i.e. an artist neither service knows.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}

using System.Text;
using Cosine.Api.Controllers;
using Cosine.Api.Generated;
using Cosine.Api.Middleware;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace Cosine.Tests.Unit;

// search/search.view is [Obsolete] but still a live route for old clients, so exercising it
// here is the point of the file.
#pragma warning disable CS0612

/// <summary>
/// The legacy v1.0.0 <c>search</c> endpoint built its song list and then returned an empty
/// <c>&lt;searchResult/&gt;</c>, so every old client saw zero hits. The spec models
/// searchResult as an empty element, so the matches ride in the generated type's extension
/// data - which means the wire shape is worth asserting, not just the object graph.
/// </summary>
public class SearchingControllerTests
{
    private static SearchingController CreateController(FakeSearch search)
    {
        var controller = new SearchingController(search, new FakeRatingService());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static SearchResult ResultOf(ActionResult<SearchResponse> action)
    {
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<SearchResponse>(ok.Value);
        return Assert.IsType<SearchSuccessResponse>(response.SubsonicResponse).SearchResult;
    }

    private static List<Child> MatchesOf(ActionResult<SearchResponse> action)
        => Assert.IsType<List<Child>>(ResultOf(action).AdditionalProperties["match"]);

    [Fact]
    public async Task Search_ReturnsTheMatchedSongs()
    {
        var search = new FakeSearch(Track("Roads"), Track("Numb"));

        var matches = MatchesOf(await CreateController(search).Search(title: "o"));

        Assert.Equal(["Roads", "Numb"], matches.Select(m => m.Title));
        // Mapping must be populated, not just present - a client keys off id/album/artist.
        Assert.All(matches, m =>
        {
            Assert.False(string.IsNullOrEmpty(m.Id));
            Assert.Equal("Dummy", m.Album);
            Assert.Equal("Portishead", m.Artist);
            Assert.False(m.IsDir);
        });
    }

    [Fact]
    public async Task Search_HonoursOffsetAndCount()
    {
        var search = new FakeSearch(Track("a"), Track("b"), Track("c"), Track("d"));

        var matches = MatchesOf(await CreateController(search).Search(any: true, count: 2, offset: 1));

        Assert.Equal(["b", "c"], matches.Select(m => m.Title));
        // The service is asked for offset+count so the page can be sliced locally.
        Assert.Equal(3, search.RequestedTrackCount);
    }

    [Fact]
    public async Task Search_NoHits_IsAnEmptyMatchListNotAMissingOne()
    {
        var matches = MatchesOf(await CreateController(new FakeSearch()).Search(artist: "nobody"));

        Assert.Empty(matches);
    }

    [Fact]
    public async Task PostSearch_ReturnsTheSameMatchesAsTheGetForm()
    {
        var search = new FakeSearch(Track("Roads"));
        var controller = CreateController(search);

        var matches = MatchesOf(await controller.PostSearch(new Body53 { Title = "Roads", Count = 10, Offset = 0 }));

        Assert.Equal(["Roads"], matches.Select(m => m.Title));
    }

    [Fact]
    public async Task Search_XmlPutsMatchElementsInsideSearchResult()
    {
        var action = await CreateController(new FakeSearch(Track("Roads"))).Search(title: "Roads");

        var xml = await SerializeAsync(((OkObjectResult)action.Result!).Value!, format: null);

        Assert.Contains("<searchResult", xml);
        Assert.Contains("<match ", xml);
        Assert.Contains("title=\"Roads\"", xml);
        Assert.Contains("artist=\"Portishead\"", xml);
    }

    [Fact]
    public async Task Search_JsonNamesTheArrayMatch()
    {
        var action = await CreateController(new FakeSearch(Track("Roads"))).Search(title: "Roads");

        var json = await SerializeAsync(((OkObjectResult)action.Result!).Value!, format: "json");

        Assert.Contains("\"searchResult\"", json);
        Assert.Contains("\"match\"", json);
        Assert.Contains("\"Roads\"", json);
    }

    private static async Task<string> SerializeAsync(object data, string? format)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/rest/search";
        if (format is not null)
            httpContext.Request.QueryString = new QueryString($"?f={format}");
        var body = new MemoryStream();
        httpContext.Response.Body = body;

        var context = new OutputFormatterWriteContext(
            httpContext, (stream, encoding) => new StreamWriter(stream, encoding), data.GetType(), data);
        await new SubsonicOutputFormatter().WriteAsync(context);

        body.Position = 0;
        return await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
    }

    private static Core.Entities.Track Track(string title)
    {
        var artist = new Core.Entities.Artist { Id = Guid.NewGuid(), Name = "Portishead" };
        var album = new Core.Entities.Album { Id = Guid.NewGuid(), Name = "Dummy", Artist = artist, ArtistId = artist.Id };
        return new Core.Entities.Track
        {
            Id = Guid.NewGuid(),
            Title = title,
            Album = album,
            AlbumId = album.Id,
            Artist = artist,
            ArtistId = artist.Id,
            DurationMs = 180_000,
            Format = "flac"
        };
    }

    private sealed class FakeSearch : ISearchService
    {
        private readonly List<Core.Entities.Track> _tracks;

        public FakeSearch(params Core.Entities.Track[] tracks) => _tracks = [.. tracks];

        public int RequestedTrackCount { get; private set; }

        public Task<SearchResults> SearchAsync(string query, int artistCount = 20, int albumCount = 20, int trackCount = 40, CancellationToken ct = default)
        {
            RequestedTrackCount = trackCount;
            return Task.FromResult(new SearchResults([], [], _tracks));
        }

        public Task<SearchResults> BrowseAllAsync(
            int artistCount, int artistOffset, int albumCount, int albumOffset,
            int trackCount, int trackOffset, CancellationToken ct = default)
            => Task.FromResult(new SearchResults([], [], _tracks));

        public Task RebuildSearchIndexAsync(string? entityType = null, Guid? entityId = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RebuildArtistAlbumIndexAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task RebuildSearchIndexForTracksAsync(IEnumerable<Guid> trackIds, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}

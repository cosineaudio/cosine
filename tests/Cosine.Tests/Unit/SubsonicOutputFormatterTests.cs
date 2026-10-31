using System.Text;
using Cosine.Api.Middleware;
using Cosine.Api.Generated;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace Cosine.Tests.Unit;

public class SubsonicOutputFormatterTests
{
    private static GetArtistsResponse SampleResponse() => new()
    {
        SubsonicResponse = new GetArtistsSuccessResponse
        {
            Status = SubsonicSuccessResponseStatus.Ok,
            Version = "1.16.1",
            Artists = new ArtistsID3
            {
                IgnoredArticles = "The A An",
                Index = new List<IndexID3>
                {
                    new()
                    {
                        Name = "A",
                        Artist = new List<ArtistID3>
                        {
                            new() { Id = "abc", Name = "ABBA", AlbumCount = 3 }
                        }
                    }
                }
            }
        }
    };

    private static async Task<string> RunAsync(object data, string? format)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = "/rest/getArtists";
        if (format is not null)
            httpContext.Request.QueryString = new QueryString($"?f={format}");
        var body = new MemoryStream();
        httpContext.Response.Body = body;

        var formatter = new SubsonicOutputFormatter();
        var context = new OutputFormatterWriteContext(
            httpContext,
            (stream, encoding) => new StreamWriter(stream, encoding),
            data.GetType(),
            data);

        Assert.True(formatter.CanWriteResult(context));
        await formatter.WriteAsync(context);

        body.Position = 0;
        return await new StreamReader(body, Encoding.UTF8).ReadToEndAsync();
    }

    [Fact]
    public async Task Xml_ProducesSubsonicEnvelopeWithNestedElements()
    {
        var xml = await RunAsync(SampleResponse(), format: null);

        // Root envelope with attributes, not a re-wrapped/double-nested element.
        Assert.Contains("<subsonic-response", xml);
        Assert.Contains("xmlns=\"http://subsonic.org/restapi\"", xml);
        Assert.Contains("status=\"ok\"", xml);          // EnumMember value, not "Ok"
        Assert.Contains("version=\"1.16.1\"", xml);
        // Payload nested directly under the envelope.
        Assert.Contains("<artists ignoredArticles=\"The A An\"", xml);
        Assert.Contains("<index name=\"A\"", xml);
        Assert.Contains("<artist ", xml);
        Assert.Contains("id=\"abc\"", xml);
        Assert.Contains("name=\"ABBA\"", xml);
        Assert.Contains("albumCount=\"3\"", xml);
        // The C# wrapper property name must never leak into output.
        Assert.DoesNotContain("subsonicResponse", xml);
    }

    private static GetArtistResponse ArtistWithAlbums() => new()
    {
        SubsonicResponse = new GetArtistSuccessResponse
        {
            Status = SubsonicSuccessResponseStatus.Ok,
            Version = "1.16.1",
            Artist = new ArtistWithAlbumsID3
            {
                Id = "a1",
                Name = "Terror Reid",
                AlbumCount = 2,
                Album = new List<AlbumID3>
                {
                    // Nullable enum set...
                    new() { Id = "al1", Name = "One", Artist = "Terror Reid", ArtistId = "a1", ExplicitStatus = ExplicitStatus.Explicit },
                    // ...and left null (the case that previously crashed the serializer).
                    new() { Id = "al2", Name = "Two", Artist = "Terror Reid", ArtistId = "a1" }
                }
            }
        }
    };

    [Fact]
    public async Task NullableEnum_SerializesInBothFormats()
    {
        var xml = await RunAsync(ArtistWithAlbums(), format: null);
        Assert.Contains("<album ", xml);
        Assert.Contains("explicitStatus=\"explicit\"", xml);   // EnumMember value

        var json = await RunAsync(ArtistWithAlbums(), format: "json");
        Assert.Contains("\"explicitStatus\":\"explicit\"", json);
    }

    private static GetSongResponse Song(Child song) => new()
    {
        SubsonicResponse = new GetSongSuccessResponse
        {
            Status = SubsonicSuccessResponseStatus.Ok,
            Version = "1.16.1",
            Song = song
        }
    };

    [Theory]
    // A 3 GB file: the generated int32 `Size` would have wrapped to a negative number.
    [InlineData(3_221_225_472L)]
    [InlineData(long.MaxValue)]
    [InlineData(1_234L)]
    public async Task ChildSize_SerializesFileSizesBeyondInt32(long fileSize)
    {
        var song = new Child { Id = "t1", Title = "Long One", FileSize = fileSize };

        var json = await RunAsync(Song(song), format: "json");
        Assert.Contains($"\"size\":{fileSize}", json);

        var xml = await RunAsync(Song(song), format: null);
        Assert.Contains($"size=\"{fileSize}\"", xml);
    }

    [Fact]
    public async Task ChildSize_IsOmittedWhenUnset()
    {
        var json = await RunAsync(Song(new Child { Id = "t1", Title = "No Size" }), format: "json");
        Assert.DoesNotContain("\"size\"", json);

        var xml = await RunAsync(Song(new Child { Id = "t1", Title = "No Size" }), format: null);
        Assert.DoesNotContain("size=", xml);
    }

    [Fact]
    public async Task ChildSize_FallsBackToTheGeneratedIntProperty()
    {
        var json = await RunAsync(Song(new Child { Id = "t1", Title = "Small", Size = 4096 }), format: "json");
        Assert.Contains("\"size\":4096", json);
    }

    [Fact]
    public async Task Json_ProducesSubsonicResponseKeyWithoutDoubleWrapping()
    {
        var json = await RunAsync(SampleResponse(), format: "json");

        Assert.Contains("\"subsonic-response\"", json);
        Assert.Contains("\"status\":\"ok\"", json);
        Assert.Contains("\"artists\"", json);
        Assert.Contains("\"ignoredArticles\":\"The A An\"", json);
        // No re-wrapping under an underscore key or a camelCased wrapper property.
        Assert.DoesNotContain("subsonic_response", json);
        Assert.DoesNotContain("subsonicResponse", json);
    }
}

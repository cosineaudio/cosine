using Cosine.Core.Entities;
using Cosine.Infrastructure.Services;

namespace Cosine.Tests.Unit;

public class InternetRadioServiceDbTests
{
    [Fact]
    public async Task CreateStation_SetsAllProperties()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var station = await service.CreateStationAsync("Jazz FM", "https://stream.example.com/jazz", "https://jazz.example.com");

        Assert.NotEqual(Guid.Empty, station.Id);
        Assert.Equal("Jazz FM", station.Name);
        Assert.Equal("https://stream.example.com/jazz", station.StreamUrl);
        Assert.Equal("https://jazz.example.com", station.HomepageUrl);
    }

    [Fact]
    public async Task CreateStation_NoHomepageUrl()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var station = await service.CreateStationAsync("Rock Radio", "https://rock.example.com/stream");

        Assert.Equal("Rock Radio", station.Name);
        Assert.Null(station.HomepageUrl);
    }

    [Fact]
    public async Task GetStations_ReturnsAllOrderedByName()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        await service.CreateStationAsync("Zeta Radio", "https://z.example.com");
        await service.CreateStationAsync("Alpha Radio", "https://a.example.com");
        await service.CreateStationAsync("Gamma Radio", "https://g.example.com");

        var stations = await service.GetStationsAsync();

        Assert.Equal(3, stations.Count);
        Assert.Equal("Alpha Radio", stations[0].Name);
        Assert.Equal("Gamma Radio", stations[1].Name);
        Assert.Equal("Zeta Radio", stations[2].Name);
    }

    [Fact]
    public async Task GetStations_Empty_ReturnsEmptyList()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var stations = await service.GetStationsAsync();

        Assert.Empty(stations);
    }

    [Fact]
    public async Task UpdateStation_ChangesName()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var station = await service.CreateStationAsync("Old Name", "https://stream.example.com");

        await service.UpdateStationAsync(station.Id, name: "New Name");

        var stations = await service.GetStationsAsync();
        Assert.Equal("New Name", stations[0].Name);
    }

    [Fact]
    public async Task UpdateStation_ChangesStreamUrl()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var station = await service.CreateStationAsync("Test", "https://old.example.com");

        await service.UpdateStationAsync(station.Id, streamUrl: "https://new.example.com");

        var stations = await service.GetStationsAsync();
        Assert.Equal("https://new.example.com", stations[0].StreamUrl);
    }

    [Fact]
    public async Task UpdateStation_NonExistent_Throws()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateStationAsync(Guid.NewGuid(), name: "Test"));
    }

    [Fact]
    public async Task UpdateStation_NullValues_DoesNotChangeExisting()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var station = await service.CreateStationAsync("Keep Name", "https://keep.example.com", "https://home.example.com");

        await service.UpdateStationAsync(station.Id, streamUrl: "https://changed.example.com");

        var stations = await service.GetStationsAsync();
        Assert.Equal("Keep Name", stations[0].Name);
        Assert.Equal("https://changed.example.com", stations[0].StreamUrl);
        Assert.Equal("https://home.example.com", stations[0].HomepageUrl);
    }

    [Fact]
    public async Task DeleteStation_RemovesRecord()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        var station = await service.CreateStationAsync("To Delete", "https://delete.example.com");

        await service.DeleteStationAsync(station.Id);

        var stations = await service.GetStationsAsync();
        Assert.Empty(stations);
    }

    [Fact]
    public async Task DeleteStation_NonExistent_DoesNotThrow()
    {
        using var db = TestDbContextFactory.Create();
        var service = new InternetRadioService(db);

        await service.DeleteStationAsync(Guid.NewGuid());

        Assert.True(true);
    }
}

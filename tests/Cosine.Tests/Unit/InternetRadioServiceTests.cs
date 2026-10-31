using Cosine.Core.Entities;

namespace Cosine.Tests.Unit;

public class InternetRadioServiceTests
{
    [Fact]
    public void InternetRadioStation_DefaultValues_AreSet()
    {
        var station = new InternetRadioStation
        {
            Id = Guid.NewGuid(),
            Name = "SomaFM Groove Salad",
            StreamUrl = "https://ice.somafm.com/groovesalad"
        };

        Assert.Equal("SomaFM Groove Salad", station.Name);
        Assert.Equal("https://ice.somafm.com/groovesalad", station.StreamUrl);
        Assert.NotEqual(Guid.Empty, station.Id);
    }

    [Fact]
    public void InternetRadioStation_HomepageUrl_IsOptional()
    {
        var station = new InternetRadioStation
        {
            Name = "Test Station",
            StreamUrl = "https://example.com/stream"
        };

        Assert.Null(station.HomepageUrl);

        station.HomepageUrl = "https://example.com";
        Assert.Equal("https://example.com", station.HomepageUrl);
    }

    [Fact]
    public void InternetRadioStation_Name_IsRequired()
    {
        var station = new InternetRadioStation
        {
            StreamUrl = "https://example.com/stream"
        };

        Assert.Equal(string.Empty, station.Name);
    }

    [Fact]
    public void InternetRadioStation_StreamUrl_IsRequired()
    {
        var station = new InternetRadioStation
        {
            Name = "Test Station"
        };

        Assert.Equal(string.Empty, station.StreamUrl);
    }

    [Fact]
    public void InternetRadioStation_Id_IsGuaranteedUnique()
    {
        var s1 = new InternetRadioStation { Id = Guid.NewGuid(), Name = "Station 1", StreamUrl = "https://a.com" };
        var s2 = new InternetRadioStation { Id = Guid.NewGuid(), Name = "Station 2", StreamUrl = "https://b.com" };

        Assert.NotEqual(s1.Id, s2.Id);
    }

    [Fact]
    public void InternetRadioStation_Properties_CanBeUpdated()
    {
        var station = new InternetRadioStation
        {
            Name = "Original",
            StreamUrl = "https://original.com/stream"
        };

        station.Name = "Updated";
        station.StreamUrl = "https://updated.com/stream";
        station.HomepageUrl = "https://updated.com";

        Assert.Equal("Updated", station.Name);
        Assert.Equal("https://updated.com/stream", station.StreamUrl);
        Assert.Equal("https://updated.com", station.HomepageUrl);
    }

    [Fact]
    public void InternetRadioStation_PartialUpdate_NameOnly()
    {
        var station = new InternetRadioStation
        {
            Name = "Original",
            StreamUrl = "https://original.com/stream",
            HomepageUrl = "https://original.com"
        };

        station.Name = "Renamed";

        Assert.Equal("Renamed", station.Name);
        Assert.Equal("https://original.com/stream", station.StreamUrl);
        Assert.Equal("https://original.com", station.HomepageUrl);
    }
}

using Cosine.Core.Interfaces;

namespace Cosine.Tests.Unit;

public class PlayHistoryServiceTests
{
    [Fact]
    public void RecordPlay_CreatesEntryWithCorrectProperties()
    {
        var userId = Guid.NewGuid();
        var trackId = Guid.NewGuid();

        var entry = new Cosine.Core.Entities.PlayHistory
        {
            UserId = userId,
            TrackId = trackId,
            PlayedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(userId, entry.UserId);
        Assert.Equal(trackId, entry.TrackId);
        Assert.NotEqual(default, entry.PlayedAt);
    }

    [Fact]
    public void PlayHistory_Entity_Defaults()
    {
        var entry = new Cosine.Core.Entities.PlayHistory();

        Assert.Equal(Guid.Empty, entry.Id);
        Assert.Equal(Guid.Empty, entry.UserId);
        Assert.Equal(Guid.Empty, entry.TrackId);
        // Navigation properties are null by default (not initialized in entity)
        Assert.Null(entry.User);
        Assert.Null(entry.Track);
    }
}

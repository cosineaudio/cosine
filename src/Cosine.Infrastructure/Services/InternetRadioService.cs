using Cosine.Core.Entities;
using Cosine.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cosine.Infrastructure.Services;

public class InternetRadioService : IInternetRadioService
{
    private readonly Data.CosineDbContext _db;

    public InternetRadioService(Data.CosineDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<InternetRadioStation>> GetStationsAsync(CancellationToken ct = default)
    {
        return await _db.InternetRadioStations
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
    }

    public async Task<InternetRadioStation> CreateStationAsync(string name, string streamUrl, string? homepageUrl = null, CancellationToken ct = default)
    {
        var station = new InternetRadioStation
        {
            Name = name,
            StreamUrl = streamUrl,
            HomepageUrl = homepageUrl
        };

        _db.InternetRadioStations.Add(station);
        await _db.SaveChangesAsync(ct);
        return station;
    }

    public async Task UpdateStationAsync(Guid id, string? name = null, string? streamUrl = null, string? homepageUrl = null, CancellationToken ct = default)
    {
        var station = await _db.InternetRadioStations.FindAsync([id], ct)
            ?? throw new InvalidOperationException("Station not found");

        if (name is not null)
            station.Name = name;
        if (streamUrl is not null)
            station.StreamUrl = streamUrl;
        if (homepageUrl is not null)
            station.HomepageUrl = homepageUrl;

        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteStationAsync(Guid id, CancellationToken ct = default)
    {
        var station = await _db.InternetRadioStations.FindAsync([id], ct);
        if (station is not null)
        {
            _db.InternetRadioStations.Remove(station);
            await _db.SaveChangesAsync(ct);
        }
    }
}

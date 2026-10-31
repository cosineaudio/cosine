using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public interface IInternetRadioService
{
    Task<IReadOnlyList<InternetRadioStation>> GetStationsAsync(CancellationToken ct = default);
    Task<InternetRadioStation> CreateStationAsync(string name, string streamUrl, string? homepageUrl = null, CancellationToken ct = default);
    Task UpdateStationAsync(Guid id, string? name = null, string? streamUrl = null, string? homepageUrl = null, CancellationToken ct = default);
    Task DeleteStationAsync(Guid id, CancellationToken ct = default);
}

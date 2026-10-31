namespace Cosine.Core.Interfaces;

public interface ISettingsService
{
    /// <summary>All known setting keys with their default values.</summary>
    IReadOnlyDictionary<string, string> Defaults { get; }

    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task SeedDefaultsAsync(CancellationToken ct = default);
}

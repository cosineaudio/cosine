using Cosine.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Transparent decorator over the real <see cref="IDistributedCache"/> that records a
/// hit or miss on every read. A read returning null is a miss; anything else is a hit.
/// This is the single choke point for cache-hit statistics - the read-through
/// GetStringAsync/GetAsync callers throughout the services flow through here unchanged.
/// </summary>
public class MetricsDistributedCache : IDistributedCache
{
    private readonly IDistributedCache _inner;
    private readonly IMetricsService _metrics;

    public MetricsDistributedCache(IDistributedCache inner, IMetricsService metrics)
    {
        _inner = inner;
        _metrics = metrics;
    }

    public byte[]? Get(string key)
    {
        var value = _inner.Get(key);
        Record(value);
        return value;
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken token = default)
    {
        var value = await _inner.GetAsync(key, token);
        Record(value);
        return value;
    }

    private void Record(byte[]? value)
    {
        if (value is null)
            _metrics.RecordCacheMiss();
        else
            _metrics.RecordCacheHit();
    }

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        => _inner.Set(key, value, options);

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        => _inner.SetAsync(key, value, options, token);

    public void Refresh(string key) => _inner.Refresh(key);
    public Task RefreshAsync(string key, CancellationToken token = default) => _inner.RefreshAsync(key, token);
    public void Remove(string key) => _inner.Remove(key);
    public Task RemoveAsync(string key, CancellationToken token = default) => _inner.RemoveAsync(key, token);
}

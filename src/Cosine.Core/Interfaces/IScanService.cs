using Cosine.Core.Entities;

namespace Cosine.Core.Interfaces;

public record ScanProgress(string Phase, int Current, int Total, string? CurrentFile);
public record ScanStatus(Guid? ScanLogId, string Status, ScanProgress? Progress);

public interface IScanService
{
    Task<Guid> StartScanAsync(CancellationToken ct = default);
    Task<ScanStatus> GetScanStatusAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScanLog>> GetScanLogsAsync(int limit = 20, CancellationToken ct = default);
}

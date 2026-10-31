using NpgsqlTypes;

namespace Cosine.Core.Entities;

public class SearchIndex
{
    public string EntityType { get; set; } = string.Empty;
    public Guid EntityId { get; set; }
    public NpgsqlTsVector TextVector { get; set; } = null!;
    public NpgsqlTsVector? LyricsVector { get; set; }
}

namespace Cosine.Core.Entities;

/// <summary>
/// A user's 1–5 rating of one library item. A rating belongs to a (user, item) pair, which a
/// column on the item row cannot express - so this is a join table shaped exactly like
/// <see cref="Star"/>, and carries the same caveat: only <see cref="UserId"/> is a real foreign
/// key. <see cref="ItemId"/> is polymorphic, discriminated by <see cref="ItemType"/>, so deleting
/// a track leaves its rating rows behind just as it leaves its stars behind. Reads that enumerate
/// ratings therefore have to *join* the target table rather than filter on it.
/// </summary>
public class Rating
{
    public Guid UserId { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public Guid ItemId { get; set; }

    /// <summary>1–5. A rating of 0 in the Subsonic API means "clear", and deletes the row.</summary>
    public int Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
}

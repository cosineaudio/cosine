using Cosine.Playback.Upnp.Description;

namespace Cosine.Playback.Upnp.Profiles;

/// <summary>
/// The profiles cosine knows, in the order they are tried, plus the two questions the driver
/// asks of them: what to search for, and which profile owns a device it has just described.
/// <para>
/// Order is the whole mechanism. Specialisations come first and <see cref="GenericProfile"/>
/// last, so the first match wins and the fallback can safely claim everything. Adding hardware
/// means adding a profile to this list - never a conditional somewhere downstream (plan §1.4).
/// </para>
/// </summary>
public sealed class RendererProfileCatalog
{
    private readonly IRendererProfile[] _profiles;

    /// <summary>The profiles cosine ships: Sonos, then the generic fallback.</summary>
    public RendererProfileCatalog()
        : this(new SonosProfile(), new GenericProfile())
    {
    }

    /// <param name="profiles">
    /// Specialisations first, fallback last. The last entry must match any device - it is what
    /// <see cref="Select"/> returns when nothing else claims one, and the contract is that
    /// selection never comes back empty.
    /// </param>
    public RendererProfileCatalog(params IRendererProfile[] profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Length == 0)
        {
            throw new ArgumentException("A catalogue needs at least a fallback profile.", nameof(profiles));
        }

        _profiles = (IRendererProfile[])profiles.Clone();

        // De-duplicated because profiles overlap on purpose - Sonos lists the generic renderer
        // target as well as its own - and sending the same M-SEARCH twice would double the
        // responses the driver has to reconcile for nothing. Order is preserved so the most
        // specific target still goes out first.
        SearchTargets = _profiles
            .SelectMany(p => p.SearchTargets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The profiles in match order.</summary>
    public IReadOnlyList<IRendererProfile> Profiles => _profiles;

    /// <summary>
    /// Every search target any profile needs answered, each one once. This is what the driver
    /// sends <c>M-SEARCH</c> for; it lives here rather than in the SSDP code so that adding a
    /// profile is genuinely all it takes to make its hardware discoverable.
    /// </summary>
    public IReadOnlyList<string> SearchTargets { get; }

    /// <summary>
    /// The profile that owns <paramref name="description"/>. Never null: the last profile is the
    /// fallback and takes anything unclaimed, so a device always has somewhere to belong.
    /// </summary>
    public IRendererProfile Select(DeviceDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        foreach (var profile in _profiles)
        {
            if (profile.Matches(description))
            {
                return profile;
            }
        }

        return _profiles[^1];
    }
}

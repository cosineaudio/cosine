using System.Globalization;

namespace Cosine.Playback.Upnp.Soap;

/// <summary>
/// The UPnP AV time format, both directions. Pure, and deliberately unforgiving in one direction
/// only: <see cref="Format"/> emits exactly one shape because <c>Seek</c> is picky, while
/// <see cref="TryParse"/> takes everything renderers actually send.
/// </summary>
/// <remarks>
/// The spec says <c>H+:MM:SS[.F+|.F0/F1]</c>. Devices in the field send that plus a handful of
/// things that are not a time at all - <c>NOT_IMPLEMENTED</c> from a renderer that does not track
/// position, an empty element from one that has not started yet, and occasionally a raw number of
/// seconds. Every one of those means "I do not know", which is a meaningful answer:
/// <see cref="Abstractions.TransportState.Duration"/> being null tells the engine not to trust a
/// duration, whereas a fabricated zero tells it the track just ended.
/// </remarks>
public static class UpnpTime
{
    /// <summary>What a renderer sends when the action exists but the value does not.</summary>
    private const string NotImplemented = "NOT_IMPLEMENTED";

    /// <summary>
    /// Sanity ceiling on the hours field. Nothing legitimate is a four-figure number of hours, and
    /// without a bound a garbage value overflows <see cref="TimeSpan"/> construction - which would
    /// be a throw out of a method whose whole contract is that it never throws.
    /// </summary>
    private const int MaxHours = 10_000;

    /// <summary>
    /// Parse a UPnP time, or return null if it is not one.
    /// </summary>
    /// <returns>
    /// Null for null, empty, whitespace, <c>NOT_IMPLEMENTED</c>, a negative value, and anything
    /// else that is not <c>H+:MM:SS</c> with an optional fraction. Never throws - this runs inside
    /// the engine's ~1 Hz poll loop, and one confused renderer must not be able to tear it down.
    /// </returns>
    public static TimeSpan? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();

        if (text.Equals(NotImplemented, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Rejected rather than negated. A negative position or duration is not a real reading, and
        // handing one to the engine would put the progress bar somewhere impossible.
        if (text[0] == '-')
        {
            return null;
        }

        var parts = text.Split(':');
        if (parts.Length != 3)
        {
            // Two-part "MM:SS" is deliberately not accepted: "1:30" is ambiguous between a minute
            // and a half and an hour and a half, and guessing wrong is worse than reporting
            // unknown.
            return null;
        }

        if (!TryParseField(parts[0], MaxHours, out var hours) ||
            !TryParseField(parts[1], 59, out var minutes))
        {
            return null;
        }

        if (!TryParseSeconds(parts[2], out var seconds))
        {
            return null;
        }

        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + seconds;
    }

    /// <summary>
    /// Render a position as <c>H:MM:SS</c> - unpadded hours, zero-padded minutes and seconds -
    /// which is the form <c>Seek</c> with <c>Unit=REL_TIME</c> is documented to take and the form
    /// the widest set of renderers accepts. Sub-second precision is dropped: no renderer seeks
    /// more accurately than a second, and a fraction is one more thing for a strict parser on the
    /// far end to reject.
    /// </summary>
    /// <param name="value">Clamped up to zero, since there is nowhere before the start of a track.</param>
    public static string Format(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var total = (long)value.TotalSeconds;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{hours}:{minutes:00}:{seconds:00}");
    }

    private static bool TryParseField(string text, int max, out int result)
    {
        result = 0;

        // NumberStyles.None rejects signs, decimal points, thousands separators and surrounding
        // whitespace, so "1e3" and "+4" and " 7" all fail here rather than silently becoming a
        // number that was never sent.
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (parsed > max)
        {
            return false;
        }

        result = parsed;
        return true;
    }

    /// <summary>
    /// The seconds field, which carries the optional fraction. Three shapes are in the wild:
    /// plain <c>SS</c>, decimal <c>SS.mmm</c>, and the spec's own <c>SS.F0/F1</c> ratio, where the
    /// text after the point is a fraction with a slash in it rather than a decimal expansion.
    /// </summary>
    private static bool TryParseSeconds(string text, out TimeSpan result)
    {
        result = TimeSpan.Zero;

        var dot = text.IndexOf('.');
        var whole = dot < 0 ? text : text[..dot];

        if (!TryParseField(whole, 59, out var seconds))
        {
            return false;
        }

        result = TimeSpan.FromSeconds(seconds);

        if (dot < 0)
        {
            return true;
        }

        var fraction = text[(dot + 1)..];
        if (fraction.Length == 0)
        {
            // A trailing "." with nothing after it. Odd but harmless; the whole seconds are still
            // a good reading, and refusing the whole value over it would lose real information.
            return true;
        }

        var slash = fraction.IndexOf('/');
        if (slash >= 0)
        {
            if (!TryParseField(fraction[..slash], int.MaxValue, out var numerator) ||
                !TryParseField(fraction[(slash + 1)..], int.MaxValue, out var denominator) ||
                denominator == 0 ||
                numerator >= denominator)
            {
                return false;
            }

            result += TimeSpan.FromSeconds((double)numerator / denominator);
            return true;
        }

        if (!TryParseField(fraction, int.MaxValue, out _))
        {
            return false;
        }

        if (!double.TryParse(
                "0." + fraction,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var decimalFraction))
        {
            return false;
        }

        result += TimeSpan.FromSeconds(decimalFraction);
        return true;
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Cosine.Infrastructure.Services;

/// <summary>One artist in a track's credit, in credit order.</summary>
/// <param name="Name">The credited name.</param>
/// <param name="JoinPhrase">
/// Literal text between this credit and the next - empty on the last. Kept verbatim so
/// re-rendering the credit reproduces what the tag actually said.
/// </param>
public readonly record struct ParsedCredit(string Name, string JoinPhrase);

/// <summary>
/// Recovers a multi-artist credit from what a file's tags actually carry. This is the offline
/// half of multi-artist support: it costs nothing and needs no network, but it can only find
/// what the tagger wrote down. Libraries managed by Lidarr have the credit stripped before
/// Cosine ever sees the file - every track's ARTIST frame holds the *release* artist - so for
/// those the accurate source is <see cref="MusicBrainzCreditService"/>, not this.
/// <para>
/// Deliberately conservative. Every rule here has to hold for artists whose names contain the
/// very characters a naive splitter keys on - "AC/DC", "Simon &amp; Garfunkel", "Tyler, The
/// Creator" - so this never splits on "/" or on a bare comma, and only ever treats
/// "feat."/"ft."/"featuring" as a separator when it is surrounded by whitespace. Under-reading
/// a credit leaves today's behaviour; over-reading invents artists that do not exist.
/// </para>
/// </summary>
public static partial class ArtistCreditParsing
{
    /// <summary>
    /// A featuring marker used as an inline separator: " feat. ", " ft ", " featuring ".
    /// Leading whitespace is required, so an artist called "Ft. Lauderdale Sound" is not a
    /// match. The trailing side also accepts end-of-string, which is how a dangling
    /// "Skrillex feat." is recognised as a marker with no guest rather than part of the name.
    /// </summary>
    [GeneratedRegex(@"\s+(?:feat\.?|ft\.?|featuring)(?:\s+|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FeatureSeparator();

    /// <summary>
    /// A trailing "(feat. X)" / "[featuring X]" / "(with X)" on a title. Anchored to the end and
    /// required to be bracketed - an unbracketed "with" appears in ordinary titles ("Grooving
    /// With a Pict") and is not a credit.
    /// </summary>
    [GeneratedRegex(@"[\(\[]\s*(?:feat\.?|ft\.?|featuring|with)\s+(?<names>[^\)\]]+?)\s*[\)\]]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleFeatureSuffix();

    /// <summary>
    /// Separators inside one featuring clause. " &amp; " and " and " only: a bare comma cannot be
    /// used because "Tyler, The Creator" is one artist, and splitting on it would mint a second.
    /// A comma-separated guest list therefore stays a single credit rather than a wrong pair.
    /// </summary>
    [GeneratedRegex(@"\s+(?:&|and)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GuestSeparator();

    /// <summary>Separators between values of a multi-valued ARTISTS tag.</summary>
    private static readonly char[] ListSeparators = ['\0', ';'];

    /// <summary>
    /// The credit for one track. <paramref name="artistName"/> is the ARTIST tag (always the
    /// first credit), <paramref name="artistsTag"/> the optional multi-valued ARTISTS tag, and
    /// <paramref name="title"/> the track title, mined for a trailing "(feat. …)".
    /// <para>
    /// Always returns at least one credit, so a single-artist track still produces a row and the
    /// join table is never partially populated.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ParsedCredit> Parse(string artistName, string? artistsTag = null, string? title = null)
    {
        var primary = artistName.Trim();
        if (primary.Length == 0)
            return [];

        // An explicit ARTISTS list is the tagger stating the credit outright, so it wins. The
        // ARTIST string is still consulted for the join phrases, which ARTISTS does not carry.
        var listed = SplitList(artistsTag);
        var credits = listed.Count > 1
            ? AlignToDisplay(primary, listed)
            : SplitOnFeatureMarkers(primary);

        // A title suffix names guests that the artist fields may not. Appended rather than
        // merged: the ARTIST string is the authority on who comes first.
        foreach (var guest in FeaturedFromTitle(title))
        {
            if (credits.Any(c => string.Equals(c.Name, guest, StringComparison.OrdinalIgnoreCase)))
                continue;

            // The credit before it stops being last, so it needs a join phrase.
            credits[^1] = credits[^1] with { JoinPhrase = credits.Count == 1 ? " feat. " : ", " };
            credits.Add(new ParsedCredit(guest, string.Empty));
        }

        return credits;
    }

    /// <summary>
    /// Renders a credit back to its display string - "Pouya &amp; Fat Nick". The inverse of
    /// <see cref="Parse"/> for anything it parsed out of a single ARTIST tag.
    /// </summary>
    public static string Render(IEnumerable<ParsedCredit> credits)
    {
        var sb = new StringBuilder();
        foreach (var credit in credits)
            sb.Append(credit.Name).Append(credit.JoinPhrase);
        return sb.ToString();
    }

    /// <summary>
    /// Renders a stored credit for display - "Pouya &amp; Fat Nick". Shared by every surface that
    /// shows an artist line (the Subsonic mapping, the remote playback queue) so a track cannot
    /// read one way in the browser and another on a speaker.
    /// <para>
    /// A join phrase missing from a non-final credit becomes ", ": MusicBrainz always supplies
    /// one, but a hand-written or migrated row might not, and running two names together is
    /// worse than guessing the separator.
    /// </para>
    /// </summary>
    /// <param name="fallback">Used when there is no credit at all - the primary artist's name.</param>
    public static string Display(IReadOnlyList<(string CreditedAs, string JoinPhrase)> credits, string fallback)
    {
        if (credits.Count == 0)
            return fallback;

        var sb = new StringBuilder();
        for (var i = 0; i < credits.Count; i++)
        {
            sb.Append(credits[i].CreditedAs);
            if (i == credits.Count - 1)
                continue;

            sb.Append(credits[i].JoinPhrase is { Length: > 0 } join ? join : ", ");
        }

        return sb.ToString();
    }

    /// <summary>Values of a multi-valued tag, in order, empties dropped.</summary>
    private static List<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Splits an ARTIST string on featuring markers, keeping each matched marker as the join
    /// phrase so <see cref="Render"/> reproduces the original exactly. A string with no marker
    /// comes back as a single credit.
    /// </summary>
    private static List<ParsedCredit> SplitOnFeatureMarkers(string display)
    {
        var credits = new List<ParsedCredit>();
        var cursor = 0;

        foreach (Match match in FeatureSeparator().Matches(display))
        {
            var name = display[cursor..match.Index].Trim();
            // A marker at the very start, or two in a row, leaves nothing to credit.
            if (name.Length == 0)
                continue;

            credits.Add(new ParsedCredit(name, match.Value));
            cursor = match.Index + match.Length;
        }

        // No usable marker: the whole ARTIST string is one name. Note what does *not* happen
        // here - it is not split on "&", because "Simon & Garfunkel" is a single artist.
        if (credits.Count == 0)
            return [new ParsedCredit(display.Trim(), string.Empty)];

        // Guests behind a marker may themselves be a list: "feat. A & B". Splitting on "&" is
        // safe on this side of a marker, where the clause is already known to be a guest list.
        var guests = SplitGuests(display[cursor..].Trim());
        foreach (var guest in guests)
            credits.Add(new ParsedCredit(guest, " & "));

        // Either a dangling marker ("Skrillex feat.") or the real end of the list.
        credits[^1] = credits[^1] with { JoinPhrase = string.Empty };
        return credits;
    }

    /// <summary>
    /// Turns an ordered name list into a credit, recovering each join phrase from the display
    /// string when every name occurs in it in order. That is the case for anything Picard wrote
    /// (ARTIST is the rendered form of ARTISTS), and it preserves " &amp; " vs " feat. " exactly.
    /// Falls back to " &amp; " when the display string does not line up - a retagged ARTIST, say.
    /// </summary>
    private static List<ParsedCredit> AlignToDisplay(string display, List<string> names)
    {
        var credits = new List<ParsedCredit>(names.Count);
        var cursor = 0;
        var aligned = true;

        for (var i = 0; i < names.Count && aligned; i++)
        {
            var at = display.IndexOf(names[i], cursor, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                aligned = false;
                break;
            }

            var end = at + names[i].Length;
            if (i > 0)
                // The text between the previous name and this one is that name's join phrase.
                credits[^1] = credits[^1] with { JoinPhrase = display[cursor..at] };

            credits.Add(new ParsedCredit(names[i], string.Empty));
            cursor = end;
        }

        if (aligned && credits.Count == names.Count)
            return credits;

        return names
            .Select((name, i) => new ParsedCredit(name, i == names.Count - 1 ? string.Empty : " & "))
            .ToList();
    }

    /// <summary>Guest names from a trailing "(feat. …)" on the title, or nothing.</summary>
    private static List<string> FeaturedFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return [];

        var match = TitleFeatureSuffix().Match(title);
        return match.Success ? SplitGuests(match.Groups["names"].Value) : [];
    }

    /// <summary>Names inside one featuring clause. See <see cref="GuestSeparator"/> on commas.</summary>
    private static List<string> SplitGuests(string clause)
        => GuestSeparator()
            .Split(clause)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .ToList();
}

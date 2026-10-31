using System.Xml.Linq;

namespace Cosine.Playback.Upnp.Profiles;

/// <summary>
/// One Sonos zone group: a set of players that share a transport, plus the one that owns it.
/// </summary>
/// <param name="Id">
/// The group's own id, e.g. <c>RINCON_000E58F1234501400:12</c>. Diagnostics only - the id
/// changes every time the group is rebuilt, so nothing may key off it.
/// </param>
/// <param name="CoordinatorUuid">
/// The member that actually holds the queue and answers transport commands, in bare
/// <c>RINCON_…</c> form as the topology document spells it.
/// </param>
/// <param name="Members">Every player in the group, the coordinator included.</param>
public sealed record ZoneGroup(string Id, string CoordinatorUuid, IReadOnlyList<ZoneGroupMember> Members);

/// <summary>One player inside a <see cref="ZoneGroup"/>.</summary>
/// <param name="Uuid">Bare <c>RINCON_…</c> form, without the <c>uuid:</c> prefix a UPnP UDN carries.</param>
/// <param name="Location">
/// Where that player's device description lives, e.g.
/// <c>http://192.168.1.55:1400/xml/device_description.xml</c>. This is the only place the
/// topology tells us a member's address, so it is how a coordinator's host is recovered. Null
/// when the attribute is missing or unparseable, which makes the member unusable as a command
/// target.
/// </param>
/// <param name="ZoneName">What the player calls itself, e.g. "Living Room". Diagnostics only.</param>
public sealed record ZoneGroupMember(string Uuid, Uri? Location, string? ZoneName);

/// <summary>
/// Reads the document Sonos returns from <c>ZoneGroupTopology.GetZoneGroupState</c>.
/// <para>
/// Deliberately pure - a string in, records out, no SOAP and no HTTP - because the interesting
/// failure modes here are all shapes of document (a grouped pair, a standalone player, garbage,
/// a device that is not in the topology at all) and they are only cheap to test if reading the
/// document is separable from fetching it.
/// </para>
/// <para>
/// Everything in this file is Sonos-specific and stays that way: it and
/// <see cref="SonosProfile"/> are the only places in cosine that know Sonos grouping exists
/// (plan §1.4).
/// </para>
/// </summary>
public static class ZoneGroupTopology
{
    /// <summary>
    /// Parses a <c>ZoneGroupState</c> document into its groups.
    /// <para>
    /// The document arrives as the value of a string out-argument, so the SOAP layer has already
    /// unescaped it and what lands here is a document in its own right rather than a fragment of
    /// the envelope.
    /// </para>
    /// <para>
    /// Never throws. A topology that cannot be read is reported as "no groups", because the only
    /// caller's response to either answer is the same - command the device the user picked - and
    /// an exception would just be an awkward way of saying so.
    /// </para>
    /// </summary>
    /// <param name="zoneGroupStateXml">The raw document, or null/blank if the device returned none.</param>
    public static IReadOnlyList<ZoneGroup> Parse(string? zoneGroupStateXml)
    {
        if (string.IsNullOrWhiteSpace(zoneGroupStateXml))
        {
            return [];
        }

        var document = TryLoad(zoneGroupStateXml) ?? TryLoad(Unescape(zoneGroupStateXml));
        if (document is null)
        {
            return [];
        }

        // Matched on local name and searched from the root: firmwares differ on whether the
        // groups are wrapped in <ZoneGroupState><ZoneGroups> or returned as a bare <ZoneGroups>,
        // and neither shape is worth a separate code path.
        var groups = new List<ZoneGroup>();
        foreach (var element in document.Descendants().Where(e => e.Name.LocalName == "ZoneGroup"))
        {
            var coordinator = Normalise((string?)element.Attribute("Coordinator"));
            if (coordinator.Length == 0)
            {
                // A group with no coordinator names no command target, so it is noise.
                continue;
            }

            var members = element
                .Descendants()
                .Where(e => e.Name.LocalName is "ZoneGroupMember" or "Satellite")
                .Select(ReadMember)
                .Where(m => m is not null)
                .Select(m => m!)
                .ToArray();

            if (members.Length == 0)
            {
                continue;
            }

            groups.Add(new ZoneGroup(
                Id: (string?)element.Attribute("ID") ?? coordinator,
                CoordinatorUuid: coordinator,
                Members: members));
        }

        return groups;
    }

    /// <summary>
    /// The coordinator of whichever group contains <paramref name="udn"/>, or null when the
    /// device is not in the topology at all - which happens for a player that has just left the
    /// household, and for every non-Sonos device.
    /// <para>
    /// Returns the coordinator even when it <em>is</em> the device asked about. Callers need to
    /// tell "you are your own coordinator" apart from "no answer", and collapsing the two here
    /// would hide the difference.
    /// </para>
    /// </summary>
    /// <param name="groups">Output of <see cref="Parse"/>.</param>
    /// <param name="udn">The device's UDN or UUID, in either form - see <see cref="Normalise"/>.</param>
    private static XDocument? TryLoad(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            return XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes one layer of XML escaping, for firmware that returns <c>ZoneGroupState</c> already
    /// escaped inside its string out-argument - so the SOAP layer unescapes it once and what
    /// arrives here is still <c>&amp;lt;ZoneGroups&amp;gt;…</c> rather than a document.
    /// <para>
    /// Worth handling rather than reporting as "no groups": that answer is indistinguishable from
    /// a genuinely ungrouped player, so the symptom would be a grouped satellite commanded
    /// directly, with nothing anywhere to indicate why. Older Sonos firmware is reported to
    /// double-escape and this has not been checked against hardware, so the cheap fix is to
    /// accept both shapes rather than to bet on one.
    /// </para>
    /// <para>
    /// Done by parsing the text as element content, which is exactly the context the escaping was
    /// produced for, rather than by string replacement that would have to get entity ordering
    /// right by hand.
    /// </para>
    /// </summary>
    private static string? Unescape(string escaped)
    {
        try
        {
            return XElement.Parse($"<unescape>{escaped}</unescape>").Value;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public static ZoneGroupMember? FindCoordinatorFor(IReadOnlyList<ZoneGroup> groups, string? udn)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var wanted = Normalise(udn);
        if (wanted.Length == 0)
        {
            return null;
        }

        foreach (var group in groups)
        {
            if (!group.Members.Any(m => string.Equals(m.Uuid, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The coordinator attribute names a member; if the document contradicts itself and
            // names one that is not in the group, there is nothing to point at.
            return group.Members.FirstOrDefault(m =>
                string.Equals(m.Uuid, group.CoordinatorUuid, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    /// <summary>
    /// Reduces a device identifier to the bare form the topology document uses.
    /// <para>
    /// The same player is called <c>uuid:RINCON_ABC01400</c> in its device description's
    /// <c>&lt;UDN&gt;</c>, <c>RINCON_ABC01400</c> in the topology, and
    /// <c>uuid:RINCON_ABC01400::urn:schemas-upnp-org:device:ZonePlayer:1</c> in an SSDP
    /// <c>USN</c>. Comparing any two of those raw finds no match, and the visible symptom is a
    /// grouped player that quietly keeps being commanded directly - so normalisation happens on
    /// both sides of every comparison here.
    /// </para>
    /// </summary>
    /// <returns>The bare uuid, or an empty string for null/blank input.</returns>
    public static string Normalise(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var separator = text.IndexOf("::", StringComparison.Ordinal);
        if (separator >= 0)
        {
            text = text[..separator];
        }

        if (text.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["uuid:".Length..];
        }

        return text.Trim();
    }

    /// <summary>
    /// The <c>uuid:</c>-prefixed form, which is what a <c>DeviceDescription.Udn</c> and therefore
    /// a <c>RendererEndpoint.Udn</c> carries.
    /// </summary>
    public static string ToUdn(string uuid)
        => uuid.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase) ? uuid : "uuid:" + uuid;

    private static ZoneGroupMember? ReadMember(XElement element)
    {
        var uuid = Normalise((string?)element.Attribute("UUID"));
        if (uuid.Length == 0)
        {
            return null;
        }

        var location = (string?)element.Attribute("Location");
        _ = Uri.TryCreate(location, UriKind.Absolute, out var parsed);

        return new ZoneGroupMember(uuid, parsed, (string?)element.Attribute("ZoneName"));
    }
}

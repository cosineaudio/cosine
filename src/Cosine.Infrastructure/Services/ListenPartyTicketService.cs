using System.Text.Json;
using Cosine.Core.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace Cosine.Infrastructure.Services;

/// <summary>
/// Mints and validates guest tickets, following <c>PlaybackStreamResolver</c>'s token rules:
/// a distinct Data Protection purpose so a party ticket can never be replayed against the
/// renderer stream routes, and one undifferentiated "no" for everything invalid.
/// </summary>
public sealed class ListenPartyTicketService : IListenPartyTicketService
{
    private const string Purpose = "cosine.party.guest";

    /// <summary>
    /// Generous on purpose: expiry is not the access control, party liveness is - every use
    /// re-checks the party, and ending it kills all tickets at once. The TTL only bounds how
    /// long a ticket for an *abandoned but never-ended* party stays plausible.
    /// </summary>
    private const int TicketTtlHours = 24;

    private readonly ITimeLimitedDataProtector _protector;
    private readonly ILogger<ListenPartyTicketService> _log;

    public ListenPartyTicketService(IDataProtectionProvider provider, ILogger<ListenPartyTicketService> log)
    {
        _protector = provider.CreateProtector(Purpose).ToTimeLimitedDataProtector();
        _log = log;
    }

    public string Mint(ListenPartyTicket ticket) =>
        _protector.Protect(JsonSerializer.Serialize(ticket), TimeSpan.FromHours(TicketTtlHours));

    public bool TryValidate(string token, out ListenPartyTicket ticket)
    {
        ticket = null!;
        if (string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<ListenPartyTicket>(_protector.Unprotect(token));
            if (parsed is null) return false;
            ticket = parsed;
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            _log.LogDebug("Rejected party ticket: {Reason}", ex.GetType().Name);
            return false;
        }
    }
}

using System.Security.Claims;
using System.Text.Json;

namespace Saem.Mcp;

/// <summary>
/// Who a request is for, as its token says: the person (<c>sub</c>), the groups the issuer puts them in
/// (<c>groups</c>) and, when an agent obtained the token on that person's behalf, the agent (the
/// <c>sub</c> of the RFC 8693 <c>act</c> claim). Read from the validated token only — never from the
/// tool's arguments. The groups decide which materialized partitions the person reads (saem D-22): the
/// identity provider says who is in which group, saem keeps no table of people.
/// </summary>
/// <param name="Subject">The person the lookup is run for.</param>
/// <param name="Actor">The agent acting for them, or <see langword="null"/> when the person called directly.</param>
public sealed record Person(string Subject, string? Actor)
{
    /// <summary>The person's groups, in name order; empty when the token names none.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>
    /// Reads the person from a validated token's claims, or returns <see langword="null"/> when the
    /// token names nobody.
    /// </summary>
    public static Person? From(ClaimsPrincipal? user)
    {
        var subject = user?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        return new Person(subject, ActorOf(user!.FindFirst("act")?.Value))
        {
            Groups = [.. user.FindAll("groups").Select(c => c.Value).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>The same person, actor and groups.</summary>
    public bool Equals(Person? other) =>
        other is not null && Subject == other.Subject && Actor == other.Actor && Groups.SequenceEqual(other.Groups, StringComparer.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Subject, Actor, Groups.Count);

    private static string? ActorOf(string? act)
    {
        if (string.IsNullOrEmpty(act))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(act);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("sub", out var sub)
                && sub.ValueKind == JsonValueKind.String
                ? sub.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

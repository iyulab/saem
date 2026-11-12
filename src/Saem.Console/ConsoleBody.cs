using System.Text.Json;

namespace Saem.Console;

/// <summary>
/// The members a console request body is read by. A body is read by name, so a member nobody reads is a typo
/// (<c>acknowledgeBreakng</c>), a field of another version, or an attempt to be someone else (<c>requestedBy</c>) — and in
/// every case the builder believes they said something. It is refused and named, never taken for «nothing said».
/// </summary>
internal static class ConsoleBody
{
    private const int NameShown = 64;

    /// <summary>The first member of <paramref name="body"/> that is not one of <paramref name="known"/> (compared as written), if it is an object.</summary>
    internal static string? Unknown(JsonElement body, IReadOnlyCollection<string> known)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var member in body.EnumerateObject())
        {
            if (!known.Contains(member.Name, StringComparer.Ordinal))
            {
                return member.Name;
            }
        }

        return null;
    }

    /// <summary>What the refusal says: which member nobody reads, and the members that are.</summary>
    internal static string Refusal(string member, IReadOnlyCollection<string> known)
    {
        var shown = member.Length > NameShown ? member[..NameShown] + "…" : member;
        return known.Count == 0
            ? $"{shown} is not read here: this takes no members."
            : $"{shown} is not read here; the members are: {string.Join(", ", known)}.";
    }
}

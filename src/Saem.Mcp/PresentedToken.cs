namespace Saem.Mcp;

/// <summary>
/// The access token the request presented, already validated — kept only so a source that takes the
/// person's token can be given one exchanged from it. It never prints: <see cref="ToString"/> hides it,
/// so a log line or an exception message that formats it carries no credential.
/// </summary>
public sealed class PresentedToken
{
    /// <summary>Wraps <paramref name="value"/>, the bearer token as presented.</summary>
    public PresentedToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    /// <summary>The token as presented.</summary>
    public string Value { get; }

    /// <summary>Reads the bearer token from an <c>Authorization</c> header value, or returns null when there is none.</summary>
    public static PresentedToken? FromAuthorization(string? header)
    {
        const string Bearer = "Bearer ";
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Bearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = header[Bearer.Length..].Trim();
        return value.Length == 0 ? null : new PresentedToken(value);
    }

    /// <inheritdoc />
    public override string ToString() => "[token]";
}

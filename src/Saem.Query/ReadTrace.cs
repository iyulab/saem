namespace Saem.Query;

/// <summary>What became of one record a walk asked a source for.</summary>
/// <param name="Type">The declared entity type.</param>
/// <param name="Key">The record's key as its type spells keys — the one spelling a walk uses for it, however a caller wrote it.</param>
/// <param name="Status"><see cref="ReadStatus"/>.</param>
public sealed record ReadAttempt(string Type, string Key, string Status);

/// <summary>The statuses of a <see cref="ReadAttempt"/>.</summary>
public static class ReadStatus
{
    /// <summary>The source returned the record to saem as the person.</summary>
    public const string Read = "read";

    /// <summary>The source returned nothing to the person — the record may not exist or may not be theirs.</summary>
    public const string NotVisible = "notVisible";

    /// <summary>The read did not complete: the source failed, answered ambiguously, or the lookup was stopped while it was out.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// The records a walk asked its sources for, in the order it asked — kept as the walk goes, so it is there however the walk
/// ends: answered, refused, stopped or failed. What a call's audit says was reached is this and nothing inferred from the
/// answer, which can only name what the walk returned.
/// </summary>
public sealed class ReadTrace
{
    private readonly List<ReadAttempt> _attempts = [];

    /// <summary>Each record the walk asked for, once per status, in the order first asked.</summary>
    public IReadOnlyList<ReadAttempt> Attempts => _attempts;

    internal void Add(string type, string key, string status)
    {
        var attempt = new ReadAttempt(type, key, status);
        if (!_attempts.Contains(attempt))
        {
            _attempts.Add(attempt);
        }
    }
}

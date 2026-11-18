namespace Saem.Authoring.Shadow;

/// <summary>How a record a proposal cited stands when it is looked at again.</summary>
public enum CitedStanding
{
    /// <summary>The cited version is the current version of its record.</summary>
    Standing,

    /// <summary>A later version of the record has corrected it; the citation was true of the version it named.</summary>
    Superseded,

    /// <summary>The record has been retired.</summary>
    Retired,

    /// <summary>The source holds no such record.</summary>
    Missing,

    /// <summary>The cited id names something that cannot be looked up again by id, or the citation does not say what it is of.</summary>
    NotCheckable,

    /// <summary>The source could not be read.</summary>
    Unreadable,
}

/// <summary>A cited record as the source holds it now.</summary>
/// <param name="Standing">How the record stands.</param>
/// <param name="Fields">The record's fields when it is there to read.</param>
public sealed record CitedReading(CitedStanding Standing, IReadOnlyDictionary<string, string?> Fields);

/// <summary>Where the records a proposal cited are looked at again — the source itself, not the sample the proposal was drawn from.</summary>
public interface ICitationSource
{
    /// <summary>Reads the cited records of one subject; every id asked about has a reading in the answer.</summary>
    /// <param name="subject">The subject (form type or table) the records are of.</param>
    /// <param name="recordIds">The cited ids.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyDictionary<string, CitedReading>> ReadAsync(string subject, IReadOnlyCollection<string> recordIds, CancellationToken cancellationToken);
}

/// <summary>What one check found about one cited record.</summary>
/// <param name="Record">The cited id.</param>
/// <param name="Subject">The subject the record is of, when the pass said.</param>
/// <param name="Standing">How the record stands.</param>
public sealed record ShadowCite(string Record, string? Subject, CitedStanding Standing);

/// <summary>The verdicts a check gives a proposal.</summary>
public static class ShadowVerdict
{
    /// <summary>Every cited record is there, current or merely corrected since, and carries what the proposal depends on.</summary>
    public const string Supported = "supported";

    /// <summary>A cited record is missing or retired, or none of them carries what the proposal depends on.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The check could not be completed: the source could not be read, or a citation cannot be looked up again.</summary>
    public const string Unreadable = "unreadable";
}

/// <summary>
/// One check's finding about one proposal of one pass. A row is a record of what was found, never an instruction: authoring, review
/// and settlement do not read the ledger the rows are kept in.
/// </summary>
/// <param name="Pass">The pass the proposal belongs to.</param>
/// <param name="ItemId">The proposal's id in the pass — its kind and its name.</param>
/// <param name="Kind">The proposal's kind.</param>
/// <param name="Name">The proposal's name.</param>
/// <param name="Layer">The check that judged — <see cref="SourceRecheck.Layer"/>.</param>
/// <param name="LayerVersion">The check's version.</param>
/// <param name="Verdict">One of <see cref="ShadowVerdict"/>.</param>
/// <param name="Field">The field the proposal depends on the cited records to carry, when it depends on one.</param>
/// <param name="FieldCarried">Whether any cited record that could be read carries <paramref name="Field"/>; absent when none could be read.</param>
/// <param name="Cites">What was found about each cited record.</param>
/// <param name="Note">Why the check could not be completed, when it could not.</param>
/// <param name="At">When the check ran.</param>
public sealed record ShadowRow(
    string Pass,
    string ItemId,
    string Kind,
    string Name,
    string Layer,
    int LayerVersion,
    string Verdict,
    string? Field,
    bool? FieldCarried,
    IReadOnlyList<ShadowCite> Cites,
    string? Note,
    DateTimeOffset At);

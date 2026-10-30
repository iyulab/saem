namespace Saem.Connectors.Formbase;

/// <summary>
/// Which end of a form type's stream a sample is taken from.
/// </summary>
public enum SampleDirection
{
    /// <summary>
    /// The most recent standing records. A form type whose declaration was revised mid-stream is sampled
    /// as it stands now, not as it was first filled in.
    /// </summary>
    Newest,

    /// <summary>
    /// The earliest standing records — the start of the stream. After a revision, a sample small enough
    /// to be spent on the original form sees none of the revised fields.
    /// </summary>
    Oldest,
}

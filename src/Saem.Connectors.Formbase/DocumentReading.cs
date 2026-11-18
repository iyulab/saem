namespace Saem.Connectors.Formbase;

/// <summary>How a document a claim cited stands in its form type's stream now.</summary>
public enum DocumentStanding
{
    /// <summary>The document is the current version of its record, or has no record key and so is a record of its own.</summary>
    Standing,

    /// <summary>A later document of the same record has corrected it: the citation was true of the version it named.</summary>
    Superseded,

    /// <summary>The record the document belongs to has been retired.</summary>
    Retired,

    /// <summary>The form type's stream holds no document with that id.</summary>
    Missing,

    /// <summary>The cited id names something a document id cannot be checked against — a row of a repeated section, which Formbase cuts and names itself.</summary>
    NotCheckable,
}

/// <summary>A cited document as the stream holds it now: how it stands, and its fields when it is there to read.</summary>
/// <param name="Standing">How the document stands.</param>
/// <param name="Fields">The document's top-level members as the sample reads them; empty when the document is missing or not checkable.</param>
public sealed record DocumentReading(DocumentStanding Standing, IReadOnlyDictionary<string, string?> Fields);

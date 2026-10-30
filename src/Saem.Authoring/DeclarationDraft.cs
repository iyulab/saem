using System.Text.Json;
using Saem.Ontology;

namespace Saem.Authoring;

/// <summary>
/// The declarations a proposal leads to, ready for a person to review: the declarations already in
/// force, untouched and in their order, followed by the ones the proposal adds. Writing
/// <see cref="ToJson"/> over the catalog file turns the review into a diff — every changed line is
/// an addition, and a line the person deletes is a declaration not taken.
/// </summary>
/// <param name="Declarations">Everything the catalog would hold: the existing declarations, then <paramref name="Added"/>.</param>
/// <param name="Added">The declarations the proposal adds — entity types by name, then relations by name.</param>
/// <param name="Evidence">For each added declaration, by name, the records it was drawn from.</param>
/// <param name="Notes">What the proposal said that could not become a declaration, and why.</param>
public sealed record DeclarationDraft(
    IReadOnlyList<Declaration> Declarations,
    IReadOnlyList<Declaration> Added,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Evidence,
    IReadOnlyList<DraftNote> Notes)
{
    /// <summary>
    /// The declarations in the catalog file's form — the same JSON <see cref="FileOntologyCatalog"/>
    /// reads, indented one declaration per block so that a diff shows each one whole.
    /// </summary>
    public string ToJson() => OntologyFile.ToIndentedJson(Declarations);
}

/// <summary>
/// One thing a proposal said that the draft does not declare: what it was about, why it could not be
/// declared, and the records it cited, so that the reviewer can look at them and declare it by hand.
/// </summary>
/// <param name="About">The entity type or relation the note is about, as the proposal named it.</param>
/// <param name="Reason">Why it is not in the draft.</param>
/// <param name="Records">The records the proposal cited for it.</param>
public sealed record DraftNote(string About, string Reason, IReadOnlyList<string> Records);

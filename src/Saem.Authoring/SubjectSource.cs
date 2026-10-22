namespace Saem.Authoring;

/// <summary>
/// Where the records of one subject stand as a source table a lookup can read: the system, the
/// table, the field whose value keys an entity, and — where a record's field names are not the
/// table's column names — which column holds each field.
/// </summary>
/// <param name="System">The source system, as the deployment names it.</param>
/// <param name="Table">The table (or equivalent) the subject's records are rows of.</param>
/// <param name="KeyField">The record field whose value is an entity's key.</param>
/// <param name="Columns">
/// Field name → column name, for the fields whose column is named otherwise. A field not listed
/// here is held by the column of the same name.
/// </param>
public sealed record SubjectSource(
    string System,
    string Table,
    string KeyField,
    IReadOnlyDictionary<string, string>? Columns = null)
{
    /// <summary>The column that holds <paramref name="field"/>.</summary>
    public string ColumnOf(string field) =>
        Columns is not null && Columns.TryGetValue(field, out var column) ? column : field;
}

using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>One group's pass: its edges, read as its accounts, made what its store holds.</summary>
internal static class MaterializeRun
{
    /// <exception cref="MaterializeUsageException">A relation leaves a type on a system the sources file does not name.</exception>
    public static async Task<EdgeWrite> RunAsync(
        Ontology.Ontology ontology,
        IReadOnlyDictionary<string, SourceOpener> openers,
        string group,
        EntityIris iris,
        FormbaseEdgeStore store,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // Checked before the pass: a system found missing halfway would leave the store half replaced.
        var missing = ontology.Relations.Values
            .Select(r => ontology.EntityTypes[r.From].System)
            .Distinct(StringComparer.Ordinal)
            .Where(system => !openers.ContainsKey(system))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            throw new MaterializeUsageException($"The relations leave types on {string.Join(", ", missing.Select(s => $"'{s}'"))}, which the sources file does not name.");
        }

        var edges = new EdgeMaterializer(ontology, iris).MaterializeAsync(new PersonSessions(openers), cancellationToken);
        var write = await store.ReplaceAsync(edges, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"group {group}: {write.Written} edge(s) written, {write.Unchanged} unchanged, {write.Retired} retired.").ConfigureAwait(false);
        return write;
    }
}

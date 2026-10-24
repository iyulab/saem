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
        RequireSystems(ontology, openers);

        var edges = new EdgeMaterializer(ontology, iris).MaterializeAsync(new PersonSessions(openers), cancellationToken);
        var write = await store.ReplaceAsync(edges, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"group {group}: {write.Written} edge(s) written, {write.Unchanged} unchanged, {write.Retired} retired; as of {write.ReadFrom:O}.").ConfigureAwait(false);
        return write;
    }

    /// <summary>
    /// The group's store compared with the edges a pass would give now, read as its accounts — nothing written
    /// (saem D-81 ⑤). A store that disagrees is rebuilt by running the pass.
    /// </summary>
    /// <exception cref="MaterializeUsageException">A relation leaves a type on a system the sources file does not name.</exception>
    public static async Task<EdgeComparison> VerifyAsync(
        Ontology.Ontology ontology,
        IReadOnlyDictionary<string, SourceOpener> openers,
        string group,
        EntityIris iris,
        FormbaseEdgeStore store,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireSystems(ontology, openers);
        var edges = new EdgeMaterializer(ontology, iris).MaterializeAsync(new PersonSessions(openers), cancellationToken);
        var comparison = await store.CompareAsync(edges, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"group {group}: {comparison.Matching} edge(s) as the sources give them, {comparison.Missing} missing, {comparison.Differing} different, " +
            $"{comparison.Extra} no longer given, {comparison.SkippedDocuments} document(s) skipped by the projection — " +
            (comparison.Agrees ? "the store agrees with the sources." : "run the pass to rebuild it.")).ConfigureAwait(false);
        return comparison;
    }

    private static void RequireSystems(Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers)
    {
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
    }
}

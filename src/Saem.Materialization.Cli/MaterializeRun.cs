using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>One group's pass: its edges and what its identities find, read as its accounts, made what its stores hold.</summary>
internal static class MaterializeRun
{
    /// <param name="identities">The group's identity store; omitted, the pass writes edges only.</param>
    /// <exception cref="MaterializeUsageException">A relation or an identity stands on a type on a system the sources file does not name.</exception>
    public static async Task<EdgeWrite> RunAsync(
        Ontology.Ontology ontology,
        IReadOnlyDictionary<string, SourceOpener> openers,
        string group,
        EntityIris iris,
        FormbaseEdgeStore store,
        FormbaseIdentityStore? identities,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        // Checked before the pass: a system found missing halfway would leave the store half replaced.
        RequireSystems(ontology, openers, identities is not null);

        var edges = new EdgeMaterializer(ontology, iris).MaterializeAsync(new PersonSessions(openers), cancellationToken);
        var write = await store.ReplaceAsync(edges, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync($"group {group}: {write.Written} edge(s) written, {write.Unchanged} unchanged, {write.Retired} retired; as of {write.ReadFrom:O}.").ConfigureAwait(false);

        if (identities is not null)
        {
            // Run even with no identity declared: one declared before leaves its records behind otherwise.
            var clusters = new IdentityMatcher(ontology, iris).MatchAsync(new PersonSessions(openers), cancellationToken);
            var found = await identities.ReplaceAsync(ontology, clusters, cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(
                $"group {group}: {found.Canonical.Written + found.Canonical.Unchanged} identified thing(s) — {found.Issued} new, {found.Merged} merged, " +
                $"{found.Canonical.Retired - found.Merged} gone; {found.Members.Written} cross-reference(s) and {found.Grounds.Written} ground(s) written.").ConfigureAwait(false);
        }

        return write;
    }

    /// <summary>
    /// The group's stores compared with what a pass would give now, read as its accounts — nothing written
    /// (saem D-81 ⑤): its edges, then what its identities find. Whether both agree; a store that disagrees is rebuilt
    /// by running the pass.
    /// </summary>
    /// <param name="identities">The group's identity store; omitted, the edges only are compared.</param>
    /// <exception cref="MaterializeUsageException">A relation or an identity stands on a type on a system the sources file does not name.</exception>
    public static async Task<bool> VerifyAsync(
        Ontology.Ontology ontology,
        IReadOnlyDictionary<string, SourceOpener> openers,
        string group,
        EntityIris iris,
        FormbaseEdgeStore store,
        FormbaseIdentityStore? identities,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        RequireSystems(ontology, openers, identities is not null);
        var edges = new EdgeMaterializer(ontology, iris).MaterializeAsync(new PersonSessions(openers), cancellationToken);
        var comparison = await store.CompareAsync(edges, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"group {group}: {comparison.Matching} edge(s) as the sources give them, {comparison.Missing} missing, {comparison.Differing} different, " +
            $"{comparison.Extra} no longer given, {comparison.SkippedDocuments} document(s) skipped by the projection — " +
            (comparison.Agrees ? "the store agrees with the sources." : "run the pass to rebuild it.")).ConfigureAwait(false);
        if (identities is null)
        {
            return comparison.Agrees;
        }

        var clusters = new IdentityMatcher(ontology, iris).MatchAsync(new PersonSessions(openers), cancellationToken);
        var found = await identities.CompareAsync(ontology, clusters, cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"group {group}: {found.Matching} identified thing(s) as the sources give them, {found.Missing} missing, {found.Differing} different, " +
            $"{found.Extra} no longer found; {found.GroundsMissing} ground(s) missing, {found.GroundsExtra} no longer found, " +
            $"{found.SkippedDocuments} document(s) skipped by the projections — " +
            (found.Agrees ? "the identities agree with the sources." : "run the pass to rebuild them.")).ConfigureAwait(false);
        return comparison.Agrees && found.Agrees;
    }

    private static void RequireSystems(Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers, bool identities)
    {
        var read = ontology.Relations.Values.Select(r => r.From);
        if (identities)
        {
            read = read.Concat(ontology.Identities.Values.Where(i => i.Match is not null).SelectMany(i => new[] { i.First, i.Second }));
        }

        var missing = read
            .Select(type => ontology.EntityTypes[type].System)
            .Distinct(StringComparer.Ordinal)
            .Where(system => !openers.ContainsKey(system))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (missing.Count > 0)
        {
            throw new MaterializeUsageException($"The relations and identities stand on types on {string.Join(", ", missing.Select(s => $"'{s}'"))}, which the sources file does not name.");
        }
    }
}

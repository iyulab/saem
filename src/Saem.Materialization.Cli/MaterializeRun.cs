using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>One group's pass: its edges and what its identities find, read as its accounts, made what its stores hold.</summary>
internal static class MaterializeRun
{
    /// <param name="identities">The group's identity store; omitted, the pass writes edges only.</param>
    /// <param name="changedOnly">
    /// Read only the rows changed since the last pass, where the store says where each relation was read to (SD-08); a
    /// relation it says nothing of is read whole. Identities are not matched by such a pass.
    /// </param>
    /// <exception cref="MaterializeUsageException">A relation or an identity stands on a type on a system the sources file does not name.</exception>
    public static async Task<EdgeWrite> RunAsync(
        Ontology.Ontology ontology,
        IReadOnlyDictionary<string, SourceOpener> openers,
        string group,
        EntityIris iris,
        FormbaseEdgeStore store,
        FormbaseIdentityStore? identities,
        TextWriter output,
        CancellationToken cancellationToken,
        bool changedOnly = false)
    {
        // Checked before the pass: a system found missing halfway would leave the store half replaced.
        RequireSystems(ontology, openers, identities is not null && !changedOnly);

        // Where this pass reads to is asked before it reads: a row changed while it reads is read again by the next.
        var materializer = new EdgeMaterializer(ontology, iris);
        var latest = await materializer.LatestChangesAsync(new PersonSessions(openers), cancellationToken).ConfigureAwait(false);
        var since = changedOnly
            ? (await store.WatermarksAsync(cancellationToken).ConfigureAwait(false)).Where(m => latest.ContainsKey(m.Key)).ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal)
            : [];
        var write = await store.MergeAsync(materializer.RowsAsync(new PersonSessions(openers), since, cancellationToken), since.Keys.ToHashSet(StringComparer.Ordinal), latest, cancellationToken).ConfigureAwait(false);
        var part = changedOnly ? $" ({write.ReadInPart} relation(s) read where changed, {write.RowsRead} row(s) read)" : "";
        await output.WriteLineAsync($"group {group}: {write.Written} edge(s) written, {write.Unchanged} unchanged, {write.Retired} retired{part}; as of {write.ReadFrom:O}.").ConfigureAwait(false);

        if (changedOnly)
        {
            return write;
        }

        if (identities is not null)
        {
            // Run even with no identity declared: one declared before leaves its records behind otherwise.
            var skipped = new List<LinkageSkip>();
            var clusters = await FoundAsync(ontology, openers, iris, identities, skipped, cancellationToken).ConfigureAwait(false);
            var found = await identities.ReplaceAsync(ontology, clusters, cancellationToken).ConfigureAwait(false);
            await TellSkippedAsync(group, skipped, output).ConfigureAwait(false);
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

        var skipped = new List<LinkageSkip>();
        var clusters = await FoundAsync(ontology, openers, iris, identities, skipped, cancellationToken).ConfigureAwait(false);
        var found = await identities.CompareAsync(ontology, clusters, cancellationToken).ConfigureAwait(false);
        await TellSkippedAsync(group, skipped, output).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"group {group}: {found.Matching} identified thing(s) as the sources give them, {found.Missing} missing, {found.Differing} different, " +
            $"{found.Extra} no longer found; {found.GroundsMissing} ground(s) missing, {found.GroundsExtra} no longer found, " +
            $"{found.SkippedDocuments} document(s) skipped by the projections — " +
            (found.Agrees ? "the identities agree with the sources." : "run the pass to rebuild them.")).ConfigureAwait(false);
        return comparison.Agrees && found.Agrees;
    }

    /// <summary>
    /// What every identity finds: the field-matched ones by equal values, then the ones by a linkage rule scored with the
    /// fits the store keeps — one with no fit, or a fit of another rule, is left out and told to <paramref name="skipped"/>.
    /// </summary>
    private static async Task<IAsyncEnumerable<IdentityCluster>> FoundAsync(
        Ontology.Ontology ontology,
        IReadOnlyDictionary<string, SourceOpener> openers,
        EntityIris iris,
        FormbaseIdentityStore identities,
        List<LinkageSkip> skipped,
        CancellationToken cancellationToken)
    {
        var fits = ontology.Identities.Values.Any(i => i.Linkage is not null)
            ? await identities.FitsAsync(cancellationToken).ConfigureAwait(false)
            : new Dictionary<string, LinkageFit>();
        return new IdentityMatcher(ontology, iris).MatchAsync(new PersonSessions(openers), cancellationToken)
            .Concat(new IdentityLinker(ontology, iris).ScoreAsync(new PersonSessions(openers), fits, skipped.Add, cancellationToken));
    }

    private static async Task TellSkippedAsync(string group, List<LinkageSkip> skipped, TextWriter output)
    {
        foreach (var skip in skipped)
        {
            await output.WriteLineAsync($"group {group}: identity '{skip.Identity}' left out — {skip.Reason}.").ConfigureAwait(false);
        }
    }

    private static void RequireSystems(Ontology.Ontology ontology, IReadOnlyDictionary<string, SourceOpener> openers, bool identities)
    {
        var read = ontology.Relations.Values.Select(r => r.From);
        if (identities)
        {
            read = read.Concat(ontology.Identities.Values.SelectMany(i => new[] { i.First, i.Second }));
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

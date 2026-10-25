using Eyu.Core.Linkage;
using Saem.Query;

namespace Saem.Materialization.Cli;

/// <summary>
/// <c>saem-materialize fit</c>: fits an identity's linkage rule to what the group's account reads, and keeps the fit in
/// the group's store for its passes to score with (saem D-87 ⓑ). Run once, and again when the rule changes or the
/// records have moved far enough that the old rates no longer describe them.
/// </summary>
internal static class FitRun
{
    public const string Usage = """
        saem-materialize fit — fit an identity's linkage rule to one group's records and keep the fit for its passes.

        Usage:
          saem-materialize fit --identity <name> <the pass's own arguments, without --changed>

          --identity <name>              The declared identity by a linkage rule to fit.

        Both of the identity's types are read whole, as the group's account, the compared columns only. How often each
        compared field agrees on pairs that are one thing and on pairs that are two is fitted over every pair (two
        rows of one type are never a pair when the identity joins two), and kept in the group's store — rates only,
        no value any record holds. A pass scores the identity with the kept fit and leaves it out, saying so, while
        none is kept or the rule has changed since. A fit the records could not support (too few pairs to tell a
        match from a non-match) is not kept: exit status 1.
        """;

    /// <summary>Splits <c>--identity</c> from the pass's arguments.</summary>
    /// <exception cref="MaterializeUsageException">No identity is named, or --changed is given.</exception>
    public static (string Identity, MaterializeOptions Options) Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        var at = args.ToList().IndexOf("--identity");
        if (at < 0 || at + 1 >= args.Count || args[at + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new MaterializeUsageException("--identity <name> is required.");
        }

        var options = MaterializeOptions.Parse([.. args.Take(at), .. args.Skip(at + 2)], environment);
        return options.ChangedOnly
            ? throw new MaterializeUsageException("--changed reads part of a table; a fit reads both types whole.")
            : (args[at + 1], options);
    }

    /// <summary>Fits and keeps; whether the fit was kept.</summary>
    public static async Task<bool> RunAsync(
        Ontology.Ontology ontology,
        string identity,
        IReadOnlyDictionary<string, SourceOpener> openers,
        string group,
        EntityIris iris,
        FormbaseIdentityStore store,
        DateTimeOffset now,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var fit = await new IdentityLinker(ontology, iris).FitAsync(new PersonSessions(openers), identity, now, cancellationToken).ConfigureAwait(false);
        var parameters = fit.Parameters;
        if (parameters.Status == EstimationStatus.HeuristicDefault)
        {
            await output.WriteLineAsync(
                $"group {group}: '{identity}' could not be fitted on {fit.Records} entities — too few pairs to tell one thing from two; nothing kept.").ConfigureAwait(false);
            return false;
        }

        await store.KeepFitAsync(ontology, fit, cancellationToken).ConfigureAwait(false);
        var converged = parameters.Status == EstimationStatus.Converged ? "converged" : "did not converge within the rounds allowed — kept, check its scores";
        await output.WriteLineAsync(
            $"group {group}: '{identity}' fitted on {fit.Records} entities — {converged}; " +
            $"a pair is one thing a priori with probability {parameters.MatchPrior:0.####}. Kept for the next pass.").ConfigureAwait(false);
        return true;
    }
}

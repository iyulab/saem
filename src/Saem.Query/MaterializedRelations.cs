using Saem.Ontology;

namespace Saem.Query;

/// <summary>
/// The relations built ahead of time for the groups one lookup's person is in (saem D-22), which is how a
/// relation is walked backwards: no single read of a source finds every row that names an entity, so the
/// edges materialized for each group are asked instead — partition by partition, never merged.
/// </summary>
public interface IMaterializedRelations
{
    /// <summary>The groups whose partitions the person reads, in name order; empty when they are in none.</summary>
    IReadOnlyList<string> Partitions { get; }

    /// <summary>
    /// The keys of the entities of <paramref name="relation"/>'s <c>From</c> type whose relation leads to the
    /// entity of its <c>To</c> type keyed <paramref name="toKey"/>, as <paramref name="partition"/> holds them —
    /// at most <paramref name="limit"/> and one more, so a step with more than the limit is known without
    /// reading them all.
    /// </summary>
    /// <exception cref="MaterializedRelationsException">The partition could not be read.</exception>
    ValueTask<PartitionEdges> LeadingToAsync(string partition, RelationDeclaration relation, string toKey, int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// The time <paramref name="partition"/>'s edges are as of — when the pass that built them began reading
    /// the sources — or null when the partition does not say.
    /// </summary>
    /// <exception cref="MaterializedRelationsException">The partition could not be read.</exception>
    ValueTask<DateTimeOffset?> AsOfAsync(string partition, CancellationToken cancellationToken = default);
}

/// <summary>What a partition holds leading to one entity.</summary>
/// <param name="Keys">The keys of the entities its edges lead from.</param>
/// <param name="Incomplete">The partition was built without some of its source documents, so keys may be missing.</param>
public sealed record PartitionEdges(IReadOnlyList<string> Keys, bool Incomplete);

/// <summary>A lookup whose person reads no materialized partition.</summary>
public sealed class NoMaterializedRelations : IMaterializedRelations
{
    /// <summary>The one instance.</summary>
    public static NoMaterializedRelations Instance { get; } = new();

    private NoMaterializedRelations()
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Partitions => [];

    /// <inheritdoc />
    public ValueTask<PartitionEdges> LeadingToAsync(string partition, RelationDeclaration relation, string toKey, int limit, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new PartitionEdges([], Incomplete: false));

    /// <inheritdoc />
    public ValueTask<DateTimeOffset?> AsOfAsync(string partition, CancellationToken cancellationToken = default) => ValueTask.FromResult<DateTimeOffset?>(null);
}

/// <summary>
/// A group's partition could not be read while a relation was walked backwards. A failure, not an answer:
/// nothing is known about what the partition holds. The inner exception carries the detail for the log.
/// </summary>
public sealed class MaterializedRelationsException : Exception
{
    public MaterializedRelationsException()
    {
    }

    public MaterializedRelationsException(string message)
        : base(message)
    {
    }

    public MaterializedRelationsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for the partition of <paramref name="partition"/>.</summary>
    public static MaterializedRelationsException For(string partition, Exception innerException) =>
        new($"The materialized relations of the group '{partition}' could not be read.", innerException) { Partition = partition };

    /// <summary>The group whose partition failed.</summary>
    public string? Partition { get; private init; }
}

/// <summary>
/// A step of a lookup leads to more candidates than one step may read, so none was read. A limit of saem's,
/// not an answer about the sources: nothing is said about what they hold, nor how many there are.
/// </summary>
public sealed class TooBroadException : Exception
{
    public TooBroadException()
    {
    }

    public TooBroadException(string message)
        : base(message)
    {
    }

    public TooBroadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for the step at <paramref name="atHop"/>, walking <paramref name="relation"/>.</summary>
    public TooBroadException(int atHop, string relation, int limit)
        : base($"The step at hop {atHop} ('{relation}') leads to more than {limit} entities, so it was not read. Narrow the lookup: start nearer the entities wanted.")
    {
        AtHop = atHop;
        Relation = relation;
    }

    /// <summary>Creates the exception for a start named by <paramref name="field"/>'s value, which more than <paramref name="limit"/> entities hold.</summary>
    public static TooBroadException AtStart(string type, string field, int limit) =>
        new($"The start names '{type}' by '{field}', and that value is held by more than {limit} entities, so none was read. Narrow the lookup: start by a key, or by a field that names fewer.")
        {
            AtHop = 0,
        };

    /// <summary>The step that was too broad: 0 for the start, n for the n-th relation of the path.</summary>
    public int AtHop { get; private init; }

    /// <summary>The relation that step walks; null at the start.</summary>
    public string? Relation { get; private init; }
}

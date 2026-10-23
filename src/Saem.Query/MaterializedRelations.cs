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
    /// entity of its <c>To</c> type keyed <paramref name="toKey"/>, as <paramref name="partition"/> holds them.
    /// </summary>
    /// <exception cref="MaterializedRelationsException">The partition could not be read.</exception>
    IAsyncEnumerable<string> LeadingToAsync(string partition, RelationDeclaration relation, string toKey, CancellationToken cancellationToken = default);
}

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
    public IAsyncEnumerable<string> LeadingToAsync(string partition, RelationDeclaration relation, string toKey, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<string>();
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

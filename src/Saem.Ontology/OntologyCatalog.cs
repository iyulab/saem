using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saem.Ontology;

/// <summary>
/// Where saem reads its ontology from. The walk depends on this port only; whether the declarations
/// sit in a file next to the deployment or as documents in a store is the adapter's concern.
/// </summary>
public interface IOntologyCatalog
{
    /// <summary>Reads the declarations and returns them as a consistent ontology.</summary>
    /// <exception cref="OntologyDeclarationException">The declarations cannot be read, or are not consistent.</exception>
    ValueTask<Ontology> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads the ontology from a JSON file: an array of declarations, each naming its kind
/// (<c>entityType</c>, <c>relation</c>, <c>identity</c>, <c>linkage</c> or <c>grouping</c>). The file is read on every call, so an edit is seen by the
/// next lookup without a restart.
/// </summary>
public sealed class FileOntologyCatalog : IOntologyCatalog
{
    private readonly string _path;

    /// <summary>Creates a catalog over the file at <paramref name="path"/>.</summary>
    public FileOntologyCatalog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <inheritdoc />
    public async ValueTask<Ontology> GetAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Declaration>? declarations;
        try
        {
            var stream = File.OpenRead(_path);
            await using (stream.ConfigureAwait(false))
            {
                declarations = await JsonSerializer.DeserializeAsync(stream, OntologyJson.Default.IReadOnlyListDeclaration, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            throw new OntologyDeclarationException($"The ontology file '{_path}' cannot be read: {e.Message}", e);
        }

        if (declarations is null)
        {
            throw new OntologyDeclarationException($"The ontology file '{_path}' holds null, not an array of declarations.");
        }

        return Ontology.Create(declarations);
    }
}

/// <summary>
/// The declarations on the wire: camelCase members, the kind in <c>kind</c> wherever it appears.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, AllowOutOfOrderMetadataProperties = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IReadOnlyList<Declaration>))]
public sealed partial class OntologyJson : JsonSerializerContext;

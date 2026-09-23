using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Eyu.Core.Declared;
using Eyu.Core.Ports;
using Eyu.Core.Primitives;
using Eyu.Core.Records;

namespace Saem.Connectors.Formbase;

/// <summary>
/// Reads one Formbase instance over its HTTP surface and hands Eyu what it asks for: what a form
/// type declares (<see cref="IStructureSource"/>) and the documents it holds
/// (<see cref="IRecordSample"/>). A subject is a form type.
/// <para>
/// Read-only, and only over HTTP. Formbase runs in its own process and saem never loads its engine:
/// the process boundary is the licence boundary, and a connector that linked the engine in would
/// also be reading Formbase's storage behind its back. Nothing here writes, and nothing here judges —
/// entities and relations are Eyu's to propose from what this returns.
/// </para>
/// <para>
/// Documents are read from the raw stream, not from projected records: a projection carries only the
/// declared columns, and the fields nobody has declared yet are exactly what Eyu is asked to judge.
/// </para>
/// </summary>
/// <param name="http">
/// A client whose <see cref="HttpClient.BaseAddress"/> is the Formbase instance. The caller owns its
/// lifetime and any authentication in front of the instance.
/// </param>
/// <param name="formbaseNamespace">
/// The Formbase namespace to address, sent as <c>Formbase-Namespace</c>; omitted, the instance's own.
/// </param>
public sealed class FormbaseConnector(HttpClient http, string? formbaseNamespace = null) : IStructureSource, IRecordSample
{
    private const string NamespaceHeader = "Formbase-Namespace";
    private const string NoDeclaration = "/problems/no-declaration";

    /// <summary>The largest page the raw stream serves; a sample larger than this is read in several pages.</summary>
    private const int MaxPageSize = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<DeclaredStructure?> GetStructureAsync(SubjectRef subject, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync($"formtypes/{Uri.EscapeDataString(subject.Value)}/declaration", cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Only the answer "this form type has no declaration" means nothing is declared. Any other
            // 404 — a wrong base address, a host without this route — is a failure to read, and reading
            // it as "nothing declared" would send Eyu off to infer what was in fact declared.
            var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound && problem?.Type == NoDeclaration)
            {
                return null;
            }

            throw Refused(response, problem);
        }

        var declaration = await response.Content.ReadFromJsonAsync<DeclarationResponse>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new FormbaseConnectorException($"Formbase answered the declaration of '{subject}' with an empty body.");

        var fields = (declaration.Fields ?? [])
            .Select(field => new DeclaredField(field.Name, Kind: ToDeclaredKind(field.Type), Required: !field.Nullable))
            .ToList();

        var relations = (declaration.Relations ?? [])
            .Select(relation => new DeclaredRelation(
                relation.Name,
                SubjectRef.Create(relation.Target),
                ViaField: relation.KeyField,
                Kind: ToRelationKind(relation.Kind)))
            .ToList();

        return new DeclaredStructure(subject, fields, relations, declaration.DeclarationVersion.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The form type's documents in the order Formbase accepted them, oldest first, up to
    /// <paramref name="maxCount"/>. Each document's top-level members become the record's fields — a
    /// string as its text, a nested object or array as its JSON — and its id is the Formbase document
    /// id, so a claim Eyu grounds in it points back at a document the instance can show.
    /// </summary>
    public async Task<IReadOnlyList<RawRecord>> SampleAsync(SubjectRef subject, int maxCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);

        var records = new List<RawRecord>();
        long after = 0;
        while (records.Count < maxCount)
        {
            var limit = Math.Min(maxCount - records.Count, MaxPageSize);
            using var response = await SendAsync(
                $"formtypes/{Uri.EscapeDataString(subject.Value)}/documents?after={after.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            {
                throw new FormbaseConnectorException(
                    "This Formbase instance does not serve a form type's raw stream (GET /formtypes/{type}/documents answered 405). " +
                    "Reading documents across the process boundary needs a Formbase that serves it.");
            }

            await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync<DocumentPageResponse>(Json, cancellationToken).ConfigureAwait(false)
                ?? throw new FormbaseConnectorException($"Formbase answered a page of '{subject}' with an empty body.");

            foreach (var document in page.Documents)
            {
                records.Add(new RawRecord(document.DocumentId.ToString(), Flatten(document.Body)));
            }

            if (page.Documents.Count == 0 || page.Documents[^1].Watermark >= page.RawHead)
            {
                break;
            }

            after = page.Documents[^1].Watermark;
        }

        return records;
    }

    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (formbaseNamespace is not null)
        {
            request.Headers.Add(NamespaceHeader, formbaseNamespace);
        }

        return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw Refused(response, await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>The body is read once, by the caller, and passed in — a response's content cannot be read twice.</summary>
    private static FormbaseConnectorException Refused(HttpResponseMessage response, ProblemResponse? problem)
    {
        var detail = problem is null ? string.Empty : $" {problem.Type}: {problem.Detail}";
        return new FormbaseConnectorException(
            $"Formbase answered {(int)response.StatusCode} to {response.RequestMessage?.RequestUri}.{detail}",
            response.StatusCode);
    }

    private static async Task<ProblemResponse?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemResponse>(Json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static Dictionary<string, string?> Flatten(JsonElement body)
    {
        var fields = new Dictionary<string, string?>();
        if (body.ValueKind != JsonValueKind.Object)
        {
            return fields;
        }

        foreach (var property in body.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => property.Value.GetString(),
                _ => property.Value.GetRawText(),
            };
        }

        return fields;
    }

    private static DeclaredValueKind ToDeclaredKind(string type) => type switch
    {
        "text" => DeclaredValueKind.Text,
        "integer" => DeclaredValueKind.WholeNumber,
        "decimal" => DeclaredValueKind.FractionalNumber,
        "boolean" => DeclaredValueKind.Boolean,
        "timestamp" => DeclaredValueKind.Timestamp,
        "uuid" => DeclaredValueKind.Identifier,
        "jsonb" => DeclaredValueKind.Structured,
        _ => throw new FormbaseConnectorException($"Formbase declared a column type this connector does not know how to carry: '{type}'."),
    };

    private static DeclaredRelationKind ToRelationKind(string? kind) => kind switch
    {
        "reference" => DeclaredRelationKind.Reference,
        "child" => DeclaredRelationKind.Child,
        _ => throw new FormbaseConnectorException($"Formbase declared a relation kind this connector does not know how to carry: '{kind}'."),
    };

    private sealed record DeclarationResponse(long DeclarationVersion, IReadOnlyList<FieldResponse>? Fields, IReadOnlyList<RelationResponse>? Relations);

    private sealed record FieldResponse(string Name, string Type, bool Nullable);

    private sealed record RelationResponse(string Name, string? Kind, string Target, string? KeyField);

    private sealed record DocumentPageResponse(IReadOnlyList<DocumentResponse> Documents, long RawHead);

    private sealed record DocumentResponse(Guid DocumentId, long Watermark, JsonElement Body);

    private sealed record ProblemResponse(string? Type, string? Detail);
}

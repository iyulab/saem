using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Saem.Materialization;

/// <summary>
/// The calls saem's stores make of a group's Formbase: putting a form type of text fields in force, reading what
/// it holds, filing a record, retiring one and projecting. Every form type saem keeps there has text fields only and
/// files each record under a key of its own, so a store is a set of these over its own form types.
/// </summary>
/// <param name="http">A client whose <see cref="HttpClient.BaseAddress"/> is the group's Formbase. The caller owns its lifetime.</param>
/// <param name="formbaseNamespace">The Formbase namespace to address, sent as <c>Formbase-Namespace</c>; omitted, the instance's own.</param>
/// <param name="fail">Makes the store's own exception from a message.</param>
internal sealed class FormbaseForms(HttpClient http, string? formbaseNamespace, Func<string, Exception> fail)
{
    private const string NamespaceHeader = "Formbase-Namespace";
    private const int PageSize = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Puts the declaration of <paramref name="formType"/> in force when it has none. One already in force must
    /// be that declaration: a store whose form type was declared otherwise is not written over.
    /// </summary>
    public async Task DeclareAsync(string formType, string tableName, string[] fields, CancellationToken cancellationToken)
    {
        using (var read = Request(HttpMethod.Get, $"formtypes/{formType}/declaration"))
        using (var response = await http.SendAsync(read, cancellationToken).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                var declaration = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
                var declared = declaration.GetProperty("fields").EnumerateArray()
                    .Select(f => (Name: f.GetProperty("name").GetString(), Type: f.GetProperty("type").GetString(), Nullable: f.GetProperty("nullable").GetBoolean()))
                    .ToList();
                if (declaration.GetProperty("tableName").GetString() != tableName
                    || !declared.SequenceEqual(fields.Select(f => ((string?)f, (string?)"text", false))))
                {
                    throw fail($"The form type '{formType}' is declared with another shape than saem's; it is not written over.");
                }

                return;
            }

            if (response.StatusCode != HttpStatusCode.NotFound)
            {
                throw await FailureAsync($"Reading the declaration of '{formType}'", response, cancellationToken).ConfigureAwait(false);
            }
        }

        using var put = Request(HttpMethod.Put, $"formtypes/{formType}/declaration");
        put.Content = JsonContent.Create(new
        {
            tableName,
            declarationVersion = 1,
            fields = fields.Select(name => new { name, type = "text", nullable = false }),
        }, options: Json);
        await SendAsync(put, $"Declaring '{formType}'", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The projected records of <paramref name="formType"/> that <paramref name="filter"/> admits (query
    /// parameters, each led by <c>&amp;</c>), by record key, each as its <paramref name="fields"/>; none before
    /// the first pass declared and projected them. A projection that skipped documents fails the read, unless
    /// <paramref name="readOn"/> is given: then it is told how many, and the read goes on over the rows that stand.
    /// <paramref name="atMost"/> stops the read once that many records are in.
    /// </summary>
    public async Task<Dictionary<string, string[]>> HeldAsync(
        string formType, string filter, string[] fields, CancellationToken cancellationToken, Action<int>? readOn = null, int atMost = int.MaxValue)
    {
        var held = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var pageSize = Math.Min(PageSize, atMost);
        for (var offset = 0; ; offset += pageSize)
        {
            using var request = Request(HttpMethod.Get, $"formtypes/{formType}/records?limit={pageSize}&offset={offset}{filter}");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound
                && await ProblemAsync(response, cancellationToken).ConfigureAwait(false) is "/problems/not-projected" or "/problems/no-declaration")
            {
                return held;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw await FailureAsync($"Reading the held records of '{formType}'", response, cancellationToken).ConfigureAwait(false);
            }

            var page = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            // A Formbase before 0.35 does not report skips; one that does and reports some cannot be read in full.
            if (page.TryGetProperty("skippedDocuments", out var skipped) && skipped.GetInt32() > 0)
            {
                if (readOn is null)
                {
                    throw fail($"The projection of '{formType}' skipped documents, so what the store holds cannot be read in full.");
                }

                if (offset == 0)
                {
                    readOn(skipped.GetInt32());
                }
            }

            var rows = page.GetProperty("rows");
            foreach (var row in rows.EnumerateArray())
            {
                var key = row.GetProperty("record").GetProperty("key").GetString()
                    ?? throw fail($"A record of '{formType}' without a key is in the store; saem files every record under one.");
                var values = row.GetProperty("fields");
                held[key] = [.. fields.Select(f => values.GetProperty(f).GetString() ?? "")];
            }

            if (rows.GetArrayLength() < pageSize || held.Count >= atMost)
            {
                return held;
            }
        }
    }

    /// <summary>Files <paramref name="values"/> as the record <paramref name="key"/> of <paramref name="formType"/> — a new record, or a correction of the one held.</summary>
    public async Task FileAsync(string formType, string key, string[] fields, string[] values, string what, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, $"formtypes/{formType}/documents?recordKey={Uri.EscapeDataString(key)}");
        request.Content = JsonContent.Create(fields.Zip(values).ToDictionary(p => p.First, p => p.Second), options: Json);
        await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Retires the record <paramref name="key"/> of <paramref name="formType"/>.</summary>
    public async Task RetireAsync(string formType, string key, string what, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Delete, $"formtypes/{formType}/records?recordKey={Uri.EscapeDataString(key)}");
        await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Projects <paramref name="formType"/>, so its reads show what was filed.</summary>
    public async Task ProjectAsync(string formType, string what, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, $"formtypes/{formType}/projection");
        await SendAsync(request, what, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (formbaseNamespace is not null)
        {
            request.Headers.Add(NamespaceHeader, formbaseNamespace);
        }

        return request;
    }

    private async Task SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(what, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json, cancellationToken).ConfigureAwait(false);
            return problem.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Exception> FailureAsync(string what, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return fail($"{what} answered {(int)response.StatusCode}: {body}");
    }
}

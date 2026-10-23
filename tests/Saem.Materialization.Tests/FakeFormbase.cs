using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Saem.Materialization.Tests;

/// <summary>Formbase as its API page describes the calls the store makes.</summary>
internal sealed class FakeFormbase : HttpMessageHandler
{
    private readonly List<(string Key, JsonObject? Body)> _projected = [];

    public FakeFormbase() => Client = new HttpClient(this) { BaseAddress = new Uri("http://formbase.test/") };

    public HttpClient Client { get; }

    public JsonObject? Declaration { get; set; }

    public bool Projected { get; private set; }

    /// <summary>Every append, retirements included (a null body).</summary>
    public List<(string Key, JsonObject? Body)> Stream { get; } = [];

    /// <summary>The namespace header of every request, or null.</summary>
    public List<string?> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Headers.TryGetValues("Formbase-Namespace", out var ns) ? ns.Single() : null);
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        switch (request.Method.Method, path)
        {
            case ("GET", "/formtypes/edges/declaration"):
                return Declaration is null
                    ? Answer(HttpStatusCode.NotFound, new { type = "/problems/no-declaration" })
                    : Answer(HttpStatusCode.OK, Declaration);
            case ("PUT", "/formtypes/edges/declaration"):
                Declaration = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                return Answer(HttpStatusCode.Created, new { });
            case ("POST", "/formtypes/edges/documents"):
                Stream.Add((query["recordKey"]!, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject()));
                return Answer(HttpStatusCode.Created, new { });
            case ("DELETE", "/formtypes/edges/records"):
                Stream.Add((query["recordKey"]!, null));
                return Answer(HttpStatusCode.OK, new { });
            case ("POST", "/formtypes/edges/projection"):
                Projected = true;
                _projected.Clear();
                _projected.AddRange(Stream.GroupBy(d => d.Key).Select(g => g.Last()).Where(d => d.Body is not null).OrderBy(d => d.Key, StringComparer.Ordinal));
                return Answer(HttpStatusCode.OK, new { });
            case ("GET", "/formtypes/edges/records"):
                if (!Projected)
                {
                    return Answer(HttpStatusCode.Conflict, new { type = "/problems/not-projected" });
                }

                var filters = (query.GetValues("filter") ?? []).Select(f => (Column: f[..f.IndexOf(':', StringComparison.Ordinal)], Value: f[(f.IndexOf(':', StringComparison.Ordinal) + 1)..])).ToList();
                var page = _projected.Where(d => filters.All(f => (string?)d.Body![f.Column] == f.Value)).Skip(int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture)).Take(int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture));
                return Answer(HttpStatusCode.OK, new
                {
                    rows = page.Select(d => new { record = new { document = Guid.NewGuid(), key = d.Key }, fields = d.Body }),
                    stale = false,
                    skippedDocuments = 0,
                });
            default:
                throw new InvalidOperationException($"unexpected request {request.Method} {request.RequestUri.PathAndQuery}");
        }
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}

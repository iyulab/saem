using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Saem.Materialization.Tests;

/// <summary>Formbase as its API page describes the calls the store makes, one state per form type.</summary>
internal sealed class FakeFormbase : HttpMessageHandler
{
    private readonly Dictionary<string, FormType> _types = new(StringComparer.Ordinal);

    public FakeFormbase() => Client = new HttpClient(this) { BaseAddress = new Uri("http://formbase.test/") };

    public HttpClient Client { get; }

    /// <summary>The edges' form type.</summary>
    public FormType Edges => Type("edges");

    /// <summary>The latest pass's form type.</summary>
    public FormType Passes => Type("edge_passes");

    public JsonObject? Declaration { get => Edges.Declaration; set => Edges.Declaration = value; }

    public bool Projected => Edges.Projected;

    /// <summary>Every append of an edge, retirements included (a null body).</summary>
    public List<(string Key, JsonObject? Body)> Stream => Edges.Stream;

    /// <summary>The namespace header of every request, or null.</summary>
    public List<string?> Requests { get; } = [];

    public FormType Type(string name)
    {
        if (!_types.TryGetValue(name, out var type))
        {
            _types.Add(name, type = new FormType());
        }

        return type;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.Headers.TryGetValues("Formbase-Namespace", out var ns) ? ns.Single() : null);
        var segments = request.RequestUri!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not ["formtypes", var name, var what])
        {
            throw new InvalidOperationException($"unexpected request {request.Method} {request.RequestUri.PathAndQuery}");
        }

        var type = Type(name);
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
        switch (request.Method.Method, what)
        {
            case ("GET", "declaration"):
                return type.Declaration is null
                    ? Answer(HttpStatusCode.NotFound, new { type = "/problems/no-declaration" })
                    : Answer(HttpStatusCode.OK, type.Declaration);
            case ("PUT", "declaration"):
                type.Declaration = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
                return Answer(HttpStatusCode.Created, new { });
            case ("POST", "documents"):
                type.Stream.Add((query["recordKey"]!, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject()));
                return Answer(HttpStatusCode.Created, new { });
            case ("DELETE", "records"):
                type.Stream.Add((query["recordKey"]!, null));
                return Answer(HttpStatusCode.OK, new { });
            case ("POST", "projection"):
                type.Project();
                return Answer(HttpStatusCode.OK, new { });
            case ("GET", "records"):
                if (!type.Projected)
                {
                    return Answer(HttpStatusCode.Conflict, new { type = "/problems/not-projected" });
                }

                var filters = (query.GetValues("filter") ?? []).Select(f => (Column: f[..f.IndexOf(':', StringComparison.Ordinal)], Value: f[(f.IndexOf(':', StringComparison.Ordinal) + 1)..])).ToList();
                var page = type.Rows.Where(d => filters.All(f => (string?)d.Body![f.Column] == f.Value)).Skip(int.Parse(query["offset"]!, System.Globalization.CultureInfo.InvariantCulture)).Take(int.Parse(query["limit"]!, System.Globalization.CultureInfo.InvariantCulture));
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

    internal sealed class FormType
    {
        public JsonObject? Declaration { get; set; }

        public bool Projected { get; private set; }

        public List<(string Key, JsonObject? Body)> Stream { get; } = [];

        public List<(string Key, JsonObject? Body)> Rows { get; } = [];

        public void Project()
        {
            Projected = true;
            Rows.Clear();
            Rows.AddRange(Stream.GroupBy(d => d.Key).Select(g => g.Last()).Where(d => d.Body is not null).OrderBy(d => d.Key, StringComparer.Ordinal));
        }
    }
}

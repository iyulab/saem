using System.Net;
using System.Text;
using Eyu.Core.Declared;
using Eyu.Core.Primitives;
using Xunit;

namespace Saem.Connectors.Formbase.Tests;

/// <summary>
/// The connector against Formbase's documented HTTP answers, served by a stand-in handler. The live
/// counterpart runs the same reads against a real Formbase instance.
/// </summary>
public class FormbaseConnectorTests
{
    private static readonly SubjectRef WorkOrders = SubjectRef.Create("workorders");

    [Fact]
    public async Task A_declaration_becomes_the_declared_structure_eyu_reads()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/declaration", HttpStatusCode.OK, """
            {
              "formType": "workorders", "tableName": "workorders", "declarationVersion": 3,
              "fields": [
                { "name": "wo", "type": "text", "nullable": false, "sourceKey": null, "binding": "stored", "target": null },
                { "name": "tonnage", "type": "integer", "nullable": true, "sourceKey": null, "binding": "stored", "target": null }
              ],
              "relations": [ { "name": "machine", "kind": "reference", "target": "machines", "keyField": "equipment" } ]
            }
            """);

        var structure = await Connector(formbase).GetStructureAsync(WorkOrders, TestContext.Current.CancellationToken);

        Assert.NotNull(structure);
        Assert.Equal("3", structure.Version);
        Assert.Equal(
            [new DeclaredField("wo", Kind: DeclaredValueKind.Text, Required: true), new DeclaredField("tonnage", Kind: DeclaredValueKind.WholeNumber, Required: false)],
            structure.Fields);
        var relation = Assert.Single(structure.Relations);
        Assert.Equal(new DeclaredRelation("machine", SubjectRef.Create("machines"), ViaField: "equipment", Kind: DeclaredRelationKind.Reference), relation);
    }

    [Fact]
    public async Task A_form_type_formbase_says_has_no_declaration_declares_nothing()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/declaration", HttpStatusCode.NotFound,
            """{ "type": "/problems/no-declaration", "title": "The form type has no declaration", "status": 404 }""");

        Assert.Null(await Connector(formbase).GetStructureAsync(WorkOrders, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// A 404 that is not Formbase saying "no declaration" — a wrong base address, a proxy's page — is a
    /// failure to read. Taken as "nothing declared", it would send Eyu to infer what is declared.
    /// </summary>
    [Fact]
    public async Task Any_other_not_found_is_a_failure_rather_than_an_empty_declaration()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/declaration", HttpStatusCode.NotFound, "<html>not here</html>");

        var ex = await Assert.ThrowsAsync<FormbaseConnectorException>(() => Connector(formbase).GetStructureAsync(WorkOrders, TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task A_sample_reads_the_raw_stream_with_every_field_declared_or_not()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=10", HttpStatusCode.OK, """
            {
              "documents": [
                { "documentId": "c4b57b30-b7d6-492e-ab12-1f7fff054e1f", "formType": "workorders", "watermark": 1, "appendedAt": "2026-09-23T08:06:23+00:00",
                  "body": { "wo": "WO-1", "tonnage": 200, "note": null, "tags": ["urgent"] } },
                { "documentId": "9d2126f5-585d-4753-b644-9727e1cb0fa1", "formType": "workorders", "watermark": 2, "appendedAt": "2026-09-23T08:06:24+00:00",
                  "body": { "wo": "WO-2", "tech": "kim" } }
              ],
              "rawHead": 2
            }
            """);

        var records = await Connector(formbase).SampleAsync(WorkOrders, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["c4b57b30-b7d6-492e-ab12-1f7fff054e1f", "9d2126f5-585d-4753-b644-9727e1cb0fa1"], records.Select(r => r.Id));
        Assert.Equal("WO-1", records[0].Fields["wo"]);
        Assert.Equal("200", records[0].Fields["tonnage"]);
        Assert.Null(records[0].Fields["note"]);
        Assert.Equal("""["urgent"]""", records[0].Fields["tags"]);
        Assert.Equal("kim", records[1].Fields["tech"]);
        Assert.Single(formbase.Requests);
    }

    [Fact]
    public async Task A_sample_follows_the_watermark_until_it_has_enough_or_reaches_the_head()
    {
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/workorders/documents?after=0&limit=3", HttpStatusCode.OK, Page(head: 5, 1, 2))
            .Answer("GET", "/formtypes/workorders/documents?after=2&limit=1", HttpStatusCode.OK, Page(head: 5, 3));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, records.Count);
        Assert.Equal(2, formbase.Requests.Count);
    }

    [Fact]
    public async Task A_sample_stops_at_the_head_when_the_form_type_has_fewer_documents()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=50", HttpStatusCode.OK, Page(head: 2, 1, 2));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 50, TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.Single(formbase.Requests);
    }

    /// <summary>
    /// A Formbase from before the raw stream was served answers the read with 405 (the route exists
    /// only for intake). The message says what the instance lacks, and which release serves it,
    /// instead of a bare status.
    /// </summary>
    [Fact]
    public async Task A_formbase_that_does_not_serve_the_raw_stream_is_named_as_such()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=5", HttpStatusCode.MethodNotAllowed, "");

        var ex = await Assert.ThrowsAsync<FormbaseConnectorException>(() => Connector(formbase).SampleAsync(WorkOrders, 5, TestContext.Current.CancellationToken));
        Assert.Contains("raw stream", ex.Message);
        Assert.Contains("0.11.0", ex.Message);
    }

    [Fact]
    public async Task The_namespace_travels_on_every_request()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=1", HttpStatusCode.OK, Page(head: 1, 1));

        await new FormbaseConnector(formbase.Client, "tenant-a").SampleAsync(WorkOrders, 1, TestContext.Current.CancellationToken);

        Assert.Equal(["tenant-a"], formbase.Requests.Single().Headers.GetValues("Formbase-Namespace"));
    }

    /// <summary>
    /// A connector given a namespace and pointed at a host serving another one is refused with 404.
    /// On the declaration read that 404 must stay a failure: taken for "no declaration", the connector
    /// would report nothing declared about a host it was never meant to read.
    /// </summary>
    [Fact]
    public async Task A_host_serving_another_namespace_is_a_failure_on_both_reads()
    {
        const string UnknownNamespace = """{ "type": "/problems/unknown-namespace", "title": "No such namespace on this host", "status": 404, "detail": "This host serves the namespace 'store'." }""";
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/workorders/declaration", HttpStatusCode.NotFound, UnknownNamespace)
            .Answer("GET", "/formtypes/workorders/documents?after=0&limit=5", HttpStatusCode.NotFound, UnknownNamespace);
        var connector = new FormbaseConnector(formbase.Client, "source");

        var declaration = await Assert.ThrowsAsync<FormbaseConnectorException>(() => connector.GetStructureAsync(WorkOrders, TestContext.Current.CancellationToken));
        var sample = await Assert.ThrowsAsync<FormbaseConnectorException>(() => connector.SampleAsync(WorkOrders, 5, TestContext.Current.CancellationToken));

        Assert.Contains("/problems/unknown-namespace", declaration.Message);
        Assert.Equal(HttpStatusCode.NotFound, sample.StatusCode);
    }

    [Fact]
    public async Task A_refused_request_carries_formbases_problem()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=5", HttpStatusCode.BadRequest,
            """{ "type": "/problems/invalid-request", "detail": "limit must be between 0 and 1000.", "status": 400 }""");

        var ex = await Assert.ThrowsAsync<FormbaseConnectorException>(() => Connector(formbase).SampleAsync(WorkOrders, 5, TestContext.Current.CancellationToken));
        Assert.Contains("/problems/invalid-request", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    private static FormbaseConnector Connector(StubFormbase formbase) => new(formbase.Client);

    private static string Page(long head, params long[] watermarks) =>
        "{ \"documents\": [" + string.Join(",", watermarks.Select(w =>
            $$"""{ "documentId": "{{Guid.NewGuid()}}", "formType": "workorders", "watermark": {{w}}, "appendedAt": "2026-09-23T08:00:00+00:00", "body": { "n": {{w}} } }"""))
        + "], \"rawHead\": " + head + " }";

    private sealed class StubFormbase : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public StubFormbase() => Client = new HttpClient(this) { BaseAddress = new Uri("http://formbase.test/") };

        public HttpClient Client { get; }

        public List<HttpRequestMessage> Requests { get; } = [];

        public StubFormbase Answer(string method, string pathAndQuery, HttpStatusCode status, string body)
        {
            _answers[$"{method} {pathAndQuery}"] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            if (!_answers.TryGetValue(key, out var answer))
            {
                throw new InvalidOperationException($"unexpected request {key}");
            }

            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                RequestMessage = request,
                Content = new StringContent(answer.Body, Encoding.UTF8, answer.Body.StartsWith('<') ? "text/html" : "application/json"),
            });
        }
    }
}

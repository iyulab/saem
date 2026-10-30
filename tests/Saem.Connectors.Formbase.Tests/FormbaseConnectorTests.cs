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
                { "name": "tonnage", "type": "integer", "nullable": true, "sourceKey": null, "binding": "stored", "target": null },
                { "name": "due", "type": "date", "nullable": true, "sourceKey": null, "binding": "stored", "target": null }
              ],
              "relations": [ { "name": "machine", "kind": "reference", "target": "machines", "keyField": "equipment" } ]
            }
            """);

        var structure = await Connector(formbase).GetStructureAsync(WorkOrders, TestContext.Current.CancellationToken);

        Assert.NotNull(structure);
        Assert.Equal("3", structure.Version);
        Assert.Equal(
            [new DeclaredField("wo", Kind: DeclaredValueKind.Text, Required: true), new DeclaredField("tonnage", Kind: DeclaredValueKind.WholeNumber, Required: false),
             new DeclaredField("due", Kind: DeclaredValueKind.Date, Required: false)],
            structure.Fields);
        var relation = Assert.Single(structure.Relations);
        Assert.Equal(new DeclaredRelation("machine", SubjectRef.Create("machines"), ViaField: "equipment", Kind: DeclaredRelationKind.Reference), relation);
    }

    /// <summary>
    /// A field bound to another form type is a declared reference: it reaches Eyu as a reference relation
    /// through that field, and through the field carrying the target's key when the binding names one.
    /// </summary>
    [Fact]
    public async Task A_bound_field_becomes_a_reference_relation_through_the_fields_that_carry_it()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/declaration", HttpStatusCode.OK, """
            {
              "formType": "workorders", "tableName": "workorders", "declarationVersion": 4,
              "fields": [
                { "name": "equipment", "type": "text", "nullable": true, "sourceKey": null, "binding": "stored", "target": null },
                { "name": "machine", "type": "text", "nullable": true, "sourceKey": null, "binding": "snapshot",
                  "target": { "formType": "machines", "valueField": "name", "lookupKey": "number", "viaField": "equipment" } },
                { "name": "site", "type": "text", "nullable": true, "sourceKey": null, "binding": "snapshot",
                  "target": { "formType": "sites", "valueField": "name", "lookupKey": null, "viaField": null } }
              ],
              "relations": []
            }
            """);

        var structure = await Connector(formbase).GetStructureAsync(WorkOrders, TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new DeclaredRelation("machine", SubjectRef.Create("machines"), ViaField: "machine", Kind: DeclaredRelationKind.Reference),
                new DeclaredRelation("equipment", SubjectRef.Create("machines"), ViaField: "equipment", Kind: DeclaredRelationKind.Reference),
                new DeclaredRelation("site", SubjectRef.Create("sites"), ViaField: "site", Kind: DeclaredRelationKind.Reference),
            ],
            structure!.Relations);
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
        Assert.All(records, r => Assert.Equal(WorkOrders, r.Subject));
        Assert.Single(formbase.StreamReads);
    }

    [Fact]
    public async Task A_sample_follows_the_watermark_until_it_has_enough_or_reaches_the_head()
    {
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/workorders/documents?after=0&limit=3", HttpStatusCode.OK, Page(head: 5, 1, 2))
            .Answer("GET", "/formtypes/workorders/documents?after=2&limit=1", HttpStatusCode.OK, Page(head: 5, 3));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 3, TestContext.Current.CancellationToken);

        Assert.Equal(3, records.Count);
        Assert.Equal(2, formbase.StreamReads.Count);
    }

    [Fact]
    public async Task A_sample_stops_at_the_head_when_the_form_type_has_fewer_documents()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=50", HttpStatusCode.OK, Page(head: 2, 1, 2));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 50, TestContext.Current.CancellationToken);

        Assert.Equal(2, records.Count);
        Assert.Single(formbase.StreamReads);
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

    /// <summary>
    /// A repeated section has no documents of its own: its declaration names the parent, and its records
    /// are the parent's section items — each named by the document and its position, carrying the
    /// parent's key field value. A parent with no section has no rows, and one with an item that is not
    /// a row has none read — the projection skips it. Its structure leads back to the parent through that field.
    /// </summary>
    [Fact]
    public async Task A_repeated_section_is_sampled_as_rows_cut_from_its_parent_and_leads_back_to_it()
    {
        var lines = SubjectRef.Create("orderlines");
        const string Declaration = """
            {
              "formType": "orderlines", "tableName": "orderlines", "declarationVersion": 1,
              "fields": [ { "name": "orderNo", "type": "text", "nullable": true }, { "name": "product", "type": "text", "nullable": true },
                          { "name": "qty", "type": "integer", "nullable": true } ],
              "relations": [],
              "sectionOf": { "formType": "orders", "relation": "lines", "sectionField": "lines", "keyField": "orderNo" }
            }
            """;
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/orderlines/declaration", HttpStatusCode.OK, Declaration)
            .Answer("GET", "/formtypes/orders/documents?after=0&limit=10", HttpStatusCode.OK, """
                { "documents": [
                  { "documentId": "7b0c2f1e-3d4a-4b5c-8d6e-9f0a1b2c3d4e", "watermark": 1, "body": { "orderNo": "A-7", "lines": [ { "product": "bolt", "orderNo": "X" }, { "product": "nut" } ] } },
                  { "documentId": "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d", "watermark": 2, "body": { "orderNo": "A-8" } },
                  { "documentId": "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f", "watermark": 3, "body": { "orderNo": "A-9", "lines": [ { "product": "washer" }, 3 ] } }
                ], "rawHead": 3 }
                """);
        var connector = Connector(formbase);

        var records = await connector.SampleAsync(lines, 10, TestContext.Current.CancellationToken);
        var structure = await connector.GetStructureAsync(lines, TestContext.Current.CancellationToken);

        Assert.Equal(["7b0c2f1e-3d4a-4b5c-8d6e-9f0a1b2c3d4e/0", "7b0c2f1e-3d4a-4b5c-8d6e-9f0a1b2c3d4e/1"], records.Select(r => r.Id));
        Assert.Equal(("bolt", "A-7"), (records[0].Fields["product"], records[0].Fields["orderNo"]));
        Assert.Equal(("nut", "A-7"), (records[1].Fields["product"], records[1].Fields["orderNo"]));
        Assert.All(records, r => Assert.Equal(lines, r.Subject));
        Assert.Equal(
            new DeclaredRelation("lines", SubjectRef.Create("orders"), ViaField: "orderNo", Kind: DeclaredRelationKind.Parent),
            Assert.Single(structure!.Relations));
    }

    /// <summary>The array a declared section is held in is the section's records, not a field of the parent.</summary>
    [Fact]
    public async Task A_parent_is_sampled_without_the_array_its_section_is_held_in()
    {
        var orders = SubjectRef.Create("orders");
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/orders/declaration", HttpStatusCode.OK, """
                {
                  "formType": "orders", "tableName": "orders", "declarationVersion": 1,
                  "fields": [ { "name": "orderNo", "type": "text", "nullable": true } ],
                  "relations": [ { "name": "lines", "kind": "child", "target": "orderlines", "keyField": "orderNo", "sectionField": "lines" } ]
                }
                """)
            .Answer("GET", "/formtypes/orders/documents?after=0&limit=5", HttpStatusCode.OK, """
                { "documents": [ { "documentId": "7b0c2f1e-3d4a-4b5c-8d6e-9f0a1b2c3d4e", "watermark": 1, "body": { "orderNo": "A-7", "notes": [ "rush" ], "lines": [ { "product": "bolt" } ] } } ], "rawHead": 1 }
                """);

        var record = Assert.Single(await Connector(formbase).SampleAsync(orders, 5, TestContext.Current.CancellationToken));

        Assert.Equal(["orderNo", "notes"], record.Fields.Keys);
    }

    /// <summary>In a section of values an item that is a value is the section's one field beside the key.</summary>
    [Fact]
    public async Task A_section_of_values_samples_each_value_as_its_one_field()
    {
        var tags = SubjectRef.Create("ordertags");
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/ordertags/declaration", HttpStatusCode.OK, """
                {
                  "formType": "ordertags", "tableName": "ordertags", "declarationVersion": 1,
                  "fields": [ { "name": "orderNo", "type": "text", "nullable": true }, { "name": "tag", "type": "text", "nullable": true } ],
                  "relations": [],
                  "sectionOf": { "formType": "orders", "relation": "tags", "sectionField": "tags", "keyField": "orderNo" }
                }
                """)
            .Answer("GET", "/formtypes/orders/documents?after=0&limit=5", HttpStatusCode.OK, """
                { "documents": [ { "documentId": "7b0c2f1e-3d4a-4b5c-8d6e-9f0a1b2c3d4e", "watermark": 1, "body": { "orderNo": "A-7", "tags": [ "rush", "fragile" ] } } ], "rawHead": 1 }
                """);

        var records = await Connector(formbase).SampleAsync(tags, 5, TestContext.Current.CancellationToken);

        Assert.Equal(["rush", "fragile"], records.Select(r => r.Fields["tag"]));
        Assert.All(records, r => Assert.Equal("A-7", r.Fields["orderNo"]));
    }

    [Fact]
    public async Task The_namespace_travels_on_every_request()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=1", HttpStatusCode.OK, Page(head: 1, 1));

        await new FormbaseConnector(formbase.Client, "tenant-a", SampleDirection.Oldest).SampleAsync(WorkOrders, 1, TestContext.Current.CancellationToken);

        Assert.Equal(2, formbase.Requests.Count);
        Assert.All(formbase.Requests, r => Assert.Equal(["tenant-a"], r.Headers.GetValues("Formbase-Namespace")));
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
        var connector = new FormbaseConnector(formbase.Client, "source", SampleDirection.Oldest);

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

    [Fact]
    public async Task A_corrected_record_is_sampled_once_as_corrected()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=10", HttpStatusCode.OK,
            KeyedPage(head: 3, (1, "a", false, 1), (2, "a", false, 2), (3, null, false, 3)));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["2", "3"], records.Select(r => r.Fields["n"]));
    }

    [Fact]
    public async Task A_retired_record_is_not_sampled()
    {
        var formbase = new StubFormbase().Answer("GET", "/formtypes/workorders/documents?after=0&limit=10", HttpStatusCode.OK,
            KeyedPage(head: 3, (1, "a", false, 1), (2, "b", false, 2), (3, "a", true, null)));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["2"], records.Select(r => r.Fields["n"]));
    }

    [Fact]
    public async Task Documents_folded_away_do_not_count_toward_the_sample()
    {
        var formbase = new StubFormbase()
            .Answer("GET", "/formtypes/workorders/documents?after=0&limit=2", HttpStatusCode.OK,
                KeyedPage(head: 3, (1, "a", false, 1), (2, "a", false, 2)))
            .Answer("GET", "/formtypes/workorders/documents?after=2&limit=1", HttpStatusCode.OK,
                KeyedPage(head: 3, (3, "b", false, 3)));

        var records = await Connector(formbase).SampleAsync(WorkOrders, 2, TestContext.Current.CancellationToken);

        Assert.Equal(["2", "3"], records.Select(r => r.Fields["n"]));
    }

    // These stand-in answers are the start of the stream, so they read it from there; the most recent
    // records are read in FormbaseConnectorSampleDirectionTests.
    private static FormbaseConnector Connector(StubFormbase formbase) => new(formbase.Client, sample: SampleDirection.Oldest);

    private static string Page(long head, params long[] watermarks) =>
        "{ \"documents\": [" + string.Join(",", watermarks.Select(w =>
            $$"""{ "documentId": "{{Guid.NewGuid()}}", "formType": "workorders", "watermark": {{w}}, "appendedAt": "2026-09-23T08:00:00+00:00", "body": { "n": {{w}} } }"""))
        + "], \"rawHead\": " + head + " }";

    private static string KeyedPage(long head, params (long Watermark, string? Key, bool Retired, int? N)[] documents) =>
        "{ \"documents\": [" + string.Join(",", documents.Select(d =>
            $$"""{ "documentId": "{{Guid.NewGuid()}}", "formType": "workorders", "watermark": {{d.Watermark}}, "appendedAt": "2026-09-29T08:00:00+00:00", "body": {{(d.N is { } n ? $$"""{ "n": {{n}} }""" : "null")}}, "recordKey": {{(d.Key is null ? "null" : $"\"{d.Key}\"")}}, "retired": {{(d.Retired ? "true" : "false")}} }"""))
        + "], \"rawHead\": " + head + " }";

    private sealed class StubFormbase : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _answers = new(StringComparer.Ordinal);

        public StubFormbase() => Client = new HttpClient(this) { BaseAddress = new Uri("http://formbase.test/") };

        public HttpClient Client { get; }

        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>The requests that read a raw stream — a sample also reads the declaration first.</summary>
        public List<HttpRequestMessage> StreamReads => [.. Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("/documents", StringComparison.Ordinal))];

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
                // A form type nothing was said about has no declaration, as Formbase says it.
                answer = request.RequestUri.AbsolutePath.EndsWith("/declaration", StringComparison.Ordinal)
                    ? (HttpStatusCode.NotFound, """{ "type": "/problems/no-declaration", "status": 404 }""")
                    : throw new InvalidOperationException($"unexpected request {key}");
            }

            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                RequestMessage = request,
                Content = new StringContent(answer.Body, Encoding.UTF8, answer.Body.StartsWith('<') ? "text/html" : "application/json"),
            });
        }
    }
}

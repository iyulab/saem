using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eyu.Core.Primitives;
using Xunit;

namespace Saem.Connectors.Formbase.Tests;

/// <summary>
/// Which end of a form type's stream a sample is taken from, against a stand-in that serves the raw
/// stream the way Formbase documents it: one watermark sequence across every form type, <c>after</c>
/// and <c>limit</c> honoured, <c>limit=0</c> answering only the form type's head.
/// </summary>
public class FormbaseConnectorSampleDirectionTests
{
    private static readonly SubjectRef WorkOrders = SubjectRef.Create("workorders");

    /// <summary>
    /// A form revised after three hundred documents were filled in on the original: a sample of the
    /// earliest hundred never sees a field of the revision, a sample of the most recent hundred sees all
    /// sixty documents that carry it. Both come back in the order Formbase accepted them.
    /// </summary>
    [Fact]
    public async Task A_form_revised_late_in_its_stream_is_seen_through_its_most_recent_records()
    {
        var formbase = new InMemoryFormbase();
        for (var i = 1; i <= 300; i++)
        {
            formbase.Append("workorders", new JsonObject { ["wo"] = $"WO-{i}", ["tonnage"] = i });
        }

        for (var i = 301; i <= 360; i++)
        {
            formbase.Append("workorders", new JsonObject { ["wo"] = $"WO-{i}", ["tonnage"] = i, ["crane"] = "C-1" });
        }

        var earliest = await Connector(formbase, SampleDirection.Oldest).SampleAsync(WorkOrders, 100, TestContext.Current.CancellationToken);
        var latest = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 100, TestContext.Current.CancellationToken);

        Assert.Equal(100, earliest.Count);
        Assert.DoesNotContain(earliest, r => r.Fields.ContainsKey("crane"));
        Assert.Equal(100, latest.Count);
        Assert.Equal(60, latest.Count(r => r.Fields.ContainsKey("crane")));
        Assert.Equal(Enumerable.Range(261, 100).Select(i => $"WO-{i}"), latest.Select(r => r.Fields["wo"]));
    }

    [Fact]
    public async Task The_most_recent_records_are_the_default()
    {
        var formbase = new InMemoryFormbase();
        for (var i = 1; i <= 5; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = i });
        }

        var records = await new FormbaseConnector(formbase.Client).SampleAsync(WorkOrders, 2, TestContext.Current.CancellationToken);

        Assert.Equal(["4", "5"], records.Select(r => r.Fields["n"]));
    }

    /// <summary>
    /// Sampling from the end reads the head, then only as much of the stream below it as the sample
    /// needs — not the stream from its start.
    /// </summary>
    [Fact]
    public async Task Only_the_stretch_of_the_stream_the_sample_needs_is_read()
    {
        var formbase = new InMemoryFormbase();
        for (var i = 1; i <= 5000; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = i });
        }

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 20, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(4981, 20).Select(i => i.ToString(CultureInfo.InvariantCulture)), records.Select(r => r.Fields["n"]));
        Assert.Equal(["after=0&limit=0", "after=4872&limit=128"], formbase.StreamReads);
        Assert.Equal(128, formbase.DocumentsServed);
    }

    /// <summary>
    /// Watermarks number every form type's documents in one sequence, so a stretch of the stream can hold
    /// few of this form type's: the stretch widens until the sample is full.
    /// </summary>
    [Fact]
    public async Task A_form_type_sparse_in_a_shared_stream_is_sampled_by_widening_the_stretch()
    {
        var formbase = new InMemoryFormbase();
        for (var i = 1; i <= 400; i++)
        {
            formbase.Append(i % 10 == 0 ? "workorders" : "other", new JsonObject { ["n"] = i });
        }

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 15, TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(26, 15).Select(i => (i * 10).ToString(CultureInfo.InvariantCulture)), records.Select(r => r.Fields["n"]));

        // The head, the first stretch of 128 (about thirteen of the form type's documents, fewer than the sample wants), and one twice as wide.
        Assert.Equal(["after=0&limit=0", "after=272&limit=128", "after=16&limit=256"], formbase.StreamReads);
    }

    [Fact]
    public async Task A_form_type_with_fewer_documents_than_the_sample_is_read_in_full()
    {
        var formbase = new InMemoryFormbase();
        formbase.Append("other", new JsonObject { ["n"] = 0 });
        for (var i = 1; i <= 3; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = i });
        }

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 50, TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3"], records.Select(r => r.Fields["n"]));
    }

    [Fact]
    public async Task A_form_type_with_no_documents_is_a_head_read_and_an_empty_sample()
    {
        var formbase = new InMemoryFormbase();
        formbase.Append("other", new JsonObject { ["n"] = 0 });

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 10, TestContext.Current.CancellationToken);

        Assert.Empty(records);
        Assert.Equal(["after=0&limit=0"], formbase.StreamReads);
    }

    [Fact]
    public async Task A_sample_of_nothing_reads_nothing()
    {
        var formbase = new InMemoryFormbase();
        formbase.Append("workorders", new JsonObject { ["n"] = 1 });

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 0, TestContext.Current.CancellationToken);

        Assert.Empty(records);
        Assert.Empty(formbase.StreamReads);
    }

    /// <summary>
    /// A document accepted while the sample is being read is not part of it: the head is read once and
    /// the stretches below it are disjoint.
    /// </summary>
    [Fact]
    public async Task Documents_accepted_while_the_sample_is_read_are_not_part_of_it()
    {
        var formbase = new InMemoryFormbase();
        for (var i = 1; i <= 100; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = i });
        }

        formbase.AfterHeadRead = () => formbase.Append("workorders", new JsonObject { ["n"] = 101 });

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 3, TestContext.Current.CancellationToken);

        Assert.Equal(["98", "99", "100"], records.Select(r => r.Fields["n"]));
    }

    /// <summary>
    /// Both ends fold a stream into the same standing records: the latest document of a record stands, a
    /// retired record does not, and a retirement read in one stretch keeps the record's older documents,
    /// which lie in stretches read later, from standing again.
    /// </summary>
    [Fact]
    public async Task Both_ends_fold_a_corrected_and_retired_stream_into_the_same_records()
    {
        var formbase = new InMemoryFormbase();
        formbase.Append("workorders", new JsonObject { ["n"] = "a-1" }, key: "a");
        for (var i = 1; i <= 200; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = $"x-{i}" }, key: "x");
        }

        formbase.Append("workorders", null, key: "a", retired: true);
        for (var i = 1; i <= 10; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = $"f-{i}" });
        }

        var fromStart = await Connector(formbase, SampleDirection.Oldest).SampleAsync(WorkOrders, 128, TestContext.Current.CancellationToken);
        var fromEnd = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 128, TestContext.Current.CancellationToken);

        Assert.Equal(11, fromStart.Count);
        Assert.Equal(fromStart.Select(r => r.Id), fromEnd.Select(r => r.Id));
        Assert.Equal(["x-200", "f-1", "f-2", "f-3", "f-4", "f-5", "f-6", "f-7", "f-8", "f-9", "f-10"], fromEnd.Select(r => r.Fields["n"]));
    }

    /// <summary>A record corrected after the sample's older half is the record as corrected, wherever it was first filled in.</summary>
    [Fact]
    public async Task A_record_corrected_late_is_sampled_as_corrected_and_only_once()
    {
        var formbase = new InMemoryFormbase();
        formbase.Append("workorders", new JsonObject { ["n"] = "first" }, key: "k");
        for (var i = 1; i <= 5; i++)
        {
            formbase.Append("workorders", new JsonObject { ["n"] = $"w-{i}" });
        }

        formbase.Append("workorders", new JsonObject { ["n"] = "corrected" }, key: "k");

        var records = await Connector(formbase, SampleDirection.Newest).SampleAsync(WorkOrders, 4, TestContext.Current.CancellationToken);

        Assert.Equal(["w-3", "w-4", "w-5", "corrected"], records.Select(r => r.Fields["n"]));
    }

    /// <summary>
    /// A repeated section has no documents of its own: the direction chooses among its parent's, and the
    /// rows returned are the last of those cut from the chosen parents when sampling from the end.
    /// </summary>
    [Fact]
    public async Task A_repeated_section_is_sampled_from_the_end_of_its_parents()
    {
        var formbase = new InMemoryFormbase();
        formbase.Declare("orderlines", """
            {
              "formType": "orderlines", "tableName": "orderlines", "declarationVersion": 1,
              "fields": [ { "name": "orderNo", "type": "text", "nullable": true }, { "name": "product", "type": "text", "nullable": true } ],
              "relations": [],
              "sectionOf": { "formType": "orders", "relation": "lines", "sectionField": "lines", "keyField": "orderNo" }
            }
            """);
        for (var order = 1; order <= 4; order++)
        {
            formbase.Append("orders", new JsonObject
            {
                ["orderNo"] = $"A-{order}",
                ["lines"] = new JsonArray(new JsonObject { ["product"] = $"p{order}a" }, new JsonObject { ["product"] = $"p{order}b" }),
            });
        }

        var lines = SubjectRef.Create("orderlines");
        var fromEnd = await Connector(formbase, SampleDirection.Newest).SampleAsync(lines, 3, TestContext.Current.CancellationToken);
        var fromStart = await Connector(formbase, SampleDirection.Oldest).SampleAsync(lines, 3, TestContext.Current.CancellationToken);

        Assert.Equal(["p3b", "p4a", "p4b"], fromEnd.Select(r => r.Fields["product"]));
        Assert.Equal(["p1a", "p1b", "p2a"], fromStart.Select(r => r.Fields["product"]));
    }

    private static FormbaseConnector Connector(InMemoryFormbase formbase, SampleDirection sample) => new(formbase.Client, sample: sample);

    private sealed class InMemoryFormbase : HttpMessageHandler
    {
        private readonly List<(long Watermark, Guid Id, string FormType, JsonNode? Body, string? Key, bool Retired)> _documents = [];
        private readonly Dictionary<string, string> _declarations = new(StringComparer.Ordinal);

        public InMemoryFormbase() => Client = new HttpClient(this) { BaseAddress = new Uri("http://formbase.test/") };

        public HttpClient Client { get; }

        /// <summary>The query of each read of a raw stream, in order.</summary>
        public List<string> StreamReads { get; } = [];

        /// <summary>How many documents the raw stream has served.</summary>
        public int DocumentsServed { get; private set; }

        /// <summary>Runs once, after the first read of a form type's head.</summary>
        public Action? AfterHeadRead { get; set; }

        public void Append(string formType, JsonNode? body, string? key = null, bool retired = false) =>
            _documents.Add((_documents.Count + 1, Guid.NewGuid(), formType, body, key, retired));

        public void Declare(string formType, string declaration) => _declarations[formType] = declaration;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
            if (path is ["formtypes", var type, "declaration"])
            {
                return Task.FromResult(_declarations.TryGetValue(type, out var declaration)
                    ? Reply(request, HttpStatusCode.OK, declaration)
                    : Reply(request, HttpStatusCode.NotFound, """{ "type": "/problems/no-declaration", "status": 404 }"""));
            }

            if (path is not ["formtypes", var formType, "documents"])
            {
                throw new InvalidOperationException($"unexpected request {request.RequestUri.PathAndQuery}");
            }

            var query = request.RequestUri.Query.TrimStart('?');
            StreamReads.Add(query);
            var parameters = query.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
            var after = long.Parse(parameters["after"], CultureInfo.InvariantCulture);
            var limit = int.Parse(parameters["limit"], CultureInfo.InvariantCulture);

            var ofType = _documents.Where(d => d.FormType == formType).ToList();
            var page = ofType.Where(d => d.Watermark > after).Take(limit).ToList();
            DocumentsServed += page.Count;
            var head = ofType.Count == 0 ? 0 : ofType[^1].Watermark;
            var body = new JsonObject
            {
                ["documents"] = new JsonArray([.. page.Select(d => (JsonNode)new JsonObject
                {
                    ["documentId"] = d.Id,
                    ["formType"] = d.FormType,
                    ["watermark"] = d.Watermark,
                    ["body"] = d.Body?.DeepClone(),
                    ["recordKey"] = d.Key,
                    ["retired"] = d.Retired,
                })]),
                ["rawHead"] = head,
            };

            if (limit == 0 && AfterHeadRead is { } action)
            {
                AfterHeadRead = null;
                action();
            }

            return Task.FromResult(Reply(request, HttpStatusCode.OK, body.ToJsonString()));
        }

        private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string body) => new(status)
        {
            RequestMessage = request,
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }
}

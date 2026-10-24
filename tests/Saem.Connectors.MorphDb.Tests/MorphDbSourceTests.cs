using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Saem.Connectors.MorphDb.Tests;

/// <summary>What the adapter sends a MorphDB source, and how it reads the answer, against a recorded fake.</summary>
public class MorphDbSourceTests
{
    private static readonly Guid Project = Guid.Parse("3f1c2a7e-0000-4000-8000-000000000001");

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Page(bool hasNext, params object[] rows) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            data = rows.Select(r => new { id = Guid.NewGuid(), data = r }),
            pagination = new { page = 1, pageSize = 1000, hasNext },
        }), Encoding.UTF8, "application/json"),
    };

    private static (MorphDbSource Source, Recorder Recorder) Source(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var recorder = new Recorder(answer);
        var http = new HttpClient(recorder) { BaseAddress = new Uri("http://morphdb.test/") };
        return (new MorphDbSource(http, Project), recorder);
    }

    [Fact]
    public void A_client_that_already_carries_a_credential_is_refused()
    {
        var http = new HttpClient { BaseAddress = new Uri("http://morphdb.test/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", "shared");

        var refused = Assert.Throws<ArgumentException>(() => new MorphDbSource(http, Project));
        Assert.Contains("not who reads", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_client_without_a_base_address_or_a_source_without_a_project_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new MorphDbSource(new HttpClient(), Project));
        Assert.Throws<ArgumentException>(() => new MorphDbSource(new HttpClient { BaseAddress = new Uri("http://m/") }, Guid.Empty));
    }

    [Fact]
    public void A_read_asks_by_key_for_the_key_and_the_asked_columns_with_every_part_escaped()
    {
        var path = MorphDbSourceSession.ReadByKey("fb_inspection", "fb_doc_id", "a:b&c", ["result", "fb_doc_id"], 2);

        Assert.Equal(
            "api/data/fb_inspection?filter=fb_doc_id%3Aeq%3Aa%3Ab%26c&select=fb_doc_id%2Cresult&page=2&pageSize=1000",
            path);
    }

    [Fact]
    public void A_find_asks_by_a_field_in_key_order_with_every_part_escaped()
    {
        var path = MorphDbSourceSession.ReadWhere("orders", "wo_no", "note", "a:b&c", ["note"], 1, 51);

        Assert.Equal(
            "api/data/orders?filter=note%3Aeq%3Aa%3Ab%26c&select=wo_no%2Cnote&orderBy=wo_no%3Aasc&page=1&pageSize=51",
            path);
    }

    /// <summary>SD-08: the latest change is the first row of the latest-first order of rows that have one; changed rows are read at or after it.</summary>
    [Fact]
    public void A_pass_over_changed_rows_asks_the_latest_change_and_reads_from_it_in_key_order()
    {
        Assert.Equal(
            "api/data/orders?filter=updated_at%3Aisnotnull&select=updated_at&orderBy=updated_at%3Adesc&page=1&pageSize=1",
            MorphDbSourceSession.ReadLatest("orders", "updated_at", 1));
        Assert.Equal(
            "api/data/orders?filter=updated_at%3Agte%3A2026-10-25T01%3A00%3A00Z&select=wo_no%2Citem&orderBy=wo_no%3Aasc&page=2&pageSize=1000",
            MorphDbSourceSession.ReadChanged("orders", "wo_no", ["item"], "updated_at", "2026-10-25T01:00:00Z", 2));
    }

    /// <summary>A find stops at its limit, though the source has a next page.</summary>
    [Fact]
    public async Task A_find_stops_at_its_limit()
    {
        var (source, recorder) = Source(_ => Page(true, new { k = "a" }, new { k = "b" }));
        await using var session = await source.As(_ => ValueTask.FromResult("t"))(TestContext.Current.CancellationToken);

        var rows = await session.FindAsync("t", "k", "v", "x", [], 2, TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], rows.Select(r => r.Key));
        Assert.Single(recorder.Requests);
    }

    [Fact]
    public async Task Every_request_presents_the_persons_token_and_the_project_and_only_reads()
    {
        var (source, recorder) = Source(_ => Page(false, new { fb_doc_id = "d1", result = "pass" }));
        await using var session = await source.As(_ => ValueTask.FromResult("alice-token"))(TestContext.Current.CancellationToken);

        var rows = await session.ReadAsync("fb_inspection", "fb_doc_id", "d1", ["result"], TestContext.Current.CancellationToken);

        var request = Assert.Single(recorder.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("alice-token", request.Headers.Authorization.Parameter);
        Assert.Equal(Project.ToString(), Assert.Single(request.Headers.GetValues("X-Project-Id")));

        var row = Assert.Single(rows);
        Assert.Equal("d1", row.Key);
        Assert.Equal("pass", row.Values["result"].GetString());
    }

    [Fact]
    public async Task A_key_that_is_not_text_is_read_as_its_text_and_a_missing_column_as_null()
    {
        var (source, _) = Source(_ => Page(false, new { wo_no = 42 }));
        await using var session = await source.As(_ => ValueTask.FromResult("t"))(TestContext.Current.CancellationToken);

        var row = Assert.Single(await session.ReadAsync("orders", "wo_no", "42", ["qty"], TestContext.Current.CancellationToken));

        Assert.Equal("42", row.Key);
        Assert.Equal(JsonValueKind.Null, row.Values["qty"].ValueKind);
    }

    [Fact]
    public async Task A_read_follows_the_pages_until_none_follows()
    {
        var pages = new Queue<HttpResponseMessage>([
            Page(true, new { k = "a" }),
            Page(false, new { k = "a" }),
        ]);
        var (source, recorder) = Source(_ => pages.Dequeue());
        await using var session = await source.As(_ => ValueTask.FromResult("t"))(TestContext.Current.CancellationToken);

        var rows = await session.ReadAsync("t", "k", "a", [], TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
        Assert.EndsWith("page=2&pageSize=1000", recorder.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scan_asks_for_every_row_in_key_order_and_follows_the_pages()
    {
        var pages = new Queue<HttpResponseMessage>([
            Page(true, new { wo_no = "WO-1", item_code = "I-1" }),
            Page(false, new { wo_no = "WO-2", item_code = "I-2" }),
        ]);
        var (source, recorder) = Source(_ => pages.Dequeue());
        await using var session = await source.As(_ => ValueTask.FromResult("group-token"))(TestContext.Current.CancellationToken);

        var rows = await session.ScanAsync("work_orders", "wo_no", ["item_code"], TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["WO-1", "WO-2"], rows.Select(r => r.Key));
        Assert.Equal("I-2", rows[1].Values["item_code"].GetString());
        Assert.Equal(
            "api/data/work_orders?select=wo_no%2Citem_code&orderBy=wo_no%3Aasc&page=1&pageSize=1000",
            recorder.Requests[0].RequestUri!.PathAndQuery.TrimStart('/'));
        Assert.EndsWith("page=2&pageSize=1000", recorder.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.All(recorder.Requests, r => Assert.Equal("group-token", r.Headers.Authorization!.Parameter));
    }

    [Fact]
    public async Task No_row_the_person_may_see_is_an_empty_answer_not_an_error()
    {
        var (source, _) = Source(_ => Page(false));
        await using var session = await source.As(_ => ValueTask.FromResult("t"))(TestContext.Current.CancellationToken);

        Assert.Empty(await session.ReadAsync("t", "k", "hidden", [], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_refused_read_is_an_error_that_says_how_the_source_answered(HttpStatusCode status)
    {
        var (source, _) = Source(_ => new HttpResponseMessage(status));
        await using var session = await source.As(_ => ValueTask.FromResult("t"))(TestContext.Current.CancellationToken);

        var failed = await Assert.ThrowsAsync<MorphDbSourceException>(
            async () => await session.ReadAsync("t", "k", "v", [], TestContext.Current.CancellationToken));
        Assert.Equal(status, failed.Status);
    }

    [Fact]
    public async Task A_session_is_not_opened_without_a_token()
    {
        var (source, recorder) = Source(_ => Page(false));

        await Assert.ThrowsAsync<MorphDbSourceException>(
            async () => await source.As(_ => ValueTask.FromResult(""))(TestContext.Current.CancellationToken));
        Assert.Empty(recorder.Requests);
    }
}

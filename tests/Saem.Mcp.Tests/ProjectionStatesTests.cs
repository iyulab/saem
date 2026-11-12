using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Saem.Contracts;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>
/// F-17 ③: a table a Formbase projects into says when its projection is behind the documents. The state is
/// asked of the Formbase over the deployment's own key; a table that is no projection is never asked about,
/// and a Formbase that cannot answer leaves the state unsaid rather than failing the lookup.
/// </summary>
public sealed class ProjectionStatesTests
{
    private sealed class Through(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Formbase(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage State(string state) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"state":"{{state}}","projectedWatermark":3,"rawHead":5,"lastRun":null}""", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Skips(int count) => new(HttpStatusCode.OK)
    {
        Content = new StringContent($$"""{"skipped":[],"count":{{count}},"skippedFields":[],"fieldCount":0}""", Encoding.UTF8, "application/json"),
    };

    /// <summary>A Formbase whose projection of <c>inspection</c> is in <paramref name="state"/> and has skipped <paramref name="skipped"/> documents.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> Projecting(string state, int skipped) =>
        request => request.RequestUri!.AbsolutePath.EndsWith("/projection/skips", StringComparison.Ordinal) ? Skips(skipped) : State(state);

    private static FormbaseProjectionStates States(Formbase formbase)
    {
        var options = new SourceOptions();
        var forms = new ProjectionSourceOptions { Url = "http://forms.test", Namespace = "inspections", AccessKey = "forms-key-0123456789abcdef0123456789" };
        forms.FormTypes["fb_inspection"] = "inspection";
        options.Projections["forms"] = forms;
        return new FormbaseProjectionStates(Options.Create(options), new Through(formbase));
    }

    [Theory]
    [InlineData("projected", null)]
    [InlineData("stale", SourceProjection.Stale)]
    [InlineData("unverified", SourceProjection.Unverified)]
    [InlineData("notProjected", SourceProjection.NotProjected)]
    public async Task A_projected_table_says_how_far_behind_it_is_and_nothing_when_current(string state, SourceProjection? expected)
    {
        using var formbase = new Formbase(Projecting(state, skipped: 0));

        var said = await States(formbase).StateOfAsync("forms", "fb_inspection", TestContext.Current.CancellationToken);

        Assert.Equal(expected, said);
        var request = formbase.Requests[0];
        Assert.Equal("http://forms.test/formtypes/inspection/projection", request.RequestUri!.ToString());
        Assert.Equal("Bearer forms-key-0123456789abcdef0123456789", request.Headers.Authorization!.ToString());
        Assert.Equal("inspections", Assert.Single(request.Headers.GetValues("Formbase-Namespace")));
    }

    [Fact]
    public async Task A_current_projection_that_skipped_documents_says_it_is_incomplete()
    {
        // `projected` says the projection reached the head of the stream, not that every document is in a row:
        // a document the declaration could not map is recorded as skipped and is in no row a lookup reads.
        using var formbase = new Formbase(Projecting("projected", skipped: 2));

        var said = await States(formbase).StateOfAsync("forms", "fb_inspection", TestContext.Current.CancellationToken);

        Assert.Equal(SourceProjection.Incomplete, said);
        var skips = formbase.Requests[1];
        Assert.Equal("http://forms.test/formtypes/inspection/projection/skips", skips.RequestUri!.ToString());
        Assert.Equal("Bearer forms-key-0123456789abcdef0123456789", skips.Headers.Authorization!.ToString());
        Assert.Equal("inspections", Assert.Single(skips.Headers.GetValues("Formbase-Namespace")));
    }

    [Theory]
    [InlineData("stale", SourceProjection.Stale)]
    [InlineData("unverified", SourceProjection.Unverified)]
    [InlineData("notProjected", SourceProjection.NotProjected)]
    public async Task A_projection_already_said_to_be_behind_keeps_that_state_and_is_not_asked_for_its_skips(string state, SourceProjection expected)
    {
        using var formbase = new Formbase(Projecting(state, skipped: 5));

        Assert.Equal(expected, await States(formbase).StateOfAsync("forms", "fb_inspection", TestContext.Current.CancellationToken));
        Assert.Single(formbase.Requests);
    }

    [Fact]
    public async Task A_formbase_that_cannot_list_the_skips_of_a_current_projection_leaves_the_state_unsaid()
    {
        using var formbase = new Formbase(request => request.RequestUri!.AbsolutePath.EndsWith("/projection/skips", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : State("projected"));

        Assert.Null(await States(formbase).StateOfAsync("forms", "fb_inspection", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("mes", "work_orders")]
    [InlineData("forms", "fb_other")]
    public async Task A_table_that_is_no_projection_is_never_asked_about(string system, string table)
    {
        using var formbase = new Formbase(_ => State("stale"));

        Assert.Null(await States(formbase).StateOfAsync(system, table, TestContext.Current.CancellationToken));
        Assert.Empty(formbase.Requests);
    }

    [Fact]
    public async Task A_formbase_that_cannot_answer_leaves_the_state_unsaid()
    {
        using var failing = new Formbase(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var garbled = new Formbase(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });

        Assert.Null(await States(failing).StateOfAsync("forms", "fb_inspection", TestContext.Current.CancellationToken));
        Assert.Null(await States(garbled).StateOfAsync("forms", "fb_inspection", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_projection_source_without_a_formbase_does_not_start()
    {
        var options = new SourceOptions();
        options.Projections["forms"] = new ProjectionSourceOptions();

        var thrown = Assert.Throws<InvalidOperationException>(() => new FormbaseProjectionStates(Options.Create(options), new Through(new HttpClientHandler())));

        Assert.Contains("Saem:Sources:Projections:forms:Url", thrown.Message, StringComparison.Ordinal);
    }
}

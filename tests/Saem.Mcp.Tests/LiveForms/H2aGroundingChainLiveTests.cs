using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// One form, one answer: a document accepted by Formbase, projected into the rig's forms source
/// (MorphDB), and asked about through saem as a person — the answer's source names the document itself,
/// and MorphDB's row-level security, evaluated as the person whose token reached it, decides who is
/// answered.
/// </summary>
public sealed class H2aGroundingChainLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-a71c-dent-on-left-door";

    private FormsRig _rig = null!;
    private string _document = "";
    private TestServer _server = null!;

    private static readonly string[] Finding = ["finding"];

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _rig = await FormsRig.StartAsync("saem-h2a", ct);

        await _rig.DeclareAsync("inspection", "inspection",
        [
            new { name = "inspector", type = "text", nullable = false },
            new { name = "finding", type = "text", nullable = false },
        ], null, ct);
        _document = await _rig.FileAsync("inspection", new { inspector = "alice", finding = Canary }, ct);
        await _rig.ProjectAsync("inspection", ct);
        await _rig.PolicyAsync("inspection", "inspector = {{user_id}}", ct);

        _server = await TestServer.StartAsync("""
            [
              { "kind": "entityType", "name": "Inspection", "system": "forms", "table": "inspection", "key": "fb_doc_id",
                "fields": { "finding": "finding" } }
            ]
            """, Settings(_rig));
    }

    // The forms source, and the Formbase that projects its table — so an answer says when the projection is behind.
    private static Dictionary<string, string?> Settings(FormsRig rig)
    {
        var settings = rig.ServerSettings;
        settings["Saem:Sources:Projections:forms:Url"] = rig.Formbase.BaseAddress!.AbsoluteUri;
        settings["Saem:Sources:Projections:forms:FormTypes:inspection"] = "inspection";
        return settings;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        if (_rig is not null)
        {
            await _rig.DisposeAsync();
        }
    }

    [Fact]
    public async Task One_form_one_answer_whose_source_is_the_document_itself()
    {
        var ct = TestContext.Current.CancellationToken;
        var call = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "Inspection", ["key"] = _document },
            ["path"] = Array.Empty<string>(),
            ["read"] = new[] { "finding" },
        };

        await using var alice = await _server.ClientAsync(await FormsRig.TokenAsync("alice", ct), ct);
        var forAlice = (await alice.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;
        await using var bob = await _server.ClientAsync(await FormsRig.TokenAsync("bob", ct), ct);
        var forBob = (await bob.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;

        var claim = Assert.Single(forAlice.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        var source = Assert.Single(claim.GetProperty("sources").EnumerateArray());
        Assert.Equal("forms", source.GetProperty("system").GetString());
        Assert.Equal("inspection", source.GetProperty("table").GetString());
        // The source is the form document as Formbase accepted it: its id is the record the answer cites.
        Assert.Equal(_document, source.GetProperty("record").GetString());
        var raw = await _rig.Formbase.GetFromJsonAsync<JsonElement>($"documents/{_document}", ct);
        Assert.Contains(Canary, raw.GetRawText(), StringComparison.Ordinal);

        Assert.Equal("refused", forBob.GetProperty("kind").GetString());
        Assert.DoesNotContain(Canary, forBob.GetRawText(), StringComparison.Ordinal);
        // Projected and nothing filed since: the source says nothing of its projection.
        Assert.False(source.TryGetProperty("projection", out _));
    }

    /// <summary>
    /// F-17 ③ on the rig: a document filed after the projection ran leaves the table behind; an answer read from
    /// it says the projection is stale — the value is what the table holds, the documents may say more.
    /// </summary>
    [Fact]
    public async Task An_answer_read_from_a_projection_behind_its_documents_says_it_is_stale()
    {
        var ct = TestContext.Current.CancellationToken;
        await _rig.FileAsync("inspection", new { inspector = "alice", finding = "scratch" }, ct);
        var call = new Dictionary<string, object?>
        {
            ["start"] = new Dictionary<string, object?> { ["type"] = "Inspection", ["key"] = _document },
            ["path"] = Array.Empty<string>(),
            ["read"] = Finding,
        };

        await using var alice = await _server.ClientAsync(await FormsRig.TokenAsync("alice", ct), ct);
        var forAlice = (await alice.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;

        var source = forAlice.GetProperty("claims")[0].GetProperty("sources")[0];
        Assert.Equal("stale", source.GetProperty("projection").GetString());
        Assert.Equal(Canary, forAlice.GetProperty("claims")[0].GetProperty("value").GetString());
    }
}

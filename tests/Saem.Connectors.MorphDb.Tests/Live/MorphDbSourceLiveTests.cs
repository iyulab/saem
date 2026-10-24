using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Saem.Query;
using Xunit;

namespace Saem.Connectors.MorphDb.Tests.Live;

/// <summary>
/// The adapter against the rig's forms source (tests/rig/compose.morphdb.yaml): each person's token is
/// exchanged at the issuer for one meant for MorphDB, and MorphDB's row-level security — evaluated as
/// that person — decides which rows the same read returns.
/// </summary>
public sealed class MorphDbSourceLiveTests : IAsyncLifetime
{
    private static readonly string Issuer = Environment.GetEnvironmentVariable("SAEM_RIG_ISSUER")
        ?? throw new InvalidOperationException("SAEM_RIG_ISSUER names the rig's issuer as the host reaches it (tests/rig/compose.morphdb.yaml).");

    private static readonly Uri Forms = new(Environment.GetEnvironmentVariable("SAEM_RIG_FORMS")
        ?? throw new InvalidOperationException("SAEM_RIG_FORMS names the rig's forms source (tests/rig/compose.morphdb.yaml)."));

    private static readonly string MasterSecret = Environment.GetEnvironmentVariable("SAEM_RIG_FORMS_MASTER_SECRET")
        ?? throw new InvalidOperationException("SAEM_RIG_FORMS_MASTER_SECRET is the forms source's master secret (tests/rig/compose.morphdb.yaml).");

    private readonly HttpClient _http = new() { BaseAddress = Forms };
    private readonly string _table = $"inspection_{Guid.NewGuid():N}"[..24];
    private Guid _project;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var master = new HttpClient { BaseAddress = Forms };
        master.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterSecret);

        var project = await master.PostAsJsonAsync("api/projects", new { name = $"saem-live-{Guid.NewGuid():N}"[..24] }, ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        master.DefaultRequestHeaders.Add("X-Project-Id", _project.ToString());

        (await master.PostAsJsonAsync("api/schema/tables", new
        {
            name = _table,
            columns = new[]
            {
                new { name = "fb_doc_id", type = "text", nullable = false },
                new { name = "inspector", type = "text", nullable = false },
                new { name = "result", type = "text", nullable = false },
            },
        }, ct)).EnsureSuccessStatusCode();

        foreach (var (doc, inspector, result) in new[] { ("doc-1", "alice", "pass"), ("doc-2", "bob", "fail") })
        {
            (await master.PostAsJsonAsync($"api/data/{_table}",
                new Dictionary<string, string> { ["fb_doc_id"] = doc, ["inspector"] = inspector, ["result"] = result }, ct))
                .EnsureSuccessStatusCode();
        }

        (await master.PostAsJsonAsync("api/security/policies", new
        {
            name = "own_inspections",
            tableName = _table,
            policyType = "Select",
            expression = "inspector = {{user_id}}",
        }, ct)).EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        using var master = new HttpClient { BaseAddress = Forms };
        master.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterSecret);
        await master.DeleteAsync($"api/projects/{_project}");
        _http.Dispose();
    }

    /// <summary>The person's own token (meant for saem), then exchanged as saem for one meant for MorphDB.</summary>
    private static async ValueTask<string> TokenForMorphDbAsync(string person, CancellationToken ct)
    {
        using var issuer = new HttpClient();
        var own = await TokenAsync(issuer, new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = person, ["client_secret"] = "rig",
            ["scope"] = "saem", ["audience"] = "saem",
        }, ct);
        return await TokenAsync(issuer, new()
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange", ["client_id"] = "saem", ["client_secret"] = "rig",
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:jwt", ["subject_token"] = own, ["audience"] = "morphdb",
        }, ct);
    }

    private static async Task<string> TokenAsync(HttpClient issuer, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await issuer.PostAsync($"{Issuer}/token", new FormUrlEncodedContent(form), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }

    private async Task<IReadOnlyList<SourceRow>> ReadAsAsync(string person, string doc)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new MorphDbSource(_http, _project);
        await using var session = await source.As(c => TokenForMorphDbAsync(person, c))(ct);
        return await session.ReadAsync(_table, "fb_doc_id", doc, ["result"], ct);
    }

    /// <summary>A find by a field is filtered by the same row-level security as a read by key.</summary>
    [Fact]
    public async Task A_find_by_a_field_answers_only_the_rows_the_person_may_see()
    {
        var ct = TestContext.Current.CancellationToken;
        async Task<IReadOnlyList<SourceRow>> FindAsAsync(string person, string result)
        {
            await using var session = await new MorphDbSource(_http, _project).As(c => TokenForMorphDbAsync(person, c))(ct);
            return await session.FindAsync(_table, "fb_doc_id", "result", result, ["inspector"], 10, ct);
        }

        Assert.Equal(["doc-1"], (await FindAsAsync("alice", "pass")).Select(r => r.Key));
        Assert.Empty(await FindAsAsync("bob", "pass"));
        Assert.Equal(["doc-2"], (await FindAsAsync("bob", "fail")).Select(r => r.Key));
    }

    [Fact]
    public async Task The_same_read_answers_the_person_the_row_belongs_to_and_no_one_else()
    {
        var forAlice = Assert.Single(await ReadAsAsync("alice", "doc-1"));
        Assert.Equal("doc-1", forAlice.Key);
        Assert.Equal("pass", forAlice.Values["result"].GetString());

        Assert.Empty(await ReadAsAsync("bob", "doc-1"));
        Assert.Equal("fail", Assert.Single(await ReadAsAsync("bob", "doc-2")).Values["result"].GetString());
    }

    [Fact]
    public async Task A_token_meant_for_saem_is_not_a_credential_at_the_source()
    {
        var ct = TestContext.Current.CancellationToken;
        using var issuer = new HttpClient();
        var meantForSaem = await TokenAsync(issuer, new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "alice", ["client_secret"] = "rig",
            ["scope"] = "saem", ["audience"] = "saem",
        }, ct);

        await using var session = await new MorphDbSource(_http, _project).As(_ => ValueTask.FromResult(meantForSaem))(ct);
        var refused = await Assert.ThrowsAsync<MorphDbSourceException>(
            async () => await session.ReadAsync(_table, "fb_doc_id", "doc-1", ["result"], ct));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.Status);
    }
}

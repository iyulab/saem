using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// One form, one answer: a document accepted by Formbase, projected into the rig's forms source
/// (MorphDB), and asked about through saem as a person — the answer's source names the document itself,
/// and MorphDB's row-level security, evaluated as the person whose token reached it, decides who is
/// answered. Formbase runs as its published image on the rig's network, started here because it needs
/// the MorphDB project to exist before it starts.
/// </summary>
public sealed class H2aGroundingChainLiveTests : IAsyncLifetime
{
    private const string Canary = "CANARY-a71c-dent-on-left-door";

    private static readonly string Issuer = Required("SAEM_RIG_ISSUER");
    private static readonly string Forms = Required("SAEM_RIG_FORMS");
    private static readonly string MasterSecret = Required("SAEM_RIG_FORMS_MASTER_SECRET");
    private static readonly string FormbaseImage = Environment.GetEnvironmentVariable("SAEM_RIG_FORMBASE_IMAGE") ?? "ghcr.io/iyulab/formbase:0.32.0";
    private static readonly string Network = Environment.GetEnvironmentVariable("SAEM_RIG_NETWORK") ?? "saem-rig_default";

    private Guid _project;
    private string _container = "";
    private HttpClient _formbase = null!;
    private string _document = "";
    private TestServer _server = null!;

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is set by the rig's forms profile (tests/rig/compose.morphdb.yaml).");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        using var master = Master();
        var project = await master.PostAsJsonAsync("api/projects", new { name = $"saem-h2a-{Guid.NewGuid():N}"[..24] }, ct);
        project.EnsureSuccessStatusCode();
        _project = (await project.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        // Formbase over the forms source: its own stream in the rig's database (a schema of its own per
        // run), its projected tables in this project, written with the master secret.
        _container = (await DockerAsync(
        [
            "run", "-d", "--network", Network, "-p", "127.0.0.1::8080",
            "-e", "Formbase__Store=Durable",
            "-e", "ConnectionStrings__Formbase=Host=forms-db;Database=formbase;Username=formbase;Password=rig-formbase-local-only",
            "-e", $"Formbase__Schema=fb_{_project:N}",
            "-e", "Formbase__MorphDb__Url=http://forms:8080",
            "-e", $"Formbase__MorphDb__ProjectId={_project}",
            "-e", $"Formbase__MorphDb__Secret={MasterSecret}",
            FormbaseImage,
        ], ct)).Trim();
        var port = (await DockerAsync(["port", _container, "8080"], ct)).Split('\n')[0].Split(':')[^1].Trim();
        _formbase = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        await WaitHealthyAsync(ct);

        (await _formbase.PutAsJsonAsync("formtypes/inspection/declaration", new
        {
            tableName = "inspection",
            declarationVersion = 1,
            fields = new[]
            {
                new { name = "inspector", type = "text", nullable = false },
                new { name = "finding", type = "text", nullable = false },
            },
        }, ct)).EnsureSuccessStatusCode();
        var accepted = await _formbase.PostAsJsonAsync("formtypes/inspection/documents", new { inspector = "alice", finding = Canary }, ct);
        accepted.EnsureSuccessStatusCode();
        _document = (await accepted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("documentId").GetString()!;
        (await _formbase.PostAsync("formtypes/inspection/projection", null, ct)).EnsureSuccessStatusCode();

        master.DefaultRequestHeaders.Add("X-Project-Id", _project.ToString());
        (await master.PostAsJsonAsync("api/security/policies", new
        {
            name = "own_inspections",
            tableName = "inspection",
            policyType = "Select",
            expression = "inspector = {{user_id}}",
        }, ct)).EnsureSuccessStatusCode();

        _server = await TestServer.StartAsync("""
            [
              { "kind": "entityType", "name": "Inspection", "system": "forms", "table": "inspection", "key": "fb_doc_id",
                "fields": { "finding": "finding" } }
            ]
            """, new Dictionary<string, string?>
        {
            ["Saem:Identity:Authority"] = Issuer,
            ["Saem:Identity:Audience"] = "saem",
            ["Saem:Identity:RequireHttpsMetadata"] = "false",
            ["Saem:Sources:MorphDb:forms:Url"] = Forms,
            ["Saem:Sources:MorphDb:forms:ProjectId"] = _project.ToString(),
            ["Saem:Sources:MorphDb:forms:Audience"] = "morphdb",
            ["Saem:Sources:TokenExchange:TokenEndpoint"] = Issuer + "/token",
            ["Saem:Sources:TokenExchange:ClientId"] = "saem",
            ["Saem:Sources:TokenExchange:ClientSecret"] = "rig-local-only",
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _formbase?.Dispose();
        if (_container.Length > 0)
        {
            await DockerAsync(["rm", "-f", _container], CancellationToken.None);
        }

        using var master = Master();
        await master.DeleteAsync($"api/projects/{_project}");
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

        await using var alice = await _server.ClientAsync(await TokenAsync("alice", ct), ct);
        var forAlice = (await alice.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;
        await using var bob = await _server.ClientAsync(await TokenAsync("bob", ct), ct);
        var forBob = (await bob.CallToolAsync("lookup", call, cancellationToken: ct)).StructuredContent!.Value;

        var claim = Assert.Single(forAlice.GetProperty("claims").EnumerateArray());
        Assert.Equal(Canary, claim.GetProperty("value").GetString());
        var source = Assert.Single(claim.GetProperty("sources").EnumerateArray());
        Assert.Equal("forms", source.GetProperty("system").GetString());
        Assert.Equal("inspection", source.GetProperty("table").GetString());
        // The source is the form document as Formbase accepted it: its id is the record the answer cites.
        Assert.Equal(_document, source.GetProperty("record").GetString());
        var raw = await _formbase.GetFromJsonAsync<JsonElement>($"documents/{_document}", ct);
        Assert.Contains(Canary, raw.GetRawText(), StringComparison.Ordinal);

        Assert.Equal("refused", forBob.GetProperty("kind").GetString());
        Assert.DoesNotContain(Canary, forBob.GetRawText(), StringComparison.Ordinal);
    }

    private static HttpClient Master()
    {
        var master = new HttpClient { BaseAddress = new Uri(Forms) };
        master.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterSecret);
        return master;
    }

    private async Task WaitHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                if ((await _formbase.GetAsync("health", ct)).IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline)
            {
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Formbase did not become healthy: " + await DockerAsync(["logs", "--tail", "20", _container], ct));
            }

            await Task.Delay(500, ct);
        }
    }

    private static async Task<string> TokenAsync(string person, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var response = await http.PostAsync(Issuer + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = person,
            ["client_secret"] = "rig-local-only",
            ["scope"] = "saem-rig",
            ["audience"] = "saem",
        }), ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }

    private static async Task<string> DockerAsync(string[] arguments, CancellationToken ct)
    {
        using var docker = Process.Start(new ProcessStartInfo("docker", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = docker.StandardOutput.ReadToEndAsync(ct);
        var stderr = docker.StandardError.ReadToEndAsync(ct);
        await docker.WaitForExitAsync(ct);
        Assert.True(docker.ExitCode == 0, $"docker {string.Join(' ', arguments)} failed: {await stderr}");
        return await stdout;
    }
}

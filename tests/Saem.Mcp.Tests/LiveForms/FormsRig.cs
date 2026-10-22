using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// Formbase over the rig's forms source, for one test class: a MorphDB project of its own, and Formbase
/// running as its published image on the rig's network — its own stream in the rig's database (a schema
/// of its own per run), its projected tables in that project, written with the master secret. Started
/// here because Formbase needs the project to exist before it starts.
/// </summary>
internal sealed class FormsRig : IAsyncDisposable
{
    private static readonly string Issuer = Required("SAEM_RIG_ISSUER");
    private static readonly string Forms = Required("SAEM_RIG_FORMS");
    private static readonly string MasterSecret = Required("SAEM_RIG_FORMS_MASTER_SECRET");
    private static readonly string FormbaseImage = Environment.GetEnvironmentVariable("SAEM_RIG_FORMBASE_IMAGE") ?? "ghcr.io/iyulab/formbase:0.33.0";
    private static readonly string Network = Environment.GetEnvironmentVariable("SAEM_RIG_NETWORK") ?? "saem-rig_default";

    private readonly string _container;

    private FormsRig(Guid project, string container, HttpClient formbase)
    {
        Project = project;
        _container = container;
        Formbase = formbase;
    }

    /// <summary>The MorphDB project Formbase projects into.</summary>
    public Guid Project { get; }

    /// <summary>Formbase's HTTP API.</summary>
    public HttpClient Formbase { get; }

    /// <summary>The server settings that read the forms source as the person whose token reached saem.</summary>
    public Dictionary<string, string?> ServerSettings => new()
    {
        ["Saem:Identity:Authority"] = Issuer,
        ["Saem:Identity:Audience"] = "saem",
        ["Saem:Identity:RequireHttpsMetadata"] = "false",
        ["Saem:Sources:MorphDb:forms:Url"] = Forms,
        ["Saem:Sources:MorphDb:forms:ProjectId"] = Project.ToString(),
        ["Saem:Sources:MorphDb:forms:Audience"] = "morphdb",
        ["Saem:Sources:TokenExchange:TokenEndpoint"] = Issuer + "/token",
        ["Saem:Sources:TokenExchange:ClientId"] = "saem",
        ["Saem:Sources:TokenExchange:ClientSecret"] = "rig-local-only",
    };

    public static async Task<FormsRig> StartAsync(string name, CancellationToken ct)
    {
        using var master = Master();
        var created = await master.PostAsJsonAsync("api/projects", new { name = $"{name}-{Guid.NewGuid():N}"[..24] }, ct);
        created.EnsureSuccessStatusCode();
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();

        var container = (await DockerAsync(
        [
            "run", "-d", "--network", Network, "-p", "127.0.0.1::8080",
            "-e", "Formbase__Store=Durable",
            "-e", "ConnectionStrings__Formbase=Host=forms-db;Database=formbase;Username=formbase;Password=rig-formbase-local-only",
            "-e", $"Formbase__Schema=fb_{project:N}",
            "-e", "Formbase__MorphDb__Url=http://forms:8080",
            "-e", $"Formbase__MorphDb__ProjectId={project}",
            "-e", $"Formbase__MorphDb__Secret={MasterSecret}",
            FormbaseImage,
        ], ct)).Trim();
        var port = (await DockerAsync(["port", container, "8080"], ct)).Split('\n')[0].Split(':')[^1].Trim();
        var rig = new FormsRig(project, container, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
        await rig.WaitHealthyAsync(ct);
        return rig;
    }

    /// <summary>Declares a form type whose projected table is <paramref name="table"/>.</summary>
    public async Task DeclareAsync(string formType, string table, object[] fields, object[]? relations, CancellationToken ct)
    {
        using var response = await Formbase.PutAsJsonAsync($"formtypes/{formType}/declaration",
            new { tableName = table, declarationVersion = 1, fields, relations = relations ?? [] }, ct);
        Assert.True(response.IsSuccessStatusCode, $"Declaring {formType} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Files a document under <paramref name="formType"/> and returns the id Formbase gave it.</summary>
    public async Task<string> FileAsync(string formType, object document, CancellationToken ct)
    {
        var accepted = await Formbase.PostAsJsonAsync($"formtypes/{formType}/documents", document, ct);
        accepted.EnsureSuccessStatusCode();
        return (await accepted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("documentId").GetString()!;
    }

    public async Task ProjectAsync(string formType, CancellationToken ct) =>
        (await Formbase.PostAsync($"formtypes/{formType}/projection", null, ct)).EnsureSuccessStatusCode();

    /// <summary>Lets a person read the rows of <paramref name="table"/> that <paramref name="expression"/> admits.</summary>
    public async Task PolicyAsync(string table, string expression, CancellationToken ct)
    {
        using var master = Master();
        master.DefaultRequestHeaders.Add("X-Project-Id", Project.ToString());
        (await master.PostAsJsonAsync("api/security/policies", new
        {
            name = $"own_{table}",
            tableName = table,
            policyType = "Select",
            expression,
        }, ct)).EnsureSuccessStatusCode();
    }

    /// <summary>A token the rig's issuer gives <paramref name="person"/> for saem.</summary>
    public static async Task<string> TokenAsync(string person, CancellationToken ct)
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

    public async ValueTask DisposeAsync()
    {
        Formbase.Dispose();
        await DockerAsync(["rm", "-f", _container], CancellationToken.None);
        using var master = Master();
        await master.DeleteAsync($"api/projects/{Project}");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is set by the rig's forms profile (tests/rig/compose.morphdb.yaml).");

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
                if ((await Formbase.GetAsync("health", ct)).IsSuccessStatusCode)
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

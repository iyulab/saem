using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// Formbase over the rig's forms source, for one test class: a MorphDB project of its own, and Formbase
/// (<see cref="RigFormbase"/>) projecting into it with the master secret. Started here because Formbase
/// needs the project to exist before it starts.
/// </summary>
internal sealed class FormsRig : IAsyncDisposable
{
    private static readonly string Issuer = Required("SAEM_RIG_ISSUER");
    private static readonly string Forms = Required("SAEM_RIG_FORMS");
    private static readonly string MasterSecret = Required("SAEM_RIG_FORMS_MASTER_SECRET");

    private readonly RigFormbase _formbase;

    private FormsRig(Guid project, RigFormbase formbase)
    {
        Project = project;
        _formbase = formbase;
    }

    /// <summary>The MorphDB project Formbase projects into.</summary>
    public Guid Project { get; }

    /// <summary>Formbase's HTTP API.</summary>
    public HttpClient Formbase => _formbase.Http;

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

        return new FormsRig(project, await RigFormbase.StartAsync(project, MasterSecret, ct));
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
        await _formbase.DisposeAsync();
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
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// One group's materialization store on the rig (D-22): a MorphDB project of its own, a secret confined to
/// that project, and Formbase (<see cref="RigFormbase"/>) projecting into it with that secret. Nothing here holds the master secret past setting the project up, so what the store
/// can reach is what the confined secret reaches.
/// </summary>
internal sealed class GroupStoreRig : IAsyncDisposable
{
    private static readonly string Forms = Required("SAEM_RIG_FORMS");
    private static readonly string MasterSecret = Required("SAEM_RIG_FORMS_MASTER_SECRET");

    private readonly RigFormbase _formbase;

    private GroupStoreRig(string group, Guid project, string secret, RigFormbase formbase)
    {
        Group = group;
        Project = project;
        Secret = secret;
        _formbase = formbase;
    }

    /// <summary>The group the store is for.</summary>
    public string Group { get; }

    /// <summary>The group's MorphDB project.</summary>
    public Guid Project { get; }

    /// <summary>The secret confined to <see cref="Project"/>, with the group's role.</summary>
    public string Secret { get; }

    /// <summary>The group's Formbase.</summary>
    public HttpClient Formbase => _formbase.Http;

    public static async Task<GroupStoreRig> StartAsync(string group, CancellationToken ct)
    {
        using var master = new HttpClient { BaseAddress = new Uri(Forms) };
        master.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterSecret);
        var created = await master.PostAsJsonAsync("api/projects", new { name = $"{group}-{Guid.NewGuid():N}"[..24] }, ct);
        created.EnsureSuccessStatusCode();
        var project = (await created.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
        var issued = await master.PostAsJsonAsync("api/security/secrets", new { name = $"{group}-store", role = group.Replace('-', '_'), projectId = project }, ct);
        issued.EnsureSuccessStatusCode();
        var secret = (await issued.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("secret").GetString()!;

        return new GroupStoreRig(group, project, secret, await RigFormbase.StartAsync(project, secret, ct));
    }

    /// <summary>MorphDB as this group's secret, addressing <paramref name="project"/>.</summary>
    public HttpClient MorphDbAs(Guid project)
    {
        var client = new HttpClient { BaseAddress = new Uri(Forms) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        client.DefaultRequestHeaders.Add("X-Project-Id", project.ToString());
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await _formbase.DisposeAsync();
        using var master = new HttpClient { BaseAddress = new Uri(Forms) };
        master.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MasterSecret);
        await master.DeleteAsync($"api/projects/{Project}");
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is set by the rig's forms profile (tests/rig/compose.morphdb.yaml).");
}

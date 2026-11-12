using System.Diagnostics;
using System.Net.Http.Headers;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// Formbase running as its published image on the rig's network, projecting into one MorphDB project with
/// the secret it is given — its own schema in the rig's Formbase database. The forms source and each
/// group's store start one the same way and differ only in the project and the secret. Each host is started with
/// an access key of its own (SD-15: the host asks no one who is calling, so it answers only a caller that presents
/// a key — a key names a calling service, not a person): <see cref="Http"/> carries it, and a caller that is not
/// this client sends <see cref="AccessKey"/> itself.
/// </summary>
internal sealed class RigFormbase : IAsyncDisposable
{
    private static readonly string Image = Environment.GetEnvironmentVariable("SAEM_RIG_FORMBASE_IMAGE") ?? "ghcr.io/iyulab/formbase:0.35.0";
    private static readonly string Network = Environment.GetEnvironmentVariable("SAEM_RIG_NETWORK") ?? "saem-rig_default";

    private readonly string _container;

    private RigFormbase(string container, HttpClient http, string accessKey)
    {
        _container = container;
        Http = http;
        AccessKey = accessKey;
    }

    /// <summary>Formbase's HTTP API, presenting <see cref="AccessKey"/>.</summary>
    public HttpClient Http { get; }

    /// <summary>The access key this host was started with — what a caller of it presents as a bearer token.</summary>
    public string AccessKey { get; }

    public static async Task<RigFormbase> StartAsync(Guid project, string secret, CancellationToken ct)
    {
        // The host refuses to start with a key shorter than 32 characters.
        var accessKey = $"rig-access-key-{Guid.NewGuid():N}";
        var container = (await DockerAsync(
        [
            "run", "-d", "--network", Network, "-p", "127.0.0.1::8080",
            "-e", "Formbase__Store=Durable",
            "-e", "ConnectionStrings__Formbase=Host=forms-db;Database=formbase;Username=formbase;Password=rig-formbase-local-only",
            "-e", $"Formbase__Schema=fb_{project:N}",
            "-e", "Formbase__MorphDb__Url=http://forms:8080",
            "-e", $"Formbase__MorphDb__ProjectId={project}",
            "-e", $"Formbase__MorphDb__Secret={secret}",
            "-e", $"Formbase__Access__Keys__0={accessKey}",
            Image,
        ], ct)).Trim();
        var port = (await DockerAsync(["port", container, "8080"], ct)).Split('\n')[0].Split(':')[^1].Trim();
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessKey);
        var formbase = new RigFormbase(container, http, accessKey);
        await formbase.WaitHealthyAsync(ct);
        return formbase;
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await DockerAsync(["rm", "-f", _container], CancellationToken.None);
    }

    private async Task WaitHealthyAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                if ((await Http.GetAsync("health", ct)).IsSuccessStatusCode)
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

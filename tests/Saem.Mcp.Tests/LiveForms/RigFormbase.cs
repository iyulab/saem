using System.Diagnostics;
using Xunit;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// Formbase running as its published image on the rig's network, projecting into one MorphDB project with
/// the secret it is given — its own schema in the rig's Formbase database. The forms source and each
/// group's store start one the same way and differ only in the project and the secret.
/// </summary>
internal sealed class RigFormbase : IAsyncDisposable
{
    private static readonly string Image = Environment.GetEnvironmentVariable("SAEM_RIG_FORMBASE_IMAGE") ?? "ghcr.io/iyulab/formbase:0.34.0";
    private static readonly string Network = Environment.GetEnvironmentVariable("SAEM_RIG_NETWORK") ?? "saem-rig_default";

    private readonly string _container;

    private RigFormbase(string container, HttpClient http)
    {
        _container = container;
        Http = http;
    }

    /// <summary>Formbase's HTTP API.</summary>
    public HttpClient Http { get; }

    public static async Task<RigFormbase> StartAsync(Guid project, string secret, CancellationToken ct)
    {
        var container = (await DockerAsync(
        [
            "run", "-d", "--network", Network, "-p", "127.0.0.1::8080",
            "-e", "Formbase__Store=Durable",
            "-e", "ConnectionStrings__Formbase=Host=forms-db;Database=formbase;Username=formbase;Password=rig-formbase-local-only",
            "-e", $"Formbase__Schema=fb_{project:N}",
            "-e", "Formbase__MorphDb__Url=http://forms:8080",
            "-e", $"Formbase__MorphDb__ProjectId={project}",
            "-e", $"Formbase__MorphDb__Secret={secret}",
            Image,
        ], ct)).Trim();
        var port = (await DockerAsync(["port", container, "8080"], ct)).Split('\n')[0].Split(':')[^1].Trim();
        var formbase = new RigFormbase(container, new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") });
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

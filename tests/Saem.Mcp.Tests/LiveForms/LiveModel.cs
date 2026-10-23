using System.Net.Http.Headers;
using Eyu.Core.Inference.Http;
using Eyu.Core.Judgment;
using Saem.Rig.Model;

namespace Saem.Mcp.Tests.LiveForms;

/// <summary>
/// Eyu's proposer against the real model the tests that draft an ontology call — named by
/// <c>EYU_LLM_ENDPOINT</c>, <c>EYU_LLM_API_KEY</c> and <c>EYU_LLM_MODEL</c>, streamed when <c>EYU_LLM_STREAM</c> is true,
/// each call retried once when it fails transiently (<see cref="LiveModelRetry"/>).
/// </summary>
internal sealed class LiveModel : IDisposable
{
    private readonly HttpClient _http;

    public LiveModel()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(Require("EYU_LLM_ENDPOINT").TrimEnd('/') + "/v1/"),
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Require("EYU_LLM_API_KEY"));
        var stream = bool.TryParse(Environment.GetEnvironmentVariable("EYU_LLM_STREAM"), out var streamed) && streamed;
        Proposer = new SinglePassOntologyProposer(LiveModelRetry.Around(new HttpModelClient(_http, Require("EYU_LLM_MODEL"), stream: stream)));
    }

    public SinglePassOntologyProposer Proposer { get; }

    public void Dispose() => _http.Dispose();

    /// <summary>Writes a pass's review to the test output, and where <c>SAEM_AUTHORING_REPORT_DIR</c> points.</summary>
    public static async Task ReportAsync(string name, string review, CancellationToken ct)
    {
        Xunit.TestContext.Current.SendDiagnosticMessage(review);
        if (Environment.GetEnvironmentVariable("SAEM_AUTHORING_REPORT_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.md"), review, ct);
        }
    }

    private static string Require(string variable) =>
        Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"{variable} is required: the ontology is drafted with a real model.");
}

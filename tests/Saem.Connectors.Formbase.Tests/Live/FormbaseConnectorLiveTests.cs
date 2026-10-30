using System.Net.Http.Json;
using Eyu.Core.Primitives;
using Xunit;

namespace Saem.Connectors.Formbase.Tests.Live;

/// <summary>
/// The connector against a running Formbase, over the network, the way saem will read one: documents
/// accepted with nothing declared, a declaration put in force for part of them, and then both of
/// Eyu's reads — the declared part as structure, every field as records.
/// </summary>
public class FormbaseConnectorLiveTests
{
    private static HttpClient Formbase() => new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("SAEM_FORMBASE_URL")
            ?? throw new InvalidOperationException("SAEM_FORMBASE_URL names the Formbase instance these tests read.")),
    };

    [Fact]
    public async Task Both_of_eyus_reads_come_back_across_the_process_boundary()
    {
        using var http = Formbase();
        var type = "workorders-" + Guid.NewGuid().ToString("N")[..12];
        var ct = TestContext.Current.CancellationToken;
        await AssertAnswersAsFormbaseAsync(http, ct);

        foreach (var body in new[]
                 {
                     new { wo = "WO-1", equipment = "PRESS-3", started = "2024-03-11", note = "initial inspection" },
                     new { wo = "WO-2", equipment = "PRESS-3", started = "2024-03-12", note = "bearing noise" },
                 })
        {
            (await http.PostAsJsonAsync($"formtypes/{type}/documents", body, ct)).EnsureSuccessStatusCode();
        }

        var connector = new FormbaseConnector(http);
        var subject = SubjectRef.Create(type);

        Assert.Null(await connector.GetStructureAsync(subject, ct));
        var before = await connector.SampleAsync(subject, 10, ct);
        Assert.Equal(2, before.Count);
        Assert.Equal("bearing noise", before[1].Fields["note"]);

        (await http.PutAsJsonAsync($"formtypes/{type}/declaration", new
        {
            tableName = type.Replace('-', '_'),
            declarationVersion = 1,
            fields = new[] { new { name = "wo", type = "text", nullable = false }, new { name = "equipment", type = "text", nullable = true } },
        }, ct)).EnsureSuccessStatusCode();

        var structure = await connector.GetStructureAsync(subject, ct);
        Assert.NotNull(structure);
        Assert.Equal(["wo", "equipment"], structure.Fields.Select(f => f.Name));

        var after = await connector.SampleAsync(subject, 10, ct);
        Assert.Equal(before.Select(r => r.Id), after.Select(r => r.Id));
        Assert.All(after, r => Assert.Contains("started", r.Fields.Keys));
    }

    /// <summary>
    /// A form revised after a long stretch of the original, read from both ends of its stream: the
    /// earliest records show none of the revision, the most recent show all of it, and the most recent
    /// are the same records on a stream another form type shares.
    /// </summary>
    [Fact]
    public async Task A_revised_forms_most_recent_records_are_read_across_the_process_boundary()
    {
        using var http = Formbase();
        var type = "revised-" + Guid.NewGuid().ToString("N")[..12];
        var other = "other-" + Guid.NewGuid().ToString("N")[..12];
        var ct = TestContext.Current.CancellationToken;
        await AssertAnswersAsFormbaseAsync(http, ct);

        for (var i = 1; i <= 140; i++)
        {
            (await http.PostAsJsonAsync($"formtypes/{type}/documents", new { wo = $"WO-{i}" }, ct)).EnsureSuccessStatusCode();
            if (i % 3 == 0)
            {
                (await http.PostAsJsonAsync($"formtypes/{other}/documents", new { n = i }, ct)).EnsureSuccessStatusCode();
            }
        }

        for (var i = 141; i <= 150; i++)
        {
            (await http.PostAsJsonAsync($"formtypes/{type}/documents", new { wo = $"WO-{i}", crane = "C-1" }, ct)).EnsureSuccessStatusCode();
        }

        var subject = SubjectRef.Create(type);
        var earliest = await new FormbaseConnector(http, sample: SampleDirection.Oldest).SampleAsync(subject, 20, ct);
        var latest = await new FormbaseConnector(http, sample: SampleDirection.Newest).SampleAsync(subject, 20, ct);

        Assert.Equal(20, earliest.Count);
        Assert.DoesNotContain(earliest, r => r.Fields.ContainsKey("crane"));
        Assert.Equal(Enumerable.Range(131, 20).Select(i => $"WO-{i}"), latest.Select(r => r.Fields["wo"]));
        Assert.Equal(10, latest.Count(r => r.Fields.ContainsKey("crane")));
    }

    /// <summary>
    /// Fails with the cause when <c>SAEM_FORMBASE_URL</c> reaches something that is not a Formbase host —
    /// a port another service already listens on, say. Without it the first write below fails with a bare
    /// status from whatever answered, in the test's own preparation rather than in the connector.
    /// </summary>
    private static async Task AssertAnswersAsFormbaseAsync(HttpClient http, CancellationToken ct)
    {
        string? observed;
        try
        {
            using var settings = await http.GetAsync("settings", ct);
            observed = settings.IsSuccessStatusCode
                && settings.Content.Headers.ContentType?.MediaType == "application/json"
                && (await settings.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct))
                    .TryGetProperty("namespace", out _)
                ? null
                : $"GET settings answered {(int)settings.StatusCode} without a Formbase settings document";
        }
        catch (HttpRequestException e)
        {
            observed = $"nothing answered ({e.Message})";
        }

        Assert.True(observed is null,
            $"SAEM_FORMBASE_URL ({http.BaseAddress}) does not answer as a Formbase host: {observed}. " +
            "Start one — docker run -p 8080:8080 ghcr.io/iyulab/formbase:<version> — and point the variable at it.");
    }
}

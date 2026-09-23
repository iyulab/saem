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
}

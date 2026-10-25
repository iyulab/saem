using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// The overview and the last check of the declarations against the sources: what the console shows of a report a builder
/// ran with their own account — the findings, never the records they cite.
/// </summary>
public sealed class OverviewApiTests
{
    private const string Catalog = """
        [
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no", "fields": { "item": "item_code" } },
          { "kind": "entityType", "name": "Item", "system": "erp", "table": "items", "key": "item_no", "fields": { "name": "item_name" } },
          { "kind": "relation", "name": "orders_item", "from": "WorkOrder", "via": "item", "to": "Item" }
        ]
        """;

    private static string Report(string catalogVersion) => $$"""
        { "checkedAt": "2026-10-25T03:00:00+00:00", "catalogVersion": "{{catalogVersion}}", "holds": false,
          "findings": [
            { "type": "WorkOrder", "relation": null, "kind": "read", "violation": false, "what": "2 rows read.", "count": 2, "records": [] },
            { "type": "WorkOrder", "relation": "orders_item", "kind": "unresolved", "violation": true, "what": "1 value names no Item.", "count": 1, "records": ["WO-1"] }
          ] }
        """;

    [Fact]
    public async Task The_overview_counts_the_declarations_and_says_whether_the_last_check_is_of_the_catalog_in_force()
    {
        var report = Path.GetTempFileName();
        try
        {
            await using var server = await ConsoleServer.StartAsync(ontologyJson: Catalog, configure: s => s.Configure<ConsoleCheckOptions>(o => o.CheckReport = report));
            using var auditor = server.Client(ConsoleServer.Token("ann", ["privacy-office"]));
            var version = (await auditor.GetFromJsonAsync<JsonElement>("api/catalog", TestContext.Current.CancellationToken)).GetProperty("version").GetString()!;

            await File.WriteAllTextAsync(report, Report(version), TestContext.Current.CancellationToken);
            var current = await auditor.GetFromJsonAsync<Overview>("api/overview", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(report, Report("an-older-catalog"), TestContext.Current.CancellationToken);
            var stale = await auditor.GetFromJsonAsync<Overview>("api/overview", TestContext.Current.CancellationToken);

            Assert.Equal(2, current!.Declarations["entityType"]);
            Assert.Equal(1, current.Declarations["relation"]);
            Assert.Equal(new CheckSummary(new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero), version, true, false, 1), current.Check);
            Assert.False(stale!.Check!.Current);
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public async Task A_builder_reads_the_findings_without_the_records_they_cite_and_an_auditor_may_not()
    {
        var report = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(report, Report("v"), TestContext.Current.CancellationToken);
            await using var server = await ConsoleServer.StartAsync(ontologyJson: Catalog, configure: s => s.Configure<ConsoleCheckOptions>(o => o.CheckReport = report));
            using var builder = server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));
            using var auditor = server.Client(ConsoleServer.Token("ann", ["privacy-office"]));

            var shown = await builder.GetFromJsonAsync<JsonElement>("api/check", TestContext.Current.CancellationToken);

            var unresolved = shown.GetProperty("findings")[1];
            Assert.Equal(1, unresolved.GetProperty("recordsCited").GetInt32());
            Assert.False(unresolved.TryGetProperty("records", out _));
            Assert.DoesNotContain("WO-1", shown.GetRawText(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("api/check", TestContext.Current.CancellationToken)).StatusCode);
        }
        finally
        {
            File.Delete(report);
        }
    }

    [Fact]
    public async Task Without_a_report_the_overview_has_no_check_and_the_check_is_not_found()
    {
        await using var server = await ConsoleServer.StartAsync(ontologyJson: Catalog);
        using var builder = server.Client(ConsoleServer.Token("alice", ["ontology-builders"]));

        var overview = await builder.GetFromJsonAsync<Overview>("api/overview", TestContext.Current.CancellationToken);

        Assert.Null(overview!.Check);
        Assert.Equal(HttpStatusCode.NotFound, (await builder.GetAsync("api/check", TestContext.Current.CancellationToken)).StatusCode);
    }
}

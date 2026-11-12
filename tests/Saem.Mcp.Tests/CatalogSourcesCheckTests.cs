using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Saem.Ontology;
using Xunit;

namespace Saem.Mcp.Tests;

/// <summary>The start-up check says what the deployment cannot answer — and says it only when it cannot: a system any kind of source is set for is configured.</summary>
public sealed class CatalogSourcesCheckTests : IDisposable
{
    private const string Catalog = """
        [
          { "kind": "entityType", "name": "Item", "system": "erp", "table": "items", "key": "item_no", "fields": { "name": "item_name" } },
          { "kind": "entityType", "name": "WorkOrder", "system": "mes", "table": "work_orders", "key": "wo_no", "fields": { "item": "item_code" } },
          { "kind": "relation", "name": "orders_item", "from": "WorkOrder", "via": "item", "to": "Item" }
        ]
        """;

    private readonly string _file = Path.GetTempFileName();

    public void Dispose() => File.Delete(_file);

    private async Task<List<string>> WarningsAsync(SourceOptions sources)
    {
        await File.WriteAllTextAsync(_file, Catalog, TestContext.Current.CancellationToken);
        var logger = new Capturing();
        await new CatalogSourcesCheck(new FileOntologyCatalog(_file), Options.Create(sources), logger).StartAsync(TestContext.Current.CancellationToken);
        return logger.Warnings;
    }

    [Fact]
    public async Task A_system_with_no_source_of_any_kind_is_warned_about()
    {
        var sources = new SourceOptions();
        sources.Postgres["erp"] = "Host=erp";

        var warnings = await WarningsAsync(sources);

        Assert.Single(warnings);
        Assert.Contains("'mes'", warnings[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_system_whose_source_logs_in_with_the_persons_token_is_configured_too()
    {
        var sources = new SourceOptions();
        sources.Postgres["erp"] = "Host=erp";
        sources.MySqlToken["mes"] = new MySqlTokenSourceOptions { ConnectionString = "Server=mes;Database=mes", Audience = "mes-aud" };

        Assert.Empty(await WarningsAsync(sources));
    }

    private sealed class Capturing : ILogger<CatalogSourcesCheck>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}

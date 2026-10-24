using System.Net.Http.Headers;
using Saem.Connectors.MorphDb;
using Saem.Connectors.Postgres;
using Saem.Materialization;
using Saem.Materialization.Cli;
using Saem.Ontology;
using Saem.Query;

if (args is [] or ["--help"] or ["-h"])
{
    Console.WriteLine(MaterializeOptions.Usage);
    return args is [] ? 2 : 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

if (args is ["check", .. var checkArgs])
{
    if (checkArgs is [] or ["--help"] or ["-h"])
    {
        Console.WriteLine(CheckRun.Usage);
        return checkArgs is [] ? 2 : 0;
    }

    try
    {
        var (catalog, sourcesFile) = CheckRun.Parse(checkArgs);
        var declared = await new FileOntologyCatalog(catalog).GetAsync(cancellation.Token);
        using var accounts = GroupSources.Load(await File.ReadAllTextAsync(sourcesFile, cancellation.Token), Environment.GetEnvironmentVariable);
        return await CheckRun.RunAsync(declared, accounts.Openers, Console.Out, cancellation.Token) ? 0 : 1;
    }
    catch (MaterializeUsageException e)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine("Run saem-materialize check --help for the arguments.");
        return 2;
    }
    catch (Exception e) when (e is OntologyDeclarationException or IOException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        Console.Error.WriteLine("Cancelled.");
        return 130;
    }
}

try
{
    var options = MaterializeOptions.Parse(args, Environment.GetEnvironmentVariable);
    var ontology = await new FileOntologyCatalog(options.CatalogPath).GetAsync(cancellation.Token);
    using var sources = GroupSources.Load(await File.ReadAllTextAsync(options.SourcesPath, cancellation.Token), Environment.GetEnvironmentVariable);
    using var store = new HttpClient { BaseAddress = options.Store };
    if (options.StoreToken is { } token)
    {
        store.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    await MaterializeRun.RunAsync(ontology, sources.Openers, options.Group, new EntityIris(options.BaseIri), new FormbaseEdgeStore(store, options.StoreNamespace), Console.Out, cancellation.Token);
    return 0;
}
catch (MaterializeUsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("Run saem-materialize --help for the arguments.");
    return 2;
}
catch (Exception e) when (e is EdgeStoreException or OntologyDeclarationException or NoSourceLoginException or MorphDbSourceException or InsecureSourceRoleException
                          or System.Data.Common.DbException or HttpRequestException or IOException or InvalidOperationException)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

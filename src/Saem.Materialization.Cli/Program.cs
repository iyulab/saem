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
        var (catalog, sourcesFile, beforeFile) = CheckRun.Parse(checkArgs);
        var declared = await new FileOntologyCatalog(catalog).GetAsync(cancellation.Token);
        var before = beforeFile is null ? null : await new FileOntologyCatalog(beforeFile).GetAsync(cancellation.Token);
        using var accounts = GroupSources.Load(await File.ReadAllTextAsync(sourcesFile, cancellation.Token), Environment.GetEnvironmentVariable);
        return await CheckRun.RunAsync(declared, accounts.Openers, Console.Out, cancellation.Token, before) ? 0 : 1;
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

if (args is ["judge", .. var judgeArgs])
{
    if (judgeArgs is [] or ["--help"] or ["-h"])
    {
        Console.WriteLine(JudgeRun.Usage);
        return judgeArgs is [] ? 2 : 0;
    }

    try
    {
        var judge = JudgeRun.Parse(judgeArgs, Environment.GetEnvironmentVariable);
        var declared = await new FileOntologyCatalog(judge.CatalogPath).GetAsync(cancellation.Token);
        using var judgeStore = new HttpClient { BaseAddress = judge.Store };
        if (judge.StoreToken is { } judgeToken)
        {
            judgeStore.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", judgeToken);
        }

        await JudgeRun.RunAsync(declared, judge, new FormbaseIdentityStore(judgeStore, judge.StoreNamespace), DateTimeOffset.UtcNow, Console.Out, cancellation.Token);
        return 0;
    }
    catch (MaterializeUsageException e)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine("Run saem-materialize judge --help for the arguments.");
        return 2;
    }
    catch (Exception e) when (e is IdentityStoreException or OntologyDeclarationException or HttpRequestException or IOException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

try
{
    // verify takes the pass's own arguments, and compares instead of writing.
    var verify = args is ["verify", ..];
    var options = MaterializeOptions.Parse(verify ? args[1..] : args, Environment.GetEnvironmentVariable);
    var ontology = await new FileOntologyCatalog(options.CatalogPath).GetAsync(cancellation.Token);
    using var sources = GroupSources.Load(await File.ReadAllTextAsync(options.SourcesPath, cancellation.Token), Environment.GetEnvironmentVariable);
    using var store = new HttpClient { BaseAddress = options.Store };
    if (options.StoreToken is { } token)
    {
        store.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    var edgeStore = new FormbaseEdgeStore(store, options.StoreNamespace);
    var identityStore = new FormbaseIdentityStore(store, options.StoreNamespace);
    if (verify)
    {
        var agrees = await MaterializeRun.VerifyAsync(ontology, sources.Openers, options.Group, new EntityIris(options.BaseIri), edgeStore, identityStore, Console.Out, cancellation.Token);
        return agrees ? 0 : 1;
    }

    await MaterializeRun.RunAsync(ontology, sources.Openers, options.Group, new EntityIris(options.BaseIri), edgeStore, identityStore, Console.Out, cancellation.Token);
    return 0;
}
catch (MaterializeUsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("Run saem-materialize --help for the arguments.");
    return 2;
}
catch (Exception e) when (e is EdgeStoreException or IdentityStoreException or OntologyDeclarationException or NoSourceLoginException or MorphDbSourceException or InsecureSourceRoleException
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

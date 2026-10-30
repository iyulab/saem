using System.Net.Http.Headers;
using System.Reflection;
using Eyu.Core.Inference;
using Eyu.Core.Inference.Http;
using Eyu.Core.Judgment;
using Saem.Authoring.Cli;
using Saem.Connectors.Formbase;
using Saem.Ontology;

if (args is ["settle", .. var settleArgs])
{
    return await Settle(settleArgs);
}

if (args is ["diff", .. var diffArgs])
{
    return await Diff(diffArgs);
}

if (args is [] or ["--help"] or ["-h"])
{
    Console.WriteLine(AuthorOptions.Usage);
    return args is [] ? 2 : 0;
}

AuthorOptions options;
try
{
    options = AuthorOptions.Parse(args, Environment.GetEnvironmentVariable);
}
catch (AuthorUsageException e)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine("Run saem-author --help for the arguments.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var formbase = new HttpClient { BaseAddress = options.Formbase };
if (options.FormbaseToken is { } formbaseToken)
{
    formbase.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", formbaseToken);
}

using var model = new HttpClient { BaseAddress = options.ModelEndpoint, Timeout = options.ModelTimeout };
if (options.ModelApiKey is { } apiKey)
{
    model.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
}

var retries = new List<ModelCallRetry>();
var proposer = new SinglePassOntologyProposer(new RetryingModelClient(
    new HttpModelClient(model, options.Model, stream: options.ModelStream),
    options.Retries,
    onRetry: retry =>
    {
        retries.Add(retry);
        Console.Error.WriteLine($"model call attempt {retry.Attempt} failed ({retry.Failure.GetType().Name}: {retry.Failure.Message}); trying again.");
    }));
var connector = new FormbaseConnector(formbase, options.FormbaseNamespace, options.Sample);

try
{
    await AuthorRun.RunAsync(options, proposer, connector, connector, retries, Console.Out, cancellation.Token, DescribeProposer());
    return 0;
}
catch (Exception e) when (e is AuthorRunException or OntologyDeclarationException or FormbaseConnectorException or HttpRequestException or FormatException
                          or InvalidOperationException or TaskCanceledException { InnerException: TimeoutException })
{
    Console.Error.WriteLine(e.Message);
    return 1;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}

static async Task<int> Settle(string[] args)
{
    if (args is [] or ["--help"] or ["-h"])
    {
        Console.WriteLine(SettleOptions.Usage);
        return args is [] ? 2 : 0;
    }

    try
    {
        await SettleRun.RunAsync(SettleOptions.Parse(args), DateTimeOffset.UtcNow, Console.Out, CancellationToken.None);
        return 0;
    }
    catch (AuthorUsageException e)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine("Run saem-author settle --help for the arguments.");
        return 2;
    }
    catch (Exception e) when (e is AuthorRunException or OntologyDeclarationException)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
}

static async Task<int> Diff(string[] args)
{
    if (args is [] or ["--help"] or ["-h"])
    {
        Console.WriteLine(DiffOptions.Usage);
        return args is [] ? 2 : 0;
    }

    try
    {
        return await DiffRun.RunAsync(DiffOptions.Parse(args), Console.Out, CancellationToken.None) ? 0 : 1;
    }
    catch (AuthorUsageException e)
    {
        Console.Error.WriteLine(e.Message);
        Console.Error.WriteLine("Run saem-author diff --help for the arguments.");
        return 2;
    }
    catch (OntologyDeclarationException e)
    {
        Console.Error.WriteLine(e.Message);
        return 2;
    }
}

static ProposerFacts DescribeProposer()
{
    var assembly = typeof(SinglePassOntologyProposer).Assembly;

    // The informational version carries the build's commit after a '+'; the version is what precedes it.
    var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
    return new ProposerFacts(version, SinglePassOntologyProposer.PromptFingerprint, SinglePassOntologyProposer.KnownTypesPromptFingerprint);
}

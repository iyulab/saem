using System.Net.Http.Headers;
using Eyu.Core.Inference;
using Eyu.Core.Inference.Http;
using Eyu.Core.Judgment;
using Saem.Authoring.Cli;
using Saem.Connectors.Formbase;
using Saem.Ontology;

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
var connector = new FormbaseConnector(formbase, options.FormbaseNamespace);

try
{
    await AuthorRun.RunAsync(options, proposer, connector, connector, retries, Console.Out, cancellation.Token);
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

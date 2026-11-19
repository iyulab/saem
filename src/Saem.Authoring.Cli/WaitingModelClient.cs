using System.Diagnostics;
using System.Globalization;
using Eyu.Core.Inference;
using Eyu.Core.Ports;

namespace Saem.Authoring.Cli;

/// <summary>
/// Says out loud that it is waiting for the model, every so often: a model that thinks takes minutes, and a run that says nothing
/// in that time looks stopped.
/// </summary>
/// <param name="inner">The client that asks the model.</param>
/// <param name="log">Where the waiting is said.</param>
/// <param name="every">How long to wait between words.</param>
internal sealed class WaitingModelClient(IModelClient inner, TextWriter log, TimeSpan every) : IModelClient
{
    private int _calls;

    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var call = Interlocked.Increment(ref _calls);
        var watch = Stopwatch.StartNew();
        using var done = new CancellationTokenSource();
        var talking = TalkAsync(call, watch, done.Token);
        try
        {
            return await inner.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await done.CancelAsync().ConfigureAwait(false);
            await talking.ConfigureAwait(false);
            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"model call {call} came back after {(int)watch.Elapsed.TotalSeconds} s")).ConfigureAwait(false);
        }
    }

    private async Task TalkAsync(int call, Stopwatch watch, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"still waiting for the model (call {call}, {(int)watch.Elapsed.TotalSeconds} s) — a model that thinks takes minutes")).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The call came back; nothing more to say.
        }
    }
}

using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Xunit;

namespace Saem.Rig.Model;

/// <summary>
/// A live test's model call, tried a second time when the first attempt throws. A shared gateway drops a
/// call now and then — a stream cut short, an upstream error after the 200 — and the same call goes
/// through when it runs again; without a retry a test that drafts with a real model fails on the gateway,
/// not on what it asserts. One retry, not a loop: a call that fails twice fails the test. The first
/// failure is written to the test's diagnostics, so a run that needed the retry says so.
/// </summary>
internal sealed class RetryOnceModelClient(IModelClient inner) : IModelClient
{
    public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await inner.CompleteAsync(request, cancellationToken);
        }
        catch (Exception first) when (first is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            TestContext.Current.SendDiagnosticMessage($"model call retried after: {first.GetType().Name}: {first.Message}");
            return await inner.CompleteAsync(request, cancellationToken);
        }
    }
}

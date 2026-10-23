using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Xunit;

namespace Saem.Rig.Model;

/// <summary>
/// A live test's model, each call tried once more when it fails transiently (<see cref="RetryingModelClient"/>). A
/// shared gateway drops a call now and then — a stream cut short, an upstream error after the 200 — and the same call
/// goes through when it runs again; without a retry a test that drafts with a real model fails on the gateway, not on
/// what it asserts. A call that fails twice fails the test. Each retry is written to the test's diagnostics, so a run
/// that needed one says so.
/// </summary>
internal static class LiveModelRetry
{
    public static IModelClient Around(IModelClient inner) => new RetryingModelClient(
        inner,
        retries: 1,
        onRetry: retry => TestContext.Current.SendDiagnosticMessage(
            $"model call retried after: {retry.Failure.GetType().Name}: {retry.Failure.Message}"));
}

using Eyu.Core.Inference;
using Eyu.Core.Ports;
using Saem.Authoring.Cli;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>A run that waits on a model says so while it waits, and says how long the call took once it is back.</summary>
public sealed class WaitingModelClientTests
{
    [Fact]
    public async Task A_slow_call_is_talked_through_and_its_answer_is_passed_on_untouched()
    {
        var log = new StringWriter();
        var client = new WaitingModelClient(new SlowModel(TimeSpan.FromMilliseconds(400)), log, TimeSpan.FromMilliseconds(60));

        var answer = await client.CompleteAsync(new ModelRequest("question"), TestContext.Current.CancellationToken);

        Assert.Equal("answer", answer.Text);
        var said = log.ToString();
        Assert.Contains("still waiting for the model (call 1,", said, StringComparison.Ordinal);
        Assert.Contains("model call 1 came back after", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_quick_call_says_only_that_it_came_back_and_a_failure_still_says_so_before_it_surfaces()
    {
        var log = new StringWriter();
        var quick = new WaitingModelClient(new SlowModel(TimeSpan.Zero), log, TimeSpan.FromMinutes(5));
        await quick.CompleteAsync(new ModelRequest("q"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("still waiting", log.ToString(), StringComparison.Ordinal);

        var failing = new WaitingModelClient(new FailingModel(), log, TimeSpan.FromMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.CompleteAsync(new ModelRequest("q"), TestContext.Current.CancellationToken));
        Assert.Contains("model call 1 came back after", log.ToString(), StringComparison.Ordinal);
    }

    private sealed class SlowModel(TimeSpan delay) : IModelClient
    {
        public async Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            return new ModelResponse("answer");
        }
    }

    private sealed class FailingModel : IModelClient
    {
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("down");
    }
}

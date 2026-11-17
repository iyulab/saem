using System.Text.Json;
using Saem.Authoring.Cli;
using Xunit;

namespace Saem.Authoring.Tests;

/// <summary>
/// How a run that did not finish ends the process: with a line and a code, whatever was thrown. An exception left unhandled does not
/// end a .NET 10 process on arm64 Linux — it stays up at full CPU — so nothing is left unhandled.
/// </summary>
public sealed class RunFailureTests
{
    [Fact]
    public void A_failure_the_command_names_is_its_message_and_code_1()
    {
        var error = new StringWriter();

        var code = RunFailure.Report(new AuthorRunException("The model gave no ontology."), cancelled: false, error);

        Assert.Equal(1, code);
        Assert.Equal("The model gave no ontology." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void A_failed_model_call_is_code_1_with_its_message()
    {
        var error = new StringWriter();

        var code = RunFailure.Report(new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended before [DONE]."), cancelled: false, error);

        Assert.Equal(1, code);
        Assert.Contains("ended before [DONE]", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_nothing_expected_is_code_1_and_names_what_it_was()
    {
        var error = new StringWriter();

        var code = RunFailure.Report(new JsonException("Expected end of string, but instead reached end of data."), cancelled: false, error);

        Assert.Equal(1, code);
        Assert.Contains("JsonException", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Expected end of string", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_cancel_the_person_asked_for_is_code_130()
    {
        var error = new StringWriter();

        var code = RunFailure.Report(new OperationCanceledException(), cancelled: true, error);

        Assert.Equal(130, code);
        Assert.Equal("Cancelled." + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void A_cancel_nobody_asked_for_is_a_failure_not_a_cancel()
    {
        var error = new StringWriter();

        var code = RunFailure.Report(new OperationCanceledException("A timeout."), cancelled: false, error);

        Assert.Equal(1, code);
        Assert.Contains("OperationCanceledException", error.ToString(), StringComparison.Ordinal);
    }
}

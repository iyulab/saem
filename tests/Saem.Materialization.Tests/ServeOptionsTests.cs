using Saem.Materialization.Cli;
using Xunit;

namespace Saem.Materialization.Tests;

/// <summary>saem D-90: what <c>saem-materialize serve</c> is told, and the refusals before it listens.</summary>
public sealed class ServeOptionsTests
{
    private const string Secret = "first-secret-of-at-least-24-chars";

    private static readonly string[] Pass =
        ["--catalog", "c.json", "--sources", "s.json", "--group", "plant", "--base-iri", "https://example.test/", "--store", "http://store.test"];

    [Fact]
    public void The_arguments_of_a_pass_and_an_address_and_a_secret_are_what_it_takes()
    {
        var options = ServeOptions.Parse([.. Pass, "--listen", "http://127.0.0.1:8080"], Environment(Secret));

        Assert.Equal("plant", options.Run.Group);
        Assert.Equal(new Uri("http://127.0.0.1:8080"), options.Listen);
        Assert.Equal([Secret], options.Secrets);
    }

    [Fact]
    public void A_second_secret_is_accepted_alongside_the_first()
    {
        var next = "next-secret-of-at-least-24-chars!";

        var options = ServeOptions.Parse([.. Pass, "--listen", "https://0.0.0.0:8443"], Environment(Secret, next));

        Assert.Equal([Secret, next], options.Secrets);
    }

    [Theory]
    [InlineData("listen", "--listen is required.")]
    [InlineData("secret", "SAEM_EXECUTOR_SECRET is required")]
    [InlineData("short", "at least 24 characters")]
    [InlineData("same", "must differ")]
    [InlineData("changed", "no --changed")]
    [InlineData("scheme", "http(s) address")]
    [InlineData("twice", "more than once")]
    public void A_command_line_it_cannot_serve_is_refused_before_listening(string fault, string message)
    {
        var args = fault switch
        {
            "listen" => Pass,
            "changed" => [.. Pass, "--changed", "--listen", "http://127.0.0.1:8080"],
            "scheme" => [.. Pass, "--listen", "ftp://example.test"],
            "twice" => [.. Pass, "--listen", "http://127.0.0.1:1", "--listen", "http://127.0.0.1:2"],
            _ => [.. Pass, "--listen", "http://127.0.0.1:8080"],
        };
        var environment = fault switch
        {
            "secret" => Environment(),
            "short" => Environment("too-short"),
            "same" => Environment(Secret, Secret),
            _ => Environment(Secret),
        };

        var refused = Assert.Throws<MaterializeUsageException>(() => ServeOptions.Parse(args, environment));

        Assert.Contains(message, refused.Message);
    }

    [Fact]
    public void The_kinds_a_request_may_name_are_the_commands_of_a_group()
    {
        var run = MaterializeOptions.Parse(Pass, _ => null);

        var kinds = ExecutorJobs.ForFiles(run, _ => null);

        Assert.Equal(["changed-pass", "check", "fit", "pass", "verify"], kinds.Keys.Order(StringComparer.Ordinal));
    }

    private static Func<string, string?> Environment(string? secret = null, string? next = null) => name => name switch
    {
        ServeOptions.SecretVariable => secret,
        ServeOptions.NextSecretVariable => next,
        _ => null,
    };
}

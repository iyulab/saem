using Xunit;

namespace Saem.Query.Tests;

public class PersonSessionsTests
{
    [Fact]
    public async Task A_system_the_lookup_has_no_login_for_is_named_not_opened_as_someone_else()
    {
        var sessions = new PersonSessions(new Dictionary<string, SourceOpener>());

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sessions.OpenAsync("erp", TestContext.Current.CancellationToken));

        Assert.Contains("'erp'", refused.Message, StringComparison.Ordinal);
    }
}

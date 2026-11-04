using System.Net.Http.Json;
using System.Text.Json;
using MySqlConnector;
using Xunit;

namespace Saem.Connectors.MySql.Tests.Live;

/// <summary>
/// The adapter against the rig's erp source with the person's own token as the login: the source
/// checks the token's signature, issuer, audience and expiry and logs the session in as the account
/// the token's subject was created for, so the views' row rules see the person — not a password
/// standing in for them. The token is fetched when the session opens, never earlier, because it
/// lives twenty seconds.
/// </summary>
public class MySqlTokenLoginLiveTests
{
    private static readonly MySqlSource Erp = new(Required("SAEM_RIG_ERP"));

    private static readonly string Issuer = Required("SAEM_RIG_ISSUER");

    private static string Required(string variable) => Environment.GetEnvironmentVariable(variable)
        ?? throw new InvalidOperationException($"{variable} names one of the rig's parts as the host reaches it (tests/rig/compose.password-login.yaml).");

    /// <summary>
    /// A token for <paramref name="person"/> meant for <paramref name="audience"/>. The server trusts the issuer under the
    /// name the rig's network gives it, so the request names that host: the token says who issued it by the host it was
    /// asked at.
    /// </summary>
    private static async ValueTask<string> TokenAsync(string person, string audience, CancellationToken ct)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Issuer}/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = person, ["client_secret"] = "unused",
                ["scope"] = "openid", ["audience"] = audience,
            }),
        };
        request.Headers.Host = "issuer:8080";
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;
    }

    private static async Task<long> VisibleSuppliersAsync(string person, string account, string audience, CancellationToken ct)
    {
        await using var session = await Erp.As(account, c => TokenAsync(person, audience, c))(ct);
        var rows = 0L;
        await foreach (var row in session.ScanAsync("visible_suppliers", "supplier_id", ["supplier_name"], ct))
        {
            rows++;
            _ = row;
        }

        return rows;
    }

    [Fact]
    public async Task A_token_logs_the_session_in_as_the_person_and_the_source_decides_what_that_person_reads()
    {
        var ct = TestContext.Current.CancellationToken;

        // Alice may read both suppliers, bob only S-1: the same read, by the same code, as two people.
        Assert.Equal(2, await VisibleSuppliersAsync("alice", "alice", "saem-rig-erp", ct));
        Assert.Equal(1, await VisibleSuppliersAsync("bob", "bob", "saem-rig-erp", ct));
    }

    [Fact]
    public async Task Another_persons_token_is_refused_by_the_source()
    {
        var ct = TestContext.Current.CancellationToken;

        // Bob's token for alice's account: the account was created for alice's subject.
        var refused = await Assert.ThrowsAsync<MySqlException>(() => VisibleSuppliersAsync("bob", "alice", "saem-rig-erp", ct));
        Assert.Contains("denied", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_token_meant_for_another_system_is_refused_by_the_source()
    {
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<MySqlException>(() => VisibleSuppliersAsync("alice", "alice", "saem-rig-mes", ct));
    }
}

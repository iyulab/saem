using System.Text;
using Xunit;

namespace Saem.Connectors.MySql.Tests;

/// <summary>What the token login says to the source: one capability byte, then the token, length-encoded.</summary>
public class OidcClientAuthenticationPluginTests
{
    private static readonly OidcClientAuthenticationPlugin Plugin = new();

    [Fact]
    public void The_plugin_answers_the_method_the_source_switches_to()
    {
        Assert.Equal("authentication_openid_connect_client", Plugin.Name);
    }

    [Theory]
    [InlineData(1, new byte[] { 1 })]
    [InlineData(250, new byte[] { 250 })]
    [InlineData(251, new byte[] { 0xFC, 251, 0 })]
    [InlineData(65535, new byte[] { 0xFC, 0xFF, 0xFF })]
    [InlineData(65536, new byte[] { 0xFD, 0, 0, 1 })]
    public void A_token_is_sent_after_the_capability_byte_with_a_length_prefix_of_its_size(int tokenLength, byte[] prefix)
    {
        var token = new string('t', tokenLength);

        var response = Plugin.CreateResponse(token, []);

        Assert.Equal(1, response[0]);
        Assert.Equal(prefix, response[1..(1 + prefix.Length)]);
        Assert.Equal(Encoding.UTF8.GetBytes(token), response[(1 + prefix.Length)..]);
    }

    [Fact]
    public void What_the_source_sent_with_the_switch_does_not_change_the_answer()
    {
        Assert.Equal(Plugin.CreateResponse("token", []), Plugin.CreateResponse("token", [1, 2, 3]));
    }

    [Fact]
    public void An_empty_token_is_refused_before_it_is_sent()
    {
        Assert.Throws<ArgumentException>(() => Plugin.CreateResponse("", []));
    }

    [Fact]
    public void A_second_registration_does_not_fail()
    {
        // The driver refuses a second plugin under one name; a host that registered its own, or a second
        // source in the process, must still get a usable source.
        OidcClientAuthenticationPlugin.Register();
        var exception = Record.Exception(OidcClientAuthenticationPlugin.Register);

        Assert.Null(exception);
        Assert.NotNull(new MySqlSource("Server=erp;Database=erp"));
    }
}

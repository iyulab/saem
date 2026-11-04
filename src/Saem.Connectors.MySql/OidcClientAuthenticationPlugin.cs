using System.Text;
using MySqlConnector.Authentication;

namespace Saem.Connectors.MySql;

/// <summary>
/// The client side of a MySQL server's OpenID Connect login: the server asks to switch to
/// <c>authentication_openid_connect_client</c>, and the answer is the person's access token. The token
/// travels in the session's password slot; the source checks its signature, issuer, audience and
/// expiry, and that its subject is the account's.
/// <para>
/// The response is one capability byte (1: the token follows), then the token as a length-encoded
/// string. The server's own data for the switch carries nothing the answer depends on.
/// </para>
/// </summary>
internal sealed class OidcClientAuthenticationPlugin : IAuthenticationPlugin
{
    internal const string MethodName = "authentication_openid_connect_client";

    private const byte TokenFollows = 1;

    public string Name => MethodName;

    /// <summary>
    /// Makes the driver answer the OpenID Connect switch. A host that already registered a plugin under
    /// the method's name keeps its own: the driver refuses a second registration, and that must not stop
    /// the source from being used.
    /// </summary>
    internal static void Register()
    {
        try
        {
            AuthenticationPlugins.Register(new OidcClientAuthenticationPlugin());
        }
        catch (ArgumentException)
        {
            // Registered already — by this assembly's other source, or by the host.
        }
    }

    public byte[] CreateResponse(string password, ReadOnlySpan<byte> authenticationData)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);

        var token = Encoding.UTF8.GetBytes(password);
        var length = LengthPrefix(token.Length);
        var response = new byte[1 + length.Length + token.Length];
        response[0] = TokenFollows;
        length.CopyTo(response, 1);
        token.CopyTo(response, 1 + length.Length);
        return response;
    }

    /// <summary>MySQL's length-encoded integer, for the sizes a token can have (under 16 MiB).</summary>
    private static byte[] LengthPrefix(int length) => length switch
    {
        < 251 => [(byte)length],
        < 1 << 16 => [0xFC, (byte)length, (byte)(length >> 8)],
        < 1 << 24 => [0xFD, (byte)length, (byte)(length >> 8), (byte)(length >> 16)],
        _ => throw new InvalidOperationException("A login token this long cannot be sent."),
    };
}

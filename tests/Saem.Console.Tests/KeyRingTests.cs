using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Saem.Console.Tests;

/// <summary>
/// The session cookie is sealed with the data-protection key ring, so consoles behind one address read one another's
/// sessions only if they share the ring and the application name.
/// </summary>
public sealed class KeyRingTests : IDisposable
{
    private readonly string _keys = Directory.CreateTempSubdirectory("saem-console-keys-").FullName;

    public void Dispose() => Directory.Delete(_keys, recursive: true);

    [Fact]
    public void Consoles_that_share_a_key_directory_read_one_anothers_cookies()
    {
        var first = Protector(keysPath: _keys);
        var second = Protector(keysPath: _keys);

        var sealedSession = first.Protect("session");

        Assert.Equal("session", second.Unprotect(sealedSession));
        Assert.NotEmpty(Directory.GetFiles(_keys, "key-*.xml"));
    }

    [Fact]
    public void A_console_with_its_own_key_ring_cannot_read_anothers_cookie()
    {
        var alone = Protector(keysPath: Path.Combine(_keys, "alone"));
        var other = Protector(keysPath: Path.Combine(_keys, "other"));

        Assert.Throws<CryptographicException>(() => other.Unprotect(alone.Protect("session")));
    }

    [Fact]
    public void A_different_application_name_does_not_share_the_ring()
    {
        var first = Protector(keysPath: _keys);
        var second = Protector(keysPath: _keys, applicationName: "another-console");

        Assert.Throws<CryptographicException>(() => second.Unprotect(first.Protect("session")));
    }

    private static IDataProtector Protector(string keysPath, string? applicationName = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Saem:Console:Identity:Authority"] = ConsoleServer.Issuer,
            ["Saem:Console:Identity:Audience"] = ConsoleServer.Audience,
            ["Saem:Console:Identity:ClientId"] = "saem-console",
            ["Saem:Console:Ontology"] = "unused.json",
            ["Saem:Console:Roles:Builders:0"] = "ontology-builders",
            ["Saem:Console:DataProtection:KeysPath"] = keysPath,
        };
        if (applicationName is not null)
        {
            settings["Saem:Console:DataProtection:ApplicationName"] = applicationName;
        }

        var services = new ServiceCollection();
        services.AddSaemConsole(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>().CreateProtector("session");
    }
}

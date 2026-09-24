using AS24Net.Import.Legacy;

namespace AS24Net.Import.Tests;

public class LegacyReaderTests
{
    [Fact]
    public void ReadsServersRoutingsAndCertificatesInTheirOrder()
    {
        using var fixture = new LegacyFixture();
        var server = fixture.AddServer("acme", "https://as2.acme.example/as2", asyncMdn: true,
            routings: [("ARTIPA", "ACME-PLANT1", "Acme Plant 1", true), ("ARTIPA", "ACME-PLANT2", "Acme Plant 2", false)]);
        var newer = fixture.AddCertificate(server, new DateTime(2026, 3, 1, 6, 0, 0, DateTimeKind.Utc), "acme-2026");
        var older = fixture.AddCertificate(server, new DateTime(2024, 3, 1, 6, 0, 0, DateTimeKind.Utc), "acme-2024");
        var warnings = new List<string>();

        var servers = LegacyReader.ReadServers(fixture.Etc, warnings);

        Assert.Empty(warnings);
        var read = Assert.Single(servers);
        Assert.Equal("acme", read.Name);
        Assert.Equal("https://as2.acme.example/as2", read.ReceiverURL);
        Assert.True(read.AsyncMDN);
        Assert.Equal("bcc", read.SecurityProvider);
        Assert.Equal(["ACME-PLANT1", "ACME-PLANT2"], read.Routings!.Select(r => r.PartnerAS2ID));
        Assert.Equal([older, newer], read.Certificates.Select(c => c.File.Thumbprint));
        Assert.Equal(new DateTime(2024, 3, 1, 6, 0, 0, DateTimeKind.Utc), read.Certificates[0].UsedFromUtc);
        Assert.Equal(DateTimeKind.Utc, read.Certificates[0].UsedFromUtc.Kind);
        Assert.EndsWith(".cer", read.Certificates[0].File.FileName);
    }

    [Fact]
    public void TakesTheServersDirectoryItselfToo()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);

        var servers = LegacyReader.ReadServers(Path.Combine(fixture.Etc, "servers"), new List<string>());

        Assert.Single(servers);
    }

    [Fact]
    public void SkipsWhatCannotBeReadWithAWarning()
    {
        using var fixture = new LegacyFixture();
        var server = fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);
        Directory.CreateDirectory(Path.Combine(server, "cert", "not a date"));
        var broken = Path.Combine(server, "cert", "2025-01-01 000000");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "x.cer"), "not a certificate");
        Directory.CreateDirectory(Path.Combine(fixture.Etc, "servers", "empty"));
        var warnings = new List<string>();

        var servers = LegacyReader.ReadServers(fixture.Etc, warnings);

        Assert.Single(servers);
        Assert.Empty(servers[0].Certificates);
        Assert.Equal(3, warnings.Count);
    }

    [Fact]
    public void ReadsTheSecurityProvidersOfTheOldSettings()
    {
        using var fixture = new LegacyFixture();
        var file = Path.Combine(fixture.Root, "appsettings.json");
        File.WriteAllText(file, """
            {
              /* the old host's settings */
              "Settings": {
                "EtcDir": "/opt/artipa/as2/etc",
                "SecurityProviders": [
                  { "Name": "bcc", "CertPassword": "secret", "CertFileName": "as2.pfx", "TypeName": "x" },
                  { "Name": "bcc-SHA256-AES256", "CertPassword": "secret", "CertFileName": "as2.pfx", "EncryptionAlgorithm": "AES-256" },
                ]
              }
            }
            """);

        var settings = LegacyReader.ReadSettings(file);

        Assert.Equal(2, settings.SecurityProviders!.Length);
        Assert.Equal("AES-256", settings.SecurityProviders[1].EncryptionAlgorithm);
        Assert.Equal("as2.pfx", settings.SecurityProviders[0].CertFileName);
    }
}

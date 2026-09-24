using AS24Net.Import.Legacy;
using AS24Net.Import.Planning;

namespace AS24Net.Import.Tests;

public class ImportPlannerTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ImportPlan Plan(LegacyFixture fixture, PlannerOptions? options = null)
    {
        var warnings = new List<string>();
        var servers = LegacyReader.ReadServers(fixture.Etc, warnings);
        Assert.Empty(warnings);
        return ImportPlanner.Plan(servers, options ?? new PlannerOptions { NowUtc = Now });
    }

    [Fact]
    public void PartnersOfOneServerShareItsConnection()
    {
        using var fixture = new LegacyFixture();
        var server = fixture.AddServer("acme", "https://as2.acme.example/as2",
            routings: [("ARTIPA", "ACME-PLANT1", "Acme Plant 1", true), ("ARTIPA", "ACME-PLANT2", "Acme Plant 2", false)]);
        var thumbprint = fixture.AddCertificate(server, Now.AddYears(-1), "acme");

        var plan = Plan(fixture);

        var connection = Assert.Single(plan.Connections);
        Assert.Equal("acme", connection.Name);
        Assert.Equal("https://as2.acme.example/as2", connection.Url);
        Assert.Equal(thumbprint, connection.Certificate!.Thumbprint);
        Assert.Equal("sha1", connection.SignatureAlgorithm);
        Assert.Equal("3des", connection.EncryptionAlgorithm);
        Assert.Equal("Sync", connection.MdnMode);
        Assert.True(connection.RequestSignedMdn);
        Assert.Equal("Bob (Acme)", connection.ContactName);
        Assert.Equal("bob@acme.example", connection.ContactEmail);

        Assert.Equal(2, plan.Partners.Count);
        Assert.All(plan.Partners, p => Assert.Equal("acme", p.Connection));
        Assert.All(plan.Partners, p => Assert.Equal("ARTIPA", p.DefaultIdentity));
        Assert.All(plan.Partners, p => Assert.Equal("application/edifact", p.ContentType));
        Assert.False(plan.Partners.Single(p => p.As2Id == "ACME-PLANT2").Enabled);

        var identity = Assert.Single(plan.Identities);
        Assert.Equal("ARTIPA", identity.As2Id);
        Assert.Equal("Artipa ARTIPA", identity.Name);
        Assert.Null(identity.Certificate);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void TheCertificateInUseIsSetAndLaterOnesAreScheduled()
    {
        using var fixture = new LegacyFixture();
        var server = fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);
        fixture.AddCertificate(server, Now.AddYears(-3), "acme-old");
        var current = fixture.AddCertificate(server, Now.AddMonths(-6), "acme-current");
        var next = fixture.AddCertificate(server, Now.AddDays(10), "acme-next");

        var connection = Assert.Single(Plan(fixture).Connections);

        Assert.Equal(current, connection.Certificate!.Thumbprint);
        var change = Assert.Single(connection.Changes);
        Assert.Equal(next, change.Certificate.Thumbprint);
        Assert.Equal(Now.AddDays(10).Date, change.ActivateAtUtc.Date);
    }

    [Fact]
    public void WithOnlyAFutureCertificateItIsUsedRightAway()
    {
        using var fixture = new LegacyFixture();
        var server = fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);
        var only = fixture.AddCertificate(server, Now.AddDays(5), "acme");

        var connection = Assert.Single(Plan(fixture).Connections);

        Assert.Equal(only, connection.Certificate!.Thumbprint);
        Assert.Empty(connection.Changes);
    }

    [Fact]
    public void WithoutACertificateEncryptionAndRequiredSignaturesAreOff()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);

        var plan = Plan(fixture);

        var connection = Assert.Single(plan.Connections);
        Assert.False(connection.EncryptMessages);
        Assert.False(connection.RequireSignedMessages);
        Assert.True(connection.SignMessages);
        Assert.Contains(plan.Warnings, w => w.Contains("no certificate of the partner"));
    }

    [Theory]
    [InlineData(false, false, "None")]
    [InlineData(true, false, "Sync")]
    [InlineData(true, true, "Async")]
    public void MdnModeFollowsTheServer(bool requestMdn, bool asyncMdn, string expected)
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("acme", "https://acme.example/as2", requestMdn: requestMdn, asyncMdn: asyncMdn,
            routings: [("ARTIPA", "ACME", "Acme", true)]);

        var connection = Assert.Single(Plan(fixture).Connections);

        Assert.Equal(expected, connection.MdnMode);
        Assert.Equal(requestMdn, connection.RequestSignedMdn);
    }

    [Fact]
    public void EncryptionComesFromTheSecurityProvider()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("a", "https://a.example/as2", securityProvider: "bcc-SHA256-AES256", routings: [("ARTIPA", "A", "A", true)]);
        fixture.AddServer("b", "https://b.example/as2", securityProvider: "bcc", routings: [("ARTIPA", "B", "B", true)]);
        fixture.AddServer("c", "https://c.example/as2", securityProvider: "rc2", routings: [("ARTIPA", "C", "C", true)]);
        var settings = LegacyFixture.Settings(("bcc-SHA256-AES256", "as2.pfx", "pw", "AES-256"), ("bcc", "as2.pfx", "pw", null),
            ("rc2", "as2.pfx", "pw", "RC2"));

        var plan = Plan(fixture, new PlannerOptions { NowUtc = Now, Settings = settings });

        Assert.Equal(["aes256-cbc", "3des", "3des"], plan.Connections.Select(c => c.EncryptionAlgorithm));
        Assert.Contains(plan.Warnings, w => w.Contains("RC2"));
    }

    [Fact]
    public void WithoutTheSettingsTheNameOfTheProviderTellsTheEncryption()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("a", "https://a.example/as2", securityProvider: "bcc-SHA256-AES256", routings: [("ARTIPA", "A", "A", true)]);

        Assert.Equal("aes256-cbc", Assert.Single(Plan(fixture).Connections).EncryptionAlgorithm);
    }

    [Fact]
    public void APartnerTalkingToSeveralIdentitiesGetsNoDefaultIdentity()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true), ("ARTIPA-CZ", "ACME", "Acme", false)]);

        var plan = Plan(fixture);

        var partner = Assert.Single(plan.Partners);
        Assert.Null(partner.DefaultIdentity);
        Assert.True(partner.Enabled);
        Assert.Equal(2, plan.Identities.Count);
        Assert.Contains(plan.Warnings, w => w.Contains("no default identity"));
    }

    [Fact]
    public void APartnerOnTwoServersTakesTheFirstOneWithAWarning()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("acme-a", "https://a.acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);
        fixture.AddServer("acme-b", "https://b.acme.example/as2", routings: [("ARTIPA-CZ", "ACME", "Acme", true)]);

        var plan = Plan(fixture);

        Assert.Equal("acme-a", Assert.Single(plan.Partners).Connection);
        Assert.Contains(plan.Warnings, w => w.Contains("one connection per partner"));
    }

    [Fact]
    public void IdenticalServersAreReportedOrMerged()
    {
        using var fixture = new LegacyFixture();
        var a = fixture.AddServer("acme-1", "https://acme.example/as2", routings: [("ARTIPA", "ACME1", "Acme 1", true)]);
        var b = fixture.AddServer("acme-2", "https://ACME.example/as2", routings: [("ARTIPA", "ACME2", "Acme 2", true)]);
        var certificate = Path.Combine(a, "cert");
        fixture.AddCertificate(a, Now.AddYears(-1), "acme");
        // The same certificate file in the other server.
        CopyDirectory(certificate, Path.Combine(b, "cert"));

        var separate = Plan(fixture);
        Assert.Equal(2, separate.Connections.Count);
        Assert.Contains(separate.Warnings, w => w.Contains("--merge-identical"));

        var merged = Plan(fixture, new PlannerOptions { NowUtc = Now, MergeIdentical = true });
        var connection = Assert.Single(merged.Connections);
        Assert.Equal("acme-1", connection.Name);
        Assert.Equal(2, connection.Sources.Count);
        Assert.All(merged.Partners, p => Assert.Equal("acme-1", p.Connection));
    }

    [Fact]
    public void OnlyTheServersAskedFor()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("acme", "https://acme.example/as2", routings: [("ARTIPA", "ACME", "Acme", true)]);
        fixture.AddServer("other", "https://other.example/as2", routings: [("ARTIPA", "OTHER", "Other", true)]);

        var plan = Plan(fixture, new PlannerOptions { NowUtc = Now, Servers = ["OTHER", "missing"] });

        Assert.Equal("other", Assert.Single(plan.Connections).Name);
        Assert.Equal("OTHER", Assert.Single(plan.Partners).As2Id);
        Assert.Contains(plan.Warnings, w => w.Contains("no server missing"));
    }

    [Fact]
    public void TheIdentityGetsTheCertificateOfItsSecurityProvider()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("a", "https://a.example/as2", securityProvider: "bcc", routings: [("ARTIPA", "A", "A", true)]);
        fixture.AddServer("b", "https://b.example/as2", securityProvider: "bcc-SHA256", routings: [("ARTIPA", "B", "B", true)]);
        fixture.AddServer("c", "https://c.example/as2", securityProvider: "bcc2", routings: [("ARTIPA", "C", "C", true)]);
        var thumbprint = fixture.AddPkcs12("as2_2018.pfx", "secret", "as2.artipa.example");
        fixture.AddPkcs12("as2.pfx", "secret", "as2-older.artipa.example");
        var settings = LegacyFixture.Settings(("bcc", "as2_2018.pfx", "secret", null), ("bcc-SHA256", "as2_2018.pfx", "secret", null),
            ("bcc2", "as2.pfx", "secret", null));

        var plan = Plan(fixture, new PlannerOptions { NowUtc = Now, Settings = settings, CertificateDirectory = fixture.CertDir });

        var identity = Assert.Single(plan.Identities);
        Assert.Equal(thumbprint, identity.Certificate!.Thumbprint);
        Assert.True(identity.Certificate.HasPrivateKey);
        Assert.Equal("secret", identity.Certificate.Password);
        Assert.Contains(plan.Warnings, w => w.Contains("as2_2018.pfx is taken"));
    }

    [Fact]
    public void AWrongPasswordLeavesTheIdentityWithoutCertificate()
    {
        using var fixture = new LegacyFixture();
        fixture.AddServer("a", "https://a.example/as2", securityProvider: "bcc", routings: [("ARTIPA", "A", "A", true)]);
        fixture.AddPkcs12("as2.pfx", "secret", "as2.artipa.example");
        var settings = LegacyFixture.Settings(("bcc", "as2.pfx", "wrong", null));

        var plan = Plan(fixture, new PlannerOptions { NowUtc = Now, Settings = settings, CertificateDirectory = fixture.CertDir });

        Assert.Null(Assert.Single(plan.Identities).Certificate);
        Assert.Contains(plan.Warnings, w => w.Contains("cannot be read"));
    }

    [Fact]
    public void LongNamesAreShortened()
    {
        using var fixture = new LegacyFixture();
        var name = new string('x', 60);
        fixture.AddServer(name, "https://acme.example/as2", routings: [("ARTIPA", "ACME", new string('p', 70), true)]);

        var plan = Plan(fixture);

        Assert.Equal(ImportPlanner.MaxNameLength, plan.Connections[0].Name.Length);
        Assert.Equal(ImportPlanner.MaxNameLength, plan.Partners[0].Name.Length);
        Assert.Equal(plan.Connections[0].Name, plan.Partners[0].Connection);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, target));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target));
    }
}

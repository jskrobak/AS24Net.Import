namespace AS24Net.Import.Tests;

public class ImportOptionsTests
{
    [Fact]
    public void ADryRunNeedsOnlyTheEtcDirectory()
    {
        var options = ImportOptions.Parse(["--etc", "/old/etc", "--dry-run", "--server", "acme", "--server", "other"], _ => null);

        Assert.True(options.DryRun);
        Assert.Equal("/old/etc", options.EtcDirectory);
        Assert.Equal(["acme", "other"], options.Servers);
        Assert.Equal("sha1", options.SignatureAlgorithm);
    }

    [Fact]
    public void TheTokenCanComeFromTheEnvironment()
    {
        var options = ImportOptions.Parse(["--etc", "/old/etc", "--url", "https://as2.example.com"],
            name => name == "AS24NET_TOKEN" ? "a24_secret" : null);

        Assert.Equal("a24_secret", options.Token);
    }

    [Theory]
    [InlineData("--etc", "/old/etc")]
    [InlineData("--etc", "/old/etc", "--url", "https://as2.example.com")]
    [InlineData("--url", "https://as2.example.com", "--token", "t")]
    [InlineData("--etc")]
    [InlineData("--etc", "/old/etc", "--dry-run", "--cert-dir", "/old/data/cert")]
    [InlineData("--etc", "/old/etc", "--dry-run", "--bogus")]
    public void WrongArgumentsAreRefused(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => ImportOptions.Parse(args, _ => null));
    }
}

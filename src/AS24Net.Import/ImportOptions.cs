namespace AS24Net.Import;

/// <summary>The command line.</summary>
public sealed class ImportOptions
{
    public const string Usage = """
        Imports the partners of the old AS2 server (underware.AS2) into AS24Net.

        Usage:
          as24net-import --etc <dir> --dry-run
          as24net-import --etc <dir> --url <AS24Net URL> [--token <token>]

        Options:
          --etc <dir>                  etc directory of the old server (with servers/<name>/config.json), or servers itself
          --url <url>                  address of AS24Net, e.g. https://as2.example.com
          --token <token>              API token allowed to change the configuration; or the variable AS24NET_TOKEN
          --settings <file>            appsettings.json of the old server: the security providers (encryption, our certificate)
          --cert-dir <dir>             directory with our .pfx files named in the security providers (the old data/cert)
          --server <name>              only this server; can be repeated
          --merge-identical            servers with the same URL, settings and certificate become one connection
          --signature-algorithm <alg>  digest of our signatures, sha1 (as the old server) by default
          --content-type <type>        media type of the partners' payloads, application/edifact by default
          --dry-run                    show what would be imported and stop
          --help                       this text
        """;

    public string? EtcDirectory { get; private set; }
    public string? Url { get; private set; }
    public string? Token { get; private set; }
    public string? SettingsFile { get; private set; }
    public string? CertificateDirectory { get; private set; }
    public List<string> Servers { get; } = [];
    public bool MergeIdentical { get; private set; }
    public string SignatureAlgorithm { get; private set; } = "sha1";
    public string ContentType { get; private set; } = "application/edifact";
    public bool DryRun { get; private set; }
    public bool Help { get; private set; }

    /// <summary>Parses the arguments; throws <see cref="ArgumentException"/> with the reason when they are wrong.</summary>
    public static ImportOptions Parse(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        var options = new ImportOptions();
        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : throw new ArgumentException($"{args[i]} needs a value.");

            switch (args[i])
            {
                case "--etc": options.EtcDirectory = Value(); break;
                case "--url": options.Url = Value(); break;
                case "--token": options.Token = Value(); break;
                case "--settings": options.SettingsFile = Value(); break;
                case "--cert-dir": options.CertificateDirectory = Value(); break;
                case "--server": options.Servers.Add(Value()); break;
                case "--merge-identical": options.MergeIdentical = true; break;
                case "--signature-algorithm": options.SignatureAlgorithm = Value(); break;
                case "--content-type": options.ContentType = Value(); break;
                case "--dry-run": options.DryRun = true; break;
                case "--help" or "-h" or "-?": options.Help = true; break;
                default: throw new ArgumentException($"Unknown argument {args[i]}.");
            }
        }

        if (options.Help)
            return options;

        options.Token ??= (environment ?? Environment.GetEnvironmentVariable)("AS24NET_TOKEN");
        if (options.EtcDirectory is null)
            throw new ArgumentException("--etc is required.");
        if (!options.DryRun && (options.Url is null || string.IsNullOrWhiteSpace(options.Token)))
            throw new ArgumentException("--url and --token (or AS24NET_TOKEN) are required, unless --dry-run.");
        if (options.CertificateDirectory is not null && options.SettingsFile is null)
            throw new ArgumentException("--cert-dir needs --settings, which names the certificates.");
        return options;
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AS24Net.Import.Legacy;

/// <summary>Reads the configuration of the old AS2 server from its <c>etc</c> directory.</summary>
public static class LegacyReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads the servers of <paramref name="directory"/>: the <c>etc</c> directory (with <c>servers</c> in it) or the
    /// <c>servers</c> directory itself. A server that cannot be read is left out with a warning.
    /// </summary>
    public static List<LegacyServer> ReadServers(string directory, ICollection<string> warnings)
    {
        var serversDirectory = Path.Combine(directory, "servers");
        if (!Directory.Exists(serversDirectory))
            serversDirectory = directory;
        if (!Directory.Exists(serversDirectory))
            throw new ImportException($"The directory {directory} does not exist.");

        var servers = new List<LegacyServer>();
        foreach (var serverDirectory in Directory.GetDirectories(serversDirectory).Order(StringComparer.Ordinal))
        {
            var configFile = Path.Combine(serverDirectory, "config.json");
            if (!File.Exists(configFile))
            {
                warnings.Add($"{serverDirectory}: no config.json, skipped.");
                continue;
            }

            LegacyServer? server;
            try
            {
                server = JsonSerializer.Deserialize<LegacyServer>(File.ReadAllText(configFile), JsonOptions);
            }
            catch (JsonException ex)
            {
                warnings.Add($"{configFile}: {ex.Message} Skipped.");
                continue;
            }

            if (server is null)
                continue;
            server.Directory = serverDirectory;
            if (string.IsNullOrWhiteSpace(server.Name))
                server.Name = Path.GetFileName(serverDirectory);
            server.Certificates = ReadCertificates(Path.Combine(serverDirectory, "cert"), warnings);
            servers.Add(server);
        }

        return servers;
    }

    /// <summary>Reads the section <c>Settings</c> of the old <c>appsettings.json</c>.</summary>
    public static LegacySettings ReadSettings(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var section = document.RootElement.TryGetProperty("Settings", out var settings) ? settings : document.RootElement;
        return section.Deserialize<LegacySettings>(JsonOptions) ?? new LegacySettings();
    }

    /// <summary>
    /// The certificates in <c>cert/&lt;yyyy-MM-dd HHmmss&gt;/</c>, the name of the directory being the time (UTC) the
    /// partner uses the certificate from; the oldest first.
    /// </summary>
    public static List<LegacyCertificate> ReadCertificates(string certDirectory, ICollection<string> warnings)
    {
        var certificates = new List<LegacyCertificate>();
        if (!Directory.Exists(certDirectory))
            return certificates;

        foreach (var directory in Directory.GetDirectories(certDirectory))
        {
            if (!DateTime.TryParseExact(Path.GetFileName(directory), "yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var usedFrom))
            {
                warnings.Add($"{directory}: the name is not a time such as '2024-03-01 060000', skipped.");
                continue;
            }

            // The old server took the first file of the directory as well.
            var file = Directory.GetFiles(directory).Order(StringComparer.Ordinal).FirstOrDefault();
            if (file is null)
            {
                warnings.Add($"{directory}: no certificate file, skipped.");
                continue;
            }

            try
            {
                certificates.Add(new LegacyCertificate(usedFrom, file, ReadPublicCertificate(file)));
            }
            catch (CryptographicException ex)
            {
                warnings.Add($"{file}: not a certificate ({ex.Message}), skipped.");
            }
        }

        return certificates.OrderBy(c => c.UsedFromUtc).ToList();
    }

    /// <summary>A certificate without the private key (DER or PEM), sent on as DER.</summary>
    public static CertificateFile ReadPublicCertificate(string file)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(file));
        return new CertificateFile(Path.GetFileNameWithoutExtension(file) + ".cer", certificate.RawData, null, certificate.Subject,
            certificate.Thumbprint, certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), HasPrivateKey: false);
    }

    /// <summary>Our certificate with the private key (PKCS#12); the password is checked here.</summary>
    public static CertificateFile ReadPkcs12(string file, string? password)
    {
        var data = File.ReadAllBytes(file);
        using var certificate = X509CertificateLoader.LoadPkcs12(data, password);
        if (!certificate.HasPrivateKey)
            throw new ImportException($"{file} has no private key.");
        return new CertificateFile(Path.GetFileNameWithoutExtension(file) + ".pfx", data, password, certificate.Subject,
            certificate.Thumbprint, certificate.NotBefore.ToUniversalTime(), certificate.NotAfter.ToUniversalTime(), HasPrivateKey: true);
    }
}

/// <summary>The import cannot go on; the message says why.</summary>
public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

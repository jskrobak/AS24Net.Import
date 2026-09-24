using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AS24Net.Import.Legacy;

namespace AS24Net.Import.Tests;

/// <summary>An etc directory of the old AS2 server in a temporary directory, written as the old server writes it.</summary>
public sealed class LegacyFixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("as24net-import-").FullName;

    public string Etc => Path.Combine(Root, "etc");

    public string CertDir => Path.Combine(Root, "data", "cert");

    /// <summary>Writes <c>servers/&lt;name&gt;/config.json</c> the way Newtonsoft.Json wrote it (PascalCase).</summary>
    public string AddServer(string name, string url, bool encrypt = true, bool sign = true, bool requestMdn = true, bool asyncMdn = false,
        string securityProvider = "bcc", params (string IdentityAs2Id, string PartnerAs2Id, string PartnerName, bool Enabled)[] routings)
    {
        var directory = Path.Combine(Etc, "servers", name);
        Directory.CreateDirectory(directory);
        var config = new
        {
            Name = name,
            Encrypt = encrypt,
            Sign = sign,
            RequireSigned = true,
            RequireEncrypted = false,
            ReceiverURL = url,
            RequestMDN = requestMdn,
            AsyncMDN = asyncMdn,
            RequestSignedMDN = true,
            SendDelay = 0,
            SecurityProvider = securityProvider,
            Contacts = new[] { new { Org = "Acme", Name = "Bob", Mail = "bob@acme.example" } },
            Routings = routings.Select(r => new
            {
                IdentityName = "Artipa " + r.IdentityAs2Id,
                IdentityAS2ID = r.IdentityAs2Id,
                PartnerName = r.PartnerName,
                PartnerAS2ID = r.PartnerAs2Id,
                r.Enabled,
            }).ToArray(),
        };
        // With a BOM, as an editor on Windows may leave it: the reader has to take it.
        File.WriteAllText(Path.Combine(directory, "config.json"), "﻿" + JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return directory;
    }

    /// <summary>Puts a certificate of the partner into <c>cert/&lt;yyyy-MM-dd HHmmss&gt;/</c>; returns its thumbprint.</summary>
    public string AddCertificate(string serverDirectory, DateTime usedFromUtc, string subject, int validDays = 730)
    {
        var directory = Path.Combine(serverDirectory, "cert", usedFromUtc.ToString("yyyy-MM-dd HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        using var certificate = CreateCertificate(subject, validDays);
        File.WriteAllBytes(Path.Combine(directory, subject + ".cer"), certificate.Export(X509ContentType.Cert));
        return certificate.Thumbprint;
    }

    /// <summary>Our PKCS#12 in the old data/cert directory.</summary>
    public string AddPkcs12(string fileName, string password, string subject)
    {
        Directory.CreateDirectory(CertDir);
        using var certificate = CreateCertificate(subject, 730);
        File.WriteAllBytes(Path.Combine(CertDir, fileName), certificate.Export(X509ContentType.Pkcs12, password));
        return certificate.Thumbprint;
    }

    public static LegacySettings Settings(params (string Name, string File, string Password, string? Encryption)[] providers) => new()
    {
        SecurityProviders = providers.Select(p => new LegacySecurityProvider
        {
            Name = p.Name,
            CertFileName = p.File,
            CertPassword = p.Password,
            EncryptionAlgorithm = p.Encryption,
        }).ToArray(),
    };

    private static X509Certificate2 CreateCertificate(string subject, int validDays)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(validDays));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

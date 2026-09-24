using System.Text.Json.Serialization;

namespace AS24Net.Import.Legacy;

/// <summary>
/// A remote AS2 server of the old AS2 server (underware.AS2): <c>servers/&lt;name&gt;/config.json</c> with the
/// certificates of the partner in <c>servers/&lt;name&gt;/cert/&lt;yyyy-MM-dd HHmmss&gt;/</c>. Its routings are the
/// pairs of our identity and a partner's AS2 name that use it; partners differing only in the AS2 name share it.
/// </summary>
public sealed class LegacyServer
{
    public string Name { get; set; } = "";
    public bool Encrypt { get; set; }
    public bool Sign { get; set; }
    public bool RequireSigned { get; set; }
    public bool RequireEncrypted { get; set; }
    public string ReceiverURL { get; set; } = "";
    public bool RequestMDN { get; set; }
    public bool AsyncMDN { get; set; }
    public bool RequestSignedMDN { get; set; }
    public int SendDelay { get; set; }

    /// <summary>Name of the security provider of the old settings: our certificate and the encryption algorithm.</summary>
    public string? SecurityProvider { get; set; }

    public LegacyContact[]? Contacts { get; set; }
    public LegacyRouting[]? Routings { get; set; }

    /// <summary>The directory of the server.</summary>
    [JsonIgnore]
    public string Directory { get; set; } = "";

    /// <summary>The certificates of the partner, the oldest first.</summary>
    [JsonIgnore]
    public List<LegacyCertificate> Certificates { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class LegacyRouting
{
    public string IdentityName { get; set; } = "";
    public string IdentityAS2ID { get; set; } = "";
    public string PartnerName { get; set; } = "";
    public string PartnerAS2ID { get; set; } = "";
    public bool Enabled { get; set; }
}

public sealed class LegacyContact
{
    public string? Org { get; set; }
    public string? Name { get; set; }
    public string? Mail { get; set; }
}

/// <summary>A certificate of the partner, used from <see cref="UsedFromUtc"/> (the name of its directory).</summary>
public sealed record LegacyCertificate(DateTime UsedFromUtc, string FilePath, CertificateFile File);

/// <summary>A certificate file read and checked: DER of a public certificate, or PKCS#12 with its password.</summary>
public sealed record CertificateFile(
    string FileName,
    byte[] Data,
    string? Password,
    string Subject,
    string Thumbprint,
    DateTime NotBefore,
    DateTime NotAfter,
    bool HasPrivateKey);

/// <summary>The old <c>appsettings.json</c>, section <c>Settings</c>: only what the import needs.</summary>
public sealed class LegacySettings
{
    public LegacySecurityProvider[]? SecurityProviders { get; set; }
}

public sealed class LegacySecurityProvider
{
    public string Name { get; set; } = "";
    public string? CertPassword { get; set; }

    /// <summary>Our PKCS#12 with the private key, in the <c>cert</c> directory of the old data directory.</summary>
    public string? CertFileName { get; set; }

    public string? TypeName { get; set; }

    /// <summary>3DES (the default), AES-256 or RC2.</summary>
    public string? EncryptionAlgorithm { get; set; }
}

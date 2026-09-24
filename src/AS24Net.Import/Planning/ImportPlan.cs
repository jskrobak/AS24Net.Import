using AS24Net.Import.Legacy;

namespace AS24Net.Import.Planning;

/// <summary>What the import creates or updates in AS24Net.</summary>
public sealed class ImportPlan
{
    public List<PlannedIdentity> Identities { get; } = [];
    public List<PlannedConnection> Connections { get; } = [];
    public List<PlannedPartner> Partners { get; } = [];

    /// <summary>What the import cannot take over as it was, or should be looked at.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>Our AS2 station; its certificate (with the private key) signs and decrypts.</summary>
public sealed record PlannedIdentity(string As2Id, string Name, CertificateFile? Certificate);

/// <summary>A remote AS2 server with the settings agreed with it; the partners of <see cref="Sources"/> share it.</summary>
public sealed record PlannedConnection
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? Description { get; init; }
    public bool SignMessages { get; init; }
    public required string SignatureAlgorithm { get; init; }
    public bool EncryptMessages { get; init; }
    public required string EncryptionAlgorithm { get; init; }
    public required string MdnMode { get; init; }
    public bool RequestSignedMdn { get; init; }
    public bool RequireSignedMessages { get; init; }
    public bool RequireEncryptedMessages { get; init; }
    public string? ContactName { get; init; }
    public string? ContactEmail { get; init; }

    /// <summary>The partner's certificate in use now, for signature verification and encryption.</summary>
    public CertificateFile? Certificate { get; init; }

    /// <summary>Certificates the partner starts using later, scheduled as certificate changes.</summary>
    public IReadOnlyList<PlannedCertificateChange> Changes { get; init; } = [];

    /// <summary>The directories of the old servers the connection is made of.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];
}

public sealed record PlannedCertificateChange(CertificateFile Certificate, DateTime ActivateAtUtc);

public sealed record PlannedPartner(string As2Id, string Name, string Connection, bool Enabled, string? DefaultIdentity, string ContentType);

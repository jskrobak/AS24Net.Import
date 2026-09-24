using AS24Net.Import.Api;
using AS24Net.Import.Legacy;
using AS24Net.Import.Planning;

namespace AS24Net.Import;

/// <summary>
/// Carries the plan out through the REST API: certificates, identities, connections, partners, then the certificate
/// changes. Everything is created or updated by its name, so the import can run again; a certificate change that is
/// scheduled or applied already is not scheduled again.
/// </summary>
public sealed class ImportRunner(As24NetClient client, TextWriter output)
{
    public async Task RunAsync(ImportPlan plan, CancellationToken cancellationToken = default)
    {
        var certificates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        async Task<int?> StoreAsync(CertificateFile? file, string name)
        {
            if (file is null)
                return null;
            var key = file.Thumbprint + (file.HasPrivateKey ? "+key" : "");
            if (!certificates.TryGetValue(key, out var id))
            {
                id = (await client.AddCertificateAsync(file, name, cancellationToken)).Id;
                certificates[key] = id;
            }

            return id;
        }

        foreach (var identity in plan.Identities)
        {
            var certificateId = await StoreAsync(identity.Certificate, $"{identity.Name}: {identity.Certificate?.Subject}");
            await client.PutIdentityAsync(identity.As2Id, new
            {
                name = identity.Name,
                signingCertificateId = certificateId,
                decryptionCertificateId = certificateId,
            }, cancellationToken);
            output.WriteLine($"Identity   {identity.As2Id} ({identity.Name})" + (certificateId is null ? "" : $", certificate #{certificateId}"));
        }

        foreach (var connection in plan.Connections)
        {
            var certificateId = await StoreAsync(connection.Certificate, $"{connection.Name}: {connection.Certificate?.Subject}");
            await client.PutConnectionAsync(connection.Name, new
            {
                url = connection.Url,
                description = connection.Description,
                signMessages = connection.SignMessages,
                signatureAlgorithm = connection.SignatureAlgorithm,
                encryptMessages = connection.EncryptMessages,
                encryptionAlgorithm = connection.EncryptionAlgorithm,
                // The old server did not compress.
                compressMessages = false,
                mdnMode = connection.MdnMode,
                requestSignedMdn = connection.RequestSignedMdn,
                requireSignedMessages = connection.RequireSignedMessages,
                requireEncryptedMessages = connection.RequireEncryptedMessages,
                signatureCertificateId = certificateId,
                encryptionCertificateId = certificateId,
                contactName = connection.ContactName,
                contactEmail = connection.ContactEmail,
            }, cancellationToken);
            output.WriteLine($"Connection {connection.Name}" + (certificateId is null ? "" : $", certificate #{certificateId}"));
        }

        foreach (var partner in plan.Partners)
        {
            await client.PutPartnerAsync(partner.As2Id, new
            {
                name = partner.Name,
                connection = partner.Connection,
                enabled = partner.Enabled,
                // Without one identity of its own (null) the partner keeps what was set in AS24Net.
                defaultIdentity = partner.DefaultIdentity,
                contentType = partner.ContentType,
            }, cancellationToken);
            output.WriteLine($"Partner    {partner.As2Id} ({partner.Name}) -> {partner.Connection}" + (partner.Enabled ? "" : ", disabled"));
        }

        foreach (var connection in plan.Connections.Where(c => c.Changes.Count > 0))
        {
            var existing = await client.GetCertificateChangesAsync(connection.Name, cancellationToken);
            foreach (var change in connection.Changes)
            {
                if (existing.Any(e => e.Status is "Scheduled" or "Applied"
                                      && string.Equals(e.CertificateThumbprint, change.Certificate.Thumbprint, StringComparison.OrdinalIgnoreCase)))
                {
                    output.WriteLine($"Change     {connection.Name}: {change.Certificate.Subject} at {change.ActivateAtUtc:yyyy-MM-dd HH:mm} UTC is scheduled already");
                    continue;
                }

                await client.ScheduleCertificateAsync(connection.Name, change.Certificate, change.ActivateAtUtc,
                    "Imported from the old AS2 server", cancellationToken);
                output.WriteLine($"Change     {connection.Name}: {change.Certificate.Subject} at {change.ActivateAtUtc:yyyy-MM-dd HH:mm} UTC");
            }
        }
    }
}

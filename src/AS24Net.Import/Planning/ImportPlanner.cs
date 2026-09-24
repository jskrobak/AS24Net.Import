using System.Security.Cryptography;
using AS24Net.Import.Legacy;

namespace AS24Net.Import.Planning;

public sealed class PlannerOptions
{
    /// <summary>Only the servers with these names; all when empty.</summary>
    public IReadOnlyCollection<string> Servers { get; init; } = [];

    /// <summary>Servers with the same URL, settings and certificate become one connection.</summary>
    public bool MergeIdentical { get; init; }

    /// <summary>The old server signed with SHA-1 and asked for a SHA-1 MIC.</summary>
    public string SignatureAlgorithm { get; init; } = "sha1";

    /// <summary>The old server sent every payload as application/EDIFACT.</summary>
    public string ContentType { get; init; } = "application/edifact";

    /// <summary>Security providers of the old settings: our certificates and the encryption algorithms.</summary>
    public LegacySettings? Settings { get; init; }

    /// <summary>Directory with our PKCS#12 files named in the security providers (the old <c>data/cert</c>).</summary>
    public string? CertificateDirectory { get; init; }

    /// <summary>Now, in UTC: certificates used from a later time are scheduled.</summary>
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Turns the servers of the old AS2 server into the connections, partners and identities of AS24Net. A server is a
/// connection (its URL, security, MDN and certificates); each of its routings is a partner by its AS2 name, and an
/// identity by ours. Partners that shared a server share its connection.
/// </summary>
public static class ImportPlanner
{
    /// <summary>Longest name of a connection or a partner in AS24Net.</summary>
    public const int MaxNameLength = 50;

    public static ImportPlan Plan(IReadOnlyList<LegacyServer> allServers, PlannerOptions options)
    {
        var plan = new ImportPlan();
        var servers = Select(allServers, options, plan);

        var providers = (options.Settings?.SecurityProviders ?? [])
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Servers with the same settings become one connection, when asked to.
        var connections = new List<(PlannedConnection Connection, List<LegacyServer> Servers)>();
        foreach (var server in servers)
        {
            var connection = PlanConnection(server, options, providers, plan);
            if (connection is null)
                continue;

            var twin = connections.FirstOrDefault(c => SameSettings(c.Connection, connection));
            if (twin.Connection is not null && options.MergeIdentical)
            {
                twin.Servers.Add(server);
                var index = connections.FindIndex(c => ReferenceEquals(c.Connection, twin.Connection));
                connections[index] = (Merge(twin.Connection, connection), twin.Servers);
                continue;
            }

            if (twin.Connection is not null)
                plan.Warnings.Add($"Servers {twin.Servers[0].Name} and {server.Name} have the same URL, settings and certificate; " +
                                  "--merge-identical makes them one connection.");
            connections.Add((connection, [server]));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (connection, _) in connections)
        {
            if (!names.Add(connection.Name))
                plan.Warnings.Add($"Two connections are named {connection.Name}; the second one would overwrite the first.");
            plan.Connections.Add(connection);
        }

        var serverConnection = connections
            .SelectMany(c => c.Servers.Select(s => (Server: s, c.Connection.Name)))
            .ToDictionary(x => x.Server, x => x.Name);

        PlanPartners(servers.Where(serverConnection.ContainsKey).ToList(), serverConnection, options, plan);
        PlanIdentities(servers.Where(serverConnection.ContainsKey).ToList(), providers, options, plan);
        return plan;
    }

    private static List<LegacyServer> Select(IReadOnlyList<LegacyServer> servers, PlannerOptions options, ImportPlan plan)
    {
        if (options.Servers.Count == 0)
            return servers.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        foreach (var missing in options.Servers.Where(n => servers.All(s => !string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))))
            plan.Warnings.Add($"There is no server {missing}.");
        return servers
            .Where(s => options.Servers.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static PlannedConnection? PlanConnection(LegacyServer server, PlannerOptions options,
        Dictionary<string, LegacySecurityProvider> providers, ImportPlan plan)
    {
        if (server.Routings is not { Length: > 0 })
        {
            plan.Warnings.Add($"Server {server.Name} has no routing, so no partner; skipped.");
            return null;
        }

        if (!Uri.TryCreate(server.ReceiverURL?.Trim(), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            plan.Warnings.Add($"Server {server.Name}: '{server.ReceiverURL}' is not an http or https URL; skipped.");
            return null;
        }

        var current = server.Certificates.LastOrDefault(c => c.UsedFromUtc <= options.NowUtc);
        var later = server.Certificates.Where(c => c.UsedFromUtc > options.NowUtc).ToList();
        if (current is null && later.Count > 0)
        {
            // Nothing is in use yet: the first one is used right away, as the old server would not encrypt at all.
            current = later[0];
            later.RemoveAt(0);
        }

        if (current is null && (server.Encrypt || server.RequireSigned))
            plan.Warnings.Add($"Server {server.Name} has no certificate of the partner; encryption and requiring signed messages are switched off for its connection.");
        if (current is { } c && c.File.NotAfter < options.NowUtc)
            plan.Warnings.Add($"Server {server.Name}: the certificate {c.File.Subject} expired on {c.File.NotAfter:yyyy-MM-dd}.");
        if (server.SendDelay > 0)
            plan.Warnings.Add($"Server {server.Name}: the send delay of {server.SendDelay} s has no counterpart in AS24Net.");

        var contacts = (server.Contacts ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.Mail)).ToList();
        if (contacts.Count > 1)
            plan.Warnings.Add($"Server {server.Name} has {contacts.Count} contacts; the first one is taken, the others go to the description.");

        var name = Limit(server.Name, plan, "connection");
        return new PlannedConnection
        {
            Name = name,
            Url = url.ToString(),
            Description = contacts.Count > 1
                ? Truncate("Contacts: " + string.Join("; ", contacts.Skip(1).Select(DescribeContact)), 200)
                : null,
            SignMessages = server.Sign,
            SignatureAlgorithm = options.SignatureAlgorithm,
            EncryptMessages = server.Encrypt && current is not null,
            EncryptionAlgorithm = EncryptionAlgorithm(server, providers, plan),
            MdnMode = !server.RequestMDN ? "None" : server.AsyncMDN ? "Async" : "Sync",
            RequestSignedMdn = server.RequestMDN && server.RequestSignedMDN,
            RequireSignedMessages = server.RequireSigned && current is not null,
            RequireEncryptedMessages = server.RequireEncrypted,
            ContactName = contacts.FirstOrDefault() is { } first ? Truncate(DescribeName(first), 200) : null,
            ContactEmail = contacts.FirstOrDefault()?.Mail?.Trim() is { Length: > 0 } mail ? Truncate(mail, 200) : null,
            Certificate = current?.File,
            Changes = later.Select(x => new PlannedCertificateChange(x.File, x.UsedFromUtc)).ToList(),
            Sources = [server.Directory],
        };
    }

    /// <summary>The encryption of the server's security provider; the old server used 3DES unless it said otherwise.</summary>
    private static string EncryptionAlgorithm(LegacyServer server, Dictionary<string, LegacySecurityProvider> providers, ImportPlan plan)
    {
        string? algorithm;
        if (server.SecurityProvider is { } name && providers.TryGetValue(name, out var provider))
        {
            algorithm = provider.EncryptionAlgorithm;
        }
        else
        {
            // Without the old settings the name of the provider tells it, e.g. bcc-SHA256-AES256.
            algorithm = server.SecurityProvider?.Replace("-", "").Contains("AES256", StringComparison.OrdinalIgnoreCase) == true
                ? "AES-256"
                : null;
            if (providers.Count > 0 && server.SecurityProvider is not null)
                plan.Warnings.Add($"Server {server.Name}: the security provider {server.SecurityProvider} is not in the settings.");
        }

        switch (algorithm?.Replace("-", "").ToUpperInvariant())
        {
            case null or "" or "3DES":
                return "3des";
            case "AES256":
                return "aes256-cbc";
            default:
                plan.Warnings.Add($"Server {server.Name}: encryption {algorithm} is not supported by AS24Net; 3DES is used instead.");
                return "3des";
        }
    }

    private static bool SameSettings(PlannedConnection a, PlannedConnection b) =>
        string.Equals(a.Url.TrimEnd('/'), b.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && a.SignMessages == b.SignMessages
        && a.EncryptMessages == b.EncryptMessages
        && a.EncryptionAlgorithm == b.EncryptionAlgorithm
        && a.MdnMode == b.MdnMode
        && a.RequestSignedMdn == b.RequestSignedMdn
        && a.RequireSignedMessages == b.RequireSignedMessages
        && a.RequireEncryptedMessages == b.RequireEncryptedMessages
        && a.Certificate?.Thumbprint == b.Certificate?.Thumbprint;

    private static PlannedConnection Merge(PlannedConnection first, PlannedConnection other) => first with
    {
        ContactName = first.ContactName ?? other.ContactName,
        ContactEmail = first.ContactEmail ?? other.ContactEmail,
        Changes = first.Changes.Concat(other.Changes)
            .DistinctBy(c => (c.Certificate.Thumbprint, c.ActivateAtUtc))
            .OrderBy(c => c.ActivateAtUtc)
            .ToList(),
        Sources = [.. first.Sources, .. other.Sources],
    };

    private static void PlanPartners(List<LegacyServer> servers, Dictionary<LegacyServer, string> serverConnection, PlannerOptions options,
        ImportPlan plan)
    {
        var routings = servers
            .SelectMany(s => (s.Routings ?? []).Select(r => (Server: s, Routing: r)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Routing.PartnerAS2ID))
            .GroupBy(x => x.Routing.PartnerAS2ID.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in routings)
        {
            var as2Id = group.Key;
            var first = group.First();
            var otherServers = group.Select(x => x.Server).Distinct().Where(s => serverConnection[s] != serverConnection[first.Server]).ToList();
            if (otherServers.Count > 0)
                plan.Warnings.Add($"Partner {as2Id} is reached through servers {string.Join(", ", group.Select(x => x.Server.Name).Distinct())}; " +
                                  $"AS24Net has one connection per partner, {serverConnection[first.Server]} is taken.");

            var names = group.Select(x => x.Routing.PartnerName?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            if (names.Count > 1)
                plan.Warnings.Add($"Partner {as2Id} has the names {string.Join(", ", names)}; {names[0]} is taken.");

            var identities = group.Select(x => x.Routing.IdentityAS2ID?.Trim()).Where(i => !string.IsNullOrEmpty(i))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (identities.Count > 1)
                plan.Warnings.Add($"Partner {as2Id} exchanges messages with the identities {string.Join(", ", identities)}; " +
                                  "it gets no default identity, so a message to it has to name the identity.");

            plan.Partners.Add(new PlannedPartner(
                as2Id,
                Limit(names.FirstOrDefault() ?? as2Id, plan, "partner"),
                serverConnection[first.Server],
                group.Any(x => x.Routing.Enabled),
                identities.Count == 1 ? identities[0] : null,
                options.ContentType));
        }
    }

    private static void PlanIdentities(List<LegacyServer> servers, Dictionary<string, LegacySecurityProvider> providers,
        PlannerOptions options, ImportPlan plan)
    {
        var routings = servers
            .SelectMany(s => (s.Routings ?? []).Select(r => (Server: s, Routing: r)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Routing.IdentityAS2ID))
            .GroupBy(x => x.Routing.IdentityAS2ID.Trim(), StringComparer.OrdinalIgnoreCase);

        var readCertificates = new Dictionary<string, CertificateFile?>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in routings)
        {
            var name = group.Select(x => x.Routing.IdentityName?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? group.Key;
            plan.Identities.Add(new PlannedIdentity(group.Key, Limit(name, plan, "identity"),
                IdentityCertificate(group.Key, group.Select(x => x.Server).Distinct().ToList(), providers, options, plan, readCertificates)));
        }
    }

    /// <summary>
    /// Our certificate of the identity: the PKCS#12 of the security provider most of its servers use. Different
    /// providers often share the file (they differ in the algorithms only).
    /// </summary>
    private static CertificateFile? IdentityCertificate(string as2Id, List<LegacyServer> servers, Dictionary<string, LegacySecurityProvider> providers,
        PlannerOptions options, ImportPlan plan, Dictionary<string, CertificateFile?> readCertificates)
    {
        if (providers.Count == 0)
            return null;

        var files = servers
            .Select(s => s.SecurityProvider is { } p && providers.TryGetValue(p, out var provider) ? provider : null)
            .OfType<LegacySecurityProvider>()
            .Where(p => !string.IsNullOrWhiteSpace(p.CertFileName))
            .GroupBy(p => p.CertFileName!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();
        if (files.Count == 0)
        {
            plan.Warnings.Add($"Identity {as2Id}: no security provider of its servers names a certificate; set it in AS24Net.");
            return null;
        }

        if (files.Count > 1)
            plan.Warnings.Add($"Identity {as2Id} signs with {string.Join(", ", files.Select(f => f.Key))} on different servers; " +
                              $"{files[0].Key} is taken for all of them.");

        var provider = files[0].First();
        if (options.CertificateDirectory is null)
        {
            plan.Warnings.Add($"Identity {as2Id}: its certificate is {provider.CertFileName}; give --cert-dir to import it.");
            return null;
        }

        var path = Path.Combine(options.CertificateDirectory, provider.CertFileName!);
        if (readCertificates.TryGetValue(path, out var read))
            return read;

        CertificateFile? certificate = null;
        try
        {
            certificate = LegacyReader.ReadPkcs12(path, provider.CertPassword);
        }
        catch (Exception ex) when (ex is IOException or CryptographicException or ImportException or UnauthorizedAccessException)
        {
            plan.Warnings.Add($"Identity {as2Id}: {path} cannot be read ({ex.Message}); set its certificate in AS24Net.");
        }

        readCertificates[path] = certificate;
        return certificate;
    }

    private static string DescribeName(LegacyContact contact) =>
        string.IsNullOrWhiteSpace(contact.Org) ? contact.Name?.Trim() ?? "" : $"{contact.Name?.Trim()} ({contact.Org.Trim()})".Trim();

    private static string DescribeContact(LegacyContact contact) =>
        string.Join(" ", new[] { DescribeName(contact), contact.Mail?.Trim() }.Where(s => !string.IsNullOrEmpty(s)));

    private static string Limit(string name, ImportPlan plan, string what)
    {
        var trimmed = name.Trim();
        if (trimmed.Length <= MaxNameLength)
            return trimmed;
        var shortened = trimmed[..MaxNameLength].TrimEnd();
        plan.Warnings.Add($"The name of the {what} '{trimmed}' is longer than {MaxNameLength} characters; '{shortened}' is used.");
        return shortened;
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}

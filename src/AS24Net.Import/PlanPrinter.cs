using AS24Net.Import.Planning;

namespace AS24Net.Import;

/// <summary>Writes the plan in a form to check before it is carried out.</summary>
public static class PlanPrinter
{
    public static void Print(ImportPlan plan, TextWriter output)
    {
        output.WriteLine($"Identities ({plan.Identities.Count})");
        foreach (var identity in plan.Identities)
            output.WriteLine($"  {identity.As2Id,-30} {identity.Name}" +
                             (identity.Certificate is { } c ? $"  [{c.Subject}, valid to {c.NotAfter:yyyy-MM-dd}]" : "  [no certificate]"));

        output.WriteLine();
        output.WriteLine($"Connections ({plan.Connections.Count})");
        foreach (var connection in plan.Connections)
        {
            output.WriteLine($"  {connection.Name}");
            output.WriteLine($"    URL          {connection.Url}");
            output.WriteLine($"    Send         {Security(connection)}");
            output.WriteLine($"    MDN          {connection.MdnMode}{(connection.RequestSignedMdn ? ", signed" : "")}");
            output.WriteLine($"    Receive      {Requirements(connection)}");
            output.WriteLine($"    Certificate  " + (connection.Certificate is { } c
                ? $"{c.Subject}, valid to {c.NotAfter:yyyy-MM-dd}, {c.Thumbprint}"
                : "none"));
            foreach (var change in connection.Changes)
                output.WriteLine($"    From {change.ActivateAtUtc:yyyy-MM-dd HH:mm} UTC  {change.Certificate.Subject}, valid to {change.Certificate.NotAfter:yyyy-MM-dd}");
            if (connection.ContactName is not null || connection.ContactEmail is not null)
                output.WriteLine($"    Contact      {connection.ContactName} {connection.ContactEmail}".TrimEnd());
            var partners = plan.Partners.Where(p => p.Connection == connection.Name).Select(p => p.As2Id).ToList();
            output.WriteLine($"    Partners     {string.Join(", ", partners)}");
        }

        output.WriteLine();
        output.WriteLine($"Partners ({plan.Partners.Count})");
        foreach (var partner in plan.Partners)
            output.WriteLine($"  {partner.As2Id,-30} {partner.Name,-30} -> {partner.Connection}" +
                             $"{(partner.DefaultIdentity is { } identity ? $", from {identity}" : "")}{(partner.Enabled ? "" : ", disabled")}");

        if (plan.Warnings.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Warnings ({plan.Warnings.Count})");
            foreach (var warning in plan.Warnings)
                output.WriteLine($"  ! {warning}");
        }
    }

    private static string Security(PlannedConnection c)
    {
        var parts = new List<string>();
        if (c.SignMessages) parts.Add($"sign {c.SignatureAlgorithm}");
        if (c.EncryptMessages) parts.Add($"encrypt {c.EncryptionAlgorithm}");
        return parts.Count == 0 ? "unsecured" : string.Join(", ", parts);
    }

    private static string Requirements(PlannedConnection c)
    {
        var parts = new List<string>();
        if (c.RequireSignedMessages) parts.Add("must be signed");
        if (c.RequireEncryptedMessages) parts.Add("must be encrypted");
        return parts.Count == 0 ? "anything" : string.Join(", ", parts);
    }
}

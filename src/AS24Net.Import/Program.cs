using AS24Net.Import;
using AS24Net.Import.Api;
using AS24Net.Import.Legacy;
using AS24Net.Import.Planning;

ImportOptions options;
try
{
    options = ImportOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(ImportOptions.Usage);
    return 2;
}

if (options.Help)
{
    Console.WriteLine(ImportOptions.Usage);
    return 0;
}

try
{
    var readWarnings = new List<string>();
    var servers = LegacyReader.ReadServers(options.EtcDirectory!, readWarnings);
    var plan = ImportPlanner.Plan(servers, new PlannerOptions
    {
        Servers = options.Servers,
        MergeIdentical = options.MergeIdentical,
        SignatureAlgorithm = options.SignatureAlgorithm,
        ContentType = options.ContentType,
        Settings = options.SettingsFile is null ? null : LegacyReader.ReadSettings(options.SettingsFile),
        CertificateDirectory = options.CertificateDirectory,
    });
    plan.Warnings.InsertRange(0, readWarnings);

    Console.WriteLine($"{servers.Count} server(s) read from {Path.GetFullPath(options.EtcDirectory!)}");
    Console.WriteLine();
    PlanPrinter.Print(plan, Console.Out);

    if (options.DryRun)
    {
        Console.WriteLine();
        Console.WriteLine("Dry run: nothing was imported.");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"Importing into {options.Url}");
    await new ImportRunner(As24NetClient.Create(options.Url!, options.Token!), Console.Out).RunAsync(plan);
    Console.WriteLine("Done.");
    return 0;
}
catch (Exception ex) when (ex is ImportException or IOException or UnauthorizedAccessException or HttpRequestException
                               or TaskCanceledException or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"Import failed: {ex.Message}");
    return 1;
}

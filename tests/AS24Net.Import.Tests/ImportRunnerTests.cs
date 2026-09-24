using System.Net;
using System.Text;
using System.Text.Json;
using AS24Net.Import.Api;
using AS24Net.Import.Legacy;
using AS24Net.Import.Planning;

namespace AS24Net.Import.Tests;

public class ImportRunnerTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Answers like AS24Net and records the requests.</summary>
    private sealed class FakeAs24Net : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Requests { get; } = [];
        public List<object> ExistingChanges { get; } = [];
        public HttpStatusCode? FailWith { get; set; }
        private int _nextId = 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Add((request.Method.Method, path, body));

            if (FailWith is { } status)
                return Json(new { error = "Nope." }, status);

            return (request.Method.Method, path) switch
            {
                ("GET", var p) when p.EndsWith("/certificate-changes") => Json(ExistingChanges),
                ("POST", "/api/v1/certificates") => Json(new { id = _nextId++, name = "c", thumbprint = "T", validFrom = Now, validTo = Now, hasPrivateKey = false }),
                ("POST", _) => Json(new { id = _nextId++, connection = "acme", usage = "SignatureAndEncryption", activateAt = Now, status = "Scheduled" }),
                ("PUT", var p) when p.StartsWith("/api/v1/connections/") => Json(new { id = 1, name = p[20..], url = "https://x" }),
                ("PUT", var p) when p.StartsWith("/api/v1/partners/") => Json(new { id = 1, name = "p", as2Id = p[17..], connection = "acme", enabled = true }),
                ("PUT", var p) when p.StartsWith("/api/v1/identities/") => Json(new { id = 1, name = "i", as2Id = p[19..] }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private static ImportPlan Plan(LegacyFixture fixture, out string nextThumbprint)
    {
        var server = fixture.AddServer("acme", "https://as2.acme.example/as2",
            routings: [("ARTIPA", "ACME-PLANT1", "Acme Plant 1", true), ("ARTIPA", "ACME-PLANT2", "Acme Plant 2", true)]);
        fixture.AddCertificate(server, Now.AddYears(-1), "acme");
        nextThumbprint = fixture.AddCertificate(server, Now.AddDays(30), "acme-next");
        return ImportPlanner.Plan(LegacyReader.ReadServers(fixture.Etc, new List<string>()), new PlannerOptions { NowUtc = Now });
    }

    [Fact]
    public async Task CreatesIdentitiesConnectionsPartnersAndChangesInThisOrder()
    {
        using var fixture = new LegacyFixture();
        var plan = Plan(fixture, out _);
        var handler = new FakeAs24Net();

        await new ImportRunner(As24NetClient.Create("https://as2.example.com/", "a24_token", handler), TextWriter.Null).RunAsync(plan);

        Assert.Equal([
            "PUT /api/v1/identities/ARTIPA",
            "POST /api/v1/certificates",
            "PUT /api/v1/connections/acme",
            "PUT /api/v1/partners/ACME-PLANT1",
            "PUT /api/v1/partners/ACME-PLANT2",
            "GET /api/v1/connections/acme/certificate-changes",
            "POST /api/v1/connections/acme/certificate-changes",
        ], handler.Requests.Select(r => $"{r.Method} {r.Path}"));

        using var connection = JsonDocument.Parse(handler.Requests[2].Body);
        Assert.Equal("https://as2.acme.example/as2", connection.RootElement.GetProperty("url").GetString());
        Assert.Equal(1, connection.RootElement.GetProperty("signatureCertificateId").GetInt32());
        Assert.Equal(1, connection.RootElement.GetProperty("encryptionCertificateId").GetInt32());
        Assert.Equal("3des", connection.RootElement.GetProperty("encryptionAlgorithm").GetString());
        Assert.Equal("sha1", connection.RootElement.GetProperty("signatureAlgorithm").GetString());

        using var partner = JsonDocument.Parse(handler.Requests[3].Body);
        Assert.Equal("acme", partner.RootElement.GetProperty("connection").GetString());
        Assert.Equal("ARTIPA", partner.RootElement.GetProperty("defaultIdentity").GetString());

        Assert.Contains("activateAt", handler.Requests[6].Body);
        Assert.Contains(Now.AddDays(30).ToString("yyyy-MM-dd"), handler.Requests[6].Body);
        Assert.Contains("SignatureAndEncryption", handler.Requests[6].Body);
    }

    [Fact]
    public async Task AChangeScheduledAlreadyIsNotScheduledAgain()
    {
        using var fixture = new LegacyFixture();
        var plan = Plan(fixture, out var next);
        var handler = new FakeAs24Net();
        handler.ExistingChanges.Add(new { id = 7, connection = "acme", certificateThumbprint = next, usage = "SignatureAndEncryption",
            activateAt = Now.AddDays(30), status = "Scheduled" });

        await new ImportRunner(As24NetClient.Create("https://as2.example.com", "a24_token", handler), TextWriter.Null).RunAsync(plan);

        Assert.DoesNotContain(handler.Requests, r => r is { Method: "POST", Path: "/api/v1/connections/acme/certificate-changes" });
    }

    [Fact]
    public async Task ARefusalTellsWhatToDo()
    {
        using var fixture = new LegacyFixture();
        var plan = Plan(fixture, out _);
        var handler = new FakeAs24Net { FailWith = HttpStatusCode.Forbidden };

        var ex = await Assert.ThrowsAsync<ImportException>(() =>
            new ImportRunner(As24NetClient.Create("https://as2.example.com", "a24_token", handler), TextWriter.Null).RunAsync(plan));

        Assert.Contains("403", ex.Message);
        Assert.Contains("not allowed to change the configuration", ex.Message);
    }

    [Fact]
    public void ThePlanPrintsEveryConnectionAndPartner()
    {
        using var fixture = new LegacyFixture();
        var plan = Plan(fixture, out _);
        var output = new StringWriter();

        PlanPrinter.Print(plan, output);

        var text = output.ToString();
        Assert.Contains("https://as2.acme.example/as2", text);
        Assert.Contains("ACME-PLANT1, ACME-PLANT2", text);
        Assert.Contains("CN=acme-next", text);
    }
}

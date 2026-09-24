using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AS24Net.Import.Legacy;

namespace AS24Net.Import.Api;

/// <summary>
/// The REST API of AS24Net (<c>/api/v1</c>) as the import needs it. The token has to be allowed to change the
/// configuration (<i>Settings → API tokens</i>, <i>May change the configuration</i>).
/// </summary>
public sealed class As24NetClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static As24NetClient Create(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/api/v1/");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromMinutes(2);
        return new As24NetClient(http);
    }

    public Task<List<CertificateDto>> GetCertificatesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<CertificateDto>>("certificates", cancellationToken);

    /// <summary>Stores the certificate, or returns the one with the same thumbprint stored already.</summary>
    public Task<CertificateDto> AddCertificateAsync(CertificateFile file, string? name, CancellationToken cancellationToken = default) =>
        SendAsync<CertificateDto>(HttpMethod.Post, "certificates",
            new { fileName = file.FileName, data = Convert.ToBase64String(file.Data), password = file.Password, name }, cancellationToken);

    public Task<IdentityDto> PutIdentityAsync(string as2Id, object request, CancellationToken cancellationToken = default) =>
        SendAsync<IdentityDto>(HttpMethod.Put, "identities/" + Escape(as2Id), request, cancellationToken);

    public Task<ConnectionDto> PutConnectionAsync(string name, object request, CancellationToken cancellationToken = default) =>
        SendAsync<ConnectionDto>(HttpMethod.Put, "connections/" + Escape(name), request, cancellationToken);

    public Task<PartnerDto> PutPartnerAsync(string as2Id, object request, CancellationToken cancellationToken = default) =>
        SendAsync<PartnerDto>(HttpMethod.Put, "partners/" + Escape(as2Id), request, cancellationToken);

    public Task<List<CertificateChangeDto>> GetCertificateChangesAsync(string connection, CancellationToken cancellationToken = default) =>
        GetAsync<List<CertificateChangeDto>>($"connections/{Escape(connection)}/certificate-changes", cancellationToken);

    /// <summary>Schedules the certificate for the connection from the time (UTC) on, for signature and encryption.</summary>
    public async Task<CertificateChangeDto> ScheduleCertificateAsync(string connection, CertificateFile file, DateTime activateAtUtc,
        string? note, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(file.Data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pkix-cert");
        content.Add(fileContent, "file", file.FileName);
        content.Add(new StringContent("SignatureAndEncryption"), "usage");
        content.Add(new StringContent(activateAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), "activateAt");
        if (note is not null)
            content.Add(new StringContent(note), "note");

        using var response = await http.PostAsync($"connections/{Escape(connection)}/certificate-changes", content, cancellationToken);
        return await ReadAsync<CertificateChangeDto>(response, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken))!;

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var error = TryReadError(text) ?? (text.Length > 300 ? text[..300] : text);
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "The token is not valid.",
            HttpStatusCode.Forbidden => "The token is not allowed to change the configuration (Settings → API tokens).",
            HttpStatusCode.NotFound when response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/connections") == true
                => "Does the server run a version of AS24Net with connections?",
            _ => "",
        };
        var reason = string.Join(" ", new[] { error.Trim(), hint.Trim() }.Where(t => t.Length > 0));
        throw new ImportException(
            $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: HTTP {(int)response.StatusCode} {reason}".TrimEnd());
    }

    private static string? TryReadError(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<ApiError>(text, JsonOptions)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}

public sealed record ApiError(string? Error);

public sealed record CertificateDto(int Id, string Name, string? Thumbprint, DateTime ValidFrom, DateTime ValidTo, bool HasPrivateKey);

public sealed record IdentityDto(int Id, string Name, string As2Id);

public sealed record ConnectionDto(int Id, string Name, string Url, int? SignatureCertificateId, int? EncryptionCertificateId,
    IReadOnlyList<string>? PartnerAs2Ids);

public sealed record PartnerDto(int Id, string Name, string As2Id, string? Connection, bool Enabled);

public sealed record CertificateChangeDto(int Id, string? Connection, int? CertificateId, string? CertificateThumbprint, string Usage,
    DateTime ActivateAt, string Status);

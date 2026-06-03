using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;

namespace DevDashboard.Ado;

/// <summary>
/// HTTP client for Azure DevOps REST. Uses AzureCliCredential so callers
/// inherit the user's existing `az login` session — no PAT to manage.
/// </summary>
public sealed class AdoClient : IDisposable
{
    // Well-known Azure DevOps resource id — same across all tenants.
    private const string AdoResourceScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private AccessToken _cachedToken;

    public AdoClient(string organizationUrl, TokenCredential? credential = null)
    {
        var baseUrl = organizationUrl.TrimEnd('/') + "/";
        _credential = credential ?? new AzureCliCredential();
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task<JsonDocument> GetJsonAsync(string relativePath, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken.Token);
        return await SendAndParseAsync(request, relativePath, ct);
    }

    public async Task<JsonDocument> PostJsonAsync(string relativePath, string jsonBody, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, relativePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken.Token);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await SendAndParseAsync(request, relativePath, ct);
    }

    private async Task<JsonDocument> SendAndParseAsync(HttpRequestMessage request, string relativePath, CancellationToken ct)
    {
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"ADO request failed: {(int)response.StatusCode} {response.ReasonPhrase} on {relativePath}\n{body}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async ValueTask EnsureTokenAsync(CancellationToken ct)
    {
        // Five-minute safety margin so we never use a token about to expire mid-request.
        if (_cachedToken.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return;
        }

        var context = new TokenRequestContext(new[] { AdoResourceScope });
        _cachedToken = await _credential.GetTokenAsync(context, ct);
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

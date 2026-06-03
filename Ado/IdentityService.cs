using System.Text.Json;

namespace DevDashboard.Ado;

public sealed record AdoIdentity(string Id, string Descriptor, string DisplayName, string? Email);

public sealed class IdentityService
{
    private readonly AdoClient _client;
    private readonly string _vsspsBaseUrl;

    public IdentityService(AdoClient client, string organizationUrl)
    {
        _client = client;
        // The identity API lives on vssps.dev.azure.com, not dev.azure.com.
        var orgSlug = new Uri(organizationUrl).AbsolutePath.Trim('/');
        _vsspsBaseUrl = $"https://vssps.dev.azure.com/{orgSlug}";
    }

    public async Task<AdoIdentity> ResolveByDisplayNameAsync(string displayName, CancellationToken ct = default)
    {
        // ADO indexes identities by AccountName ("LastName FirstName" at Palfinger),
        // while providerDisplayName is "FirstName LastName". Try the input as given,
        // then try a reversed word order as a fallback.
        var attempts = new List<string> { displayName };
        var reversed = ReverseWords(displayName);
        if (reversed != displayName)
        {
            attempts.Add(reversed);
        }

        foreach (var attempt in attempts)
        {
            var identity = await TryResolveAsync(attempt, ct);
            if (identity != null)
            {
                return identity;
            }
        }

        throw new InvalidOperationException(
            $"No ADO identity found for '{displayName}' (tried '{string.Join("' and '", attempts)}')");
    }

    private async Task<AdoIdentity?> TryResolveAsync(string filterValue, CancellationToken ct)
    {
        var url = $"{_vsspsBaseUrl}/_apis/identities"
            + "?searchFilter=DisplayName"
            + $"&filterValue={Uri.EscapeDataString(filterValue)}"
            + "&api-version=7.1-preview.1";

        using var doc = await _client.GetJsonAsync(url, ct);
        var matches = doc.RootElement.GetProperty("value").EnumerateArray().ToList();

        if (matches.Count == 0)
        {
            return null;
        }
        if (matches.Count > 1)
        {
            throw new InvalidOperationException(
                $"Multiple identities matched '{filterValue}' — disambiguate in appsettings");
        }

        var match = matches[0];
        return new AdoIdentity(
            Id: match.GetProperty("id").GetString()!,
            Descriptor: match.GetProperty("descriptor").GetString()!,
            DisplayName: match.GetProperty("providerDisplayName").GetString() ?? filterValue,
            Email: TryGetEmail(match));
    }

    private static string ReverseWords(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? s : string.Join(' ', parts.Reverse());
    }

    private static string? TryGetEmail(JsonElement identity)
    {
        if (!identity.TryGetProperty("properties", out var props)) return null;
        if (!props.TryGetProperty("Mail", out var mail)) return null;
        if (!mail.TryGetProperty("$value", out var val)) return null;
        return val.GetString();
    }
}

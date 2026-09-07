using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YazmaBackup.Domain;

namespace YazmaBackup.ControlPlane;

public sealed record OidcLoginTransaction(string State, string Nonce, string CodeVerifier, DateTimeOffset CreatedAtUtc);
public sealed record OidcValidatedIdentity(string Issuer, string Subject, string Username, string DisplayName, IReadOnlyList<string> Roles);

public sealed class OidcSettings
{
    public string Issuer { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }
    public string RedirectUri { get; }
    public string DefaultRole { get; }
    public IReadOnlyDictionary<string, string> RoleMappings { get; }
    public bool AllowInsecureEndpoints { get; }
    public bool Enabled => !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(RedirectUri);

    public OidcSettings()
    {
        Issuer = (Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_ISSUER") ?? string.Empty).Trim().TrimEnd('/');
        ClientId = (Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_CLIENT_ID") ?? string.Empty).Trim();
        ClientSecret = Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_CLIENT_SECRET") ?? string.Empty;
        RedirectUri = (Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_REDIRECT_URI") ?? string.Empty).Trim();
        DefaultRole = NormalizeMappedRole(Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_DEFAULT_ROLE") ?? ManagementRoles.Viewer);
        AllowInsecureEndpoints = string.Equals(Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_ALLOW_INSECURE_ENDPOINTS"), "true", StringComparison.OrdinalIgnoreCase);
        RoleMappings = ParseRoleMappings(Environment.GetEnvironmentVariable("YAZMABACKUP_OIDC_ROLE_MAPPINGS"));
        if (Enabled)
        {
            if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuerUri)) throw new InvalidOperationException("YAZMABACKUP_OIDC_ISSUER must be an absolute URI.");
            if (!Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect)) throw new InvalidOperationException("YAZMABACKUP_OIDC_REDIRECT_URI must be an absolute URI.");
            EnsureSafeUri(issuerUri, AllowInsecureEndpoints);
            EnsureSafeUri(redirect, AllowInsecureEndpoints);
        }
    }

    private static Dictionary<string, string> ParseRoleMappings(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1) throw new InvalidOperationException("YAZMABACKUP_OIDC_ROLE_MAPPINGS must use external-value=yb-role entries separated by ';'.");
            var external = part[..separator].Trim();
            var role = NormalizeMappedRole(part[(separator + 1)..]);
            if (external.Length > 256) throw new InvalidOperationException("OIDC external role/group value is too long.");
            result[external] = role;
        }
        return result;
    }

    private static string NormalizeMappedRole(string value)
    {
        var role = value.Trim().ToLowerInvariant();
        if (!ManagementRoles.All.Contains(role) || string.Equals(role, ManagementRoles.Administrator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OIDC role mapping cannot grant an unknown role or the local administrator role.");
        return role;
    }

    internal static void EnsureSafeUri(Uri uri, bool allowInsecure)
    {
        if (!allowInsecure && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OIDC endpoints must use HTTPS unless YAZMABACKUP_OIDC_ALLOW_INSECURE_ENDPOINTS=true is explicitly set for lab use.");
    }
}

public sealed class OidcClient(HttpClient http, OidcSettings settings) : IDisposable
{
    private OidcDiscovery? _discovery;
    private DateTimeOffset _discoveryExpiresUtc;
    private OidcJwks? _jwks;
    private DateTimeOffset _jwksExpiresUtc;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public bool Enabled => settings.Enabled;

    public void Dispose() => _cacheGate.Dispose();

    public async Task<Uri> BuildAuthorizationUriAsync(OidcLoginTransaction transaction, CancellationToken ct)
    {
        var discovery = await GetDiscoveryAsync(ct).ConfigureAwait(false);
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(transaction.CodeVerifier)));
        var values = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["response_type"] = "code",
            ["scope"] = "openid profile email",
            ["redirect_uri"] = settings.RedirectUri,
            ["state"] = transaction.State,
            ["nonce"] = transaction.Nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        var query = string.Join("&", values.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        return new Uri(discovery.AuthorizationEndpoint + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?") + query);
    }

    public async Task<OidcValidatedIdentity> ExchangeAndValidateAsync(string code, OidcLoginTransaction transaction, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 8192) throw new UnauthorizedAccessException("OIDC authorization code is invalid.");
        if (DateTimeOffset.UtcNow - transaction.CreatedAtUtc > TimeSpan.FromMinutes(10)) throw new UnauthorizedAccessException("OIDC login transaction expired.");
        var discovery = await GetDiscoveryAsync(ct).ConfigureAwait(false);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = settings.ClientId,
            ["code"] = code,
            ["redirect_uri"] = settings.RedirectUri,
            ["code_verifier"] = transaction.CodeVerifier
        };
        if (!string.IsNullOrEmpty(settings.ClientSecret)) form["client_secret"] = settings.ClientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException("OIDC token exchange failed.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var tokenDocument = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, ct).ConfigureAwait(false);
        if (!tokenDocument.RootElement.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.ValueKind != JsonValueKind.String)
            throw new UnauthorizedAccessException("OIDC provider did not return an ID token.");
        var idToken = idTokenElement.GetString() ?? string.Empty;
        return await ValidateIdTokenAsync(idToken, transaction.Nonce, discovery, ct).ConfigureAwait(false);
    }

    private async Task<OidcValidatedIdentity> ValidateIdTokenAsync(string token, string expectedNonce, OidcDiscovery discovery, CancellationToken ct)
    {
        if (token.Length is < 64 or > 32768) throw new UnauthorizedAccessException("OIDC ID token length is invalid.");
        var parts = token.Split('.');
        if (parts.Length != 3) throw new UnauthorizedAccessException("OIDC ID token structure is invalid.");
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]), new JsonDocumentOptions { MaxDepth = 16 });
        var alg = GetRequiredString(header.RootElement, "alg", 32);
        if (!string.Equals(alg, "RS256", StringComparison.Ordinal)) throw new UnauthorizedAccessException("Only OIDC RS256 ID tokens are accepted.");
        var kid = GetRequiredString(header.RootElement, "kid", 256);
        var jwks = await GetJwksAsync(discovery, ct).ConfigureAwait(false);
        var key = jwks.Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal) && string.Equals(k.Kty, "RSA", StringComparison.Ordinal) && (string.IsNullOrEmpty(k.Use) || string.Equals(k.Use, "sig", StringComparison.Ordinal)) && (string.IsNullOrEmpty(k.Alg) || string.Equals(k.Alg, "RS256", StringComparison.Ordinal)));
        if (key is null) throw new UnauthorizedAccessException("OIDC signing key was not found or is not a signing RS256 RSA key.");
        var modulus = Base64UrlDecode(key.N);
        var exponent = Base64UrlDecode(key.E);
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters { Modulus = modulus, Exponent = exponent });
        if (rsa.KeySize < 2048) throw new UnauthorizedAccessException("OIDC signing key is weaker than RSA-2048.");
        var signedBytes = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = Base64UrlDecode(parts[2]);
        if (!rsa.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new UnauthorizedAccessException("OIDC ID token signature validation failed.");

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]), new JsonDocumentOptions { MaxDepth = 32 });
        var root = payload.RootElement;
        var issuer = GetRequiredString(root, "iss", 512).TrimEnd('/');
        if (!string.Equals(issuer, settings.Issuer, StringComparison.Ordinal)) throw new UnauthorizedAccessException("OIDC issuer validation failed.");
        var subject = GetRequiredString(root, "sub", 256);
        var nonce = GetRequiredString(root, "nonce", 512);
        if (!FixedTimeTextEquals(nonce, expectedNonce)) throw new UnauthorizedAccessException("OIDC nonce validation failed.");
        ValidateAudience(root);
        ValidateLifetime(root);

        var username = FirstNonEmptyString(root, "preferred_username", "email") ?? "oidc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + subject))).ToLowerInvariant()[..12];
        username = NormalizeUsernameForProvisioning(username);
        var displayName = FirstNonEmptyString(root, "name", "preferred_username", "email") ?? username;
        if (displayName.Length > 128) displayName = displayName[..128];
        var roles = ResolveRoles(root);
        return new OidcValidatedIdentity(issuer, subject, username, displayName, roles);
    }

    private string[] ResolveRoles(JsonElement payload)
    {
        var externalValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddStringOrArray(payload, "groups", externalValues);
        AddStringOrArray(payload, "roles", externalValues);
        var roles = externalValues.Where(settings.RoleMappings.ContainsKey).Select(v => settings.RoleMappings[v]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (roles.Count == 0) roles.Add(settings.DefaultRole);
        roles.RemoveAll(r => string.Equals(r, ManagementRoles.Administrator, StringComparison.OrdinalIgnoreCase));
        return roles.OrderBy(r => r, StringComparer.Ordinal).ToArray();
    }

    private void ValidateAudience(JsonElement payload)
    {
        if (!payload.TryGetProperty("aud", out var aud)) throw new UnauthorizedAccessException("OIDC audience claim is missing.");
        var audiences = new List<string>();
        if (aud.ValueKind == JsonValueKind.String) audiences.Add(aud.GetString() ?? string.Empty);
        else if (aud.ValueKind == JsonValueKind.Array)
            audiences.AddRange(aud.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? string.Empty));
        else throw new UnauthorizedAccessException("OIDC audience claim is invalid.");
        if (!audiences.Contains(settings.ClientId, StringComparer.Ordinal)) throw new UnauthorizedAccessException("OIDC audience validation failed.");
        if (audiences.Count > 1)
        {
            var azp = GetRequiredString(payload, "azp", 512);
            if (!string.Equals(azp, settings.ClientId, StringComparison.Ordinal)) throw new UnauthorizedAccessException("OIDC authorized-party validation failed.");
        }
    }

    private static void ValidateLifetime(JsonElement payload)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var exp = GetRequiredInt64(payload, "exp");
        if (exp < now - 120) throw new UnauthorizedAccessException("OIDC ID token expired.");
        if (payload.TryGetProperty("nbf", out var nbfElement) && nbfElement.ValueKind == JsonValueKind.Number && nbfElement.TryGetInt64(out var nbf) && nbf > now + 120)
            throw new UnauthorizedAccessException("OIDC ID token is not yet valid.");
        if (payload.TryGetProperty("iat", out var iatElement) && iatElement.ValueKind == JsonValueKind.Number && iatElement.TryGetInt64(out var iat) && iat > now + 120)
            throw new UnauthorizedAccessException("OIDC ID token issue time is in the future.");
    }

    private async Task<OidcDiscovery> GetDiscoveryAsync(CancellationToken ct)
    {
        if (!settings.Enabled) throw new InvalidOperationException("OIDC is not configured.");
        if (_discovery is not null && _discoveryExpiresUtc > DateTimeOffset.UtcNow) return _discovery;
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_discovery is not null && _discoveryExpiresUtc > DateTimeOffset.UtcNow) return _discovery;
            var uri = new Uri(settings.Issuer + "/.well-known/openid-configuration");
            using var response = await http.GetAsync(uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, ct).ConfigureAwait(false);
            var issuer = GetRequiredString(doc.RootElement, "issuer", 512).TrimEnd('/');
            if (!string.Equals(issuer, settings.Issuer, StringComparison.Ordinal)) throw new InvalidOperationException("OIDC discovery issuer mismatch.");
            var authorization = new Uri(GetRequiredString(doc.RootElement, "authorization_endpoint", 2048), UriKind.Absolute);
            var token = new Uri(GetRequiredString(doc.RootElement, "token_endpoint", 2048), UriKind.Absolute);
            var jwks = new Uri(GetRequiredString(doc.RootElement, "jwks_uri", 2048), UriKind.Absolute);
            OidcSettings.EnsureSafeUri(authorization, settings.AllowInsecureEndpoints);
            OidcSettings.EnsureSafeUri(token, settings.AllowInsecureEndpoints);
            OidcSettings.EnsureSafeUri(jwks, settings.AllowInsecureEndpoints);
            _discovery = new OidcDiscovery(issuer, authorization.ToString(), token.ToString(), jwks.ToString());
            _discoveryExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            return _discovery;
        }
        finally { _cacheGate.Release(); }
    }

    private async Task<OidcJwks> GetJwksAsync(OidcDiscovery discovery, CancellationToken ct)
    {
        if (_jwks is not null && _jwksExpiresUtc > DateTimeOffset.UtcNow) return _jwks;
        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_jwks is not null && _jwksExpiresUtc > DateTimeOffset.UtcNow) return _jwks;
            using var response = await http.GetAsync(discovery.JwksUri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 32 }, ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("keys", out var keysElement) || keysElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("OIDC JWKS payload is invalid.");
            var keys = new List<OidcJwk>();
            foreach (var item in keysElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var kid = OptionalString(item, "kid", 256);
                var kty = OptionalString(item, "kty", 32);
                var n = OptionalString(item, "n", 8192);
                var e = OptionalString(item, "e", 64);
                if (string.IsNullOrWhiteSpace(kid) || string.IsNullOrWhiteSpace(kty) || string.IsNullOrWhiteSpace(n) || string.IsNullOrWhiteSpace(e)) continue;
                keys.Add(new OidcJwk(kid, kty, n, e, OptionalString(item, "use", 32), OptionalString(item, "alg", 32)));
            }
            if (keys.Count == 0) throw new InvalidOperationException("OIDC JWKS contains no usable keys.");
            _jwks = new OidcJwks(keys);
            _jwksExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(15);
            return _jwks;
        }
        finally { _cacheGate.Release(); }
    }

    private static string GetRequiredString(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) throw new UnauthorizedAccessException($"OIDC {property} claim is missing.");
        var text = value.GetString() ?? string.Empty;
        if (text.Length is < 1 || text.Length > maxLength) throw new UnauthorizedAccessException($"OIDC {property} claim is invalid.");
        return text;
    }

    private static string? OptionalString(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrEmpty(text) || text.Length > maxLength ? null : text;
    }

    private static long GetRequiredInt64(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result)) throw new UnauthorizedAccessException($"OIDC {property} claim is missing or invalid.");
        return result;
    }

    private static string? FirstNonEmptyString(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
        {
            var value = OptionalString(element, property, 512);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    private static void AddStringOrArray(JsonElement payload, string property, HashSet<string> values)
    {
        if (!payload.TryGetProperty(property, out var element)) return;
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 256) values.Add(value);
            return;
        }
        if (element.ValueKind != JsonValueKind.Array) return;
        foreach (var item in element.EnumerateArray().Take(256))
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 256) values.Add(value);
        }
    }

    private static string NormalizeUsernameForProvisioning(string input)
    {
        var value = input.Trim().ToLowerInvariant();
        var sanitized = new string(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        if (sanitized.Length < 3) sanitized = "oidc-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
        return sanitized.Length <= 64 ? sanitized : sanitized[..64];
    }

    private static bool FixedTimeTextEquals(string a, string b)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(a));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(b));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Base64UrlDecode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 16384) throw new UnauthorizedAccessException("OIDC base64url value is invalid.");
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", 0 => string.Empty, _ => throw new UnauthorizedAccessException("OIDC base64url value is invalid.") };
        try { return Convert.FromBase64String(normalized); }
        catch (FormatException ex) { throw new UnauthorizedAccessException("OIDC base64url decoding failed.", ex); }
    }

    private sealed record OidcDiscovery(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);
    private sealed record OidcJwks(IReadOnlyList<OidcJwk> Keys);
    private sealed record OidcJwk(string Kid, string Kty, string N, string E, string? Use, string? Alg);
}

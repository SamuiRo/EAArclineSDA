using System.Buffers.Text;
using System.Text.Json;

namespace Arcline.SteamGuard.Models;

/// <summary>Minimal JWT payload reader (no signature check; Steam validates tokens server-side).</summary>
public static class Jwt
{
    public static DateTimeOffset? GetExpiry(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = Base64Url.DecodeFromChars(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (FormatException) { }
        catch (JsonException) { }
        return null;
    }

    public static bool IsExpired(string? token, TimeSpan skew)
    {
        var exp = GetExpiry(token);
        return exp is null || DateTimeOffset.UtcNow + skew >= exp.Value;
    }
}

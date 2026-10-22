using Postfold.Core.Enums;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Postfold.Desktop.Models.Authentication;

/// <summary>Owner-supplied desktop registration settings. No registration is bundled.</summary>
public sealed record ProviderClientConfiguration(
    string? GoogleClientId = null,
    string? MicrosoftClientId = null,
    string? GooglePolicyDecisionReference = null,
    string? GoogleClientSecret = null
)
{
    public Failure Availability(
        string provider
    )
    {
        return provider switch
        {
            "Google" when string.IsNullOrWhiteSpace(GoogleClientId) || GoogleClientId.Length > 512 ||
                !GoogleClientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) ||
                GoogleClientId.Contains('\r') || GoogleClientId.Contains('\n') => Failure.Unavailable,
            "Google" when string.IsNullOrWhiteSpace(GooglePolicyDecisionReference) => Failure.PolicyBlocked,
            "Google" => Failure.None,
            "Microsoft" when Guid.TryParse(MicrosoftClientId, out Guid id) && id != Guid.Empty => Failure.None,
            "Microsoft" => Failure.Unavailable,
            _ => Failure.Unsupported
        };
    }

    public override string ToString()
    {
        return "[Provider client configuration]";
    }

    /// <summary>Reads only the explicitly selected configuration file; malformed or extra fields fail closed.</summary>
    public static ProviderClientConfiguration Load(
        string? path
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ProviderClientConfiguration();
        }
        try
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length > 16384)
            {
                return new ProviderClientConfiguration();
            }
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            };
            return JsonSerializer.Deserialize<ProviderClientConfiguration>(File.ReadAllText(path), options) ?? new ProviderClientConfiguration();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new ProviderClientConfiguration();
        }
    }
}

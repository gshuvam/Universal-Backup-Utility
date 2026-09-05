using System;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Supported cloud storage and transport providers.
/// </summary>
public enum CloudProvider
{
    GoogleDrive,
    OneDrive,
    CustomS3,
    LocalDrive
}

/// <summary>
/// State tracking an in-flight OAuth 2.0 PKCE authorization flow.
/// </summary>
public sealed record OAuthSessionState
{
    public required CloudProvider Provider { get; init; }
    public required string State { get; init; }
    public required string CodeVerifier { get; init; }
    public required string CodeChallenge { get; init; }
    public required string RedirectUri { get; init; }
    public required string AuthorizationUrl { get; init; }
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Token credentials returned from a successful OAuth 2.0 exchange.
/// </summary>
public sealed record OAuthTokenResponse
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string TokenType { get; init; } = "Bearer";
    public int ExpiresIn { get; init; }
    public DateTime ExpiresAtUtc { get; init; }
    public required string Scope { get; init; }
}

/// <summary>
/// Formal legal and technical licensing audit for the Ludusavi community game save manifest.
/// </summary>
public sealed record LudusaviLicenseAudit
{
    public required string RulesetName { get; init; }
    public required string RepositoryUrl { get; init; }
    public required string PrimaryLicense { get; init; }
    public required string SecondaryLicense { get; init; }
    public bool IsCommercialAllowed { get; init; }
    public bool IsModificationAllowed { get; init; }
    public bool IsDistributionAllowed { get; init; }
    public required string RequiredAttribution { get; init; }
    public required string RecommendedNotice { get; init; }
}

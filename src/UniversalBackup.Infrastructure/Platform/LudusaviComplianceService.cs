using System;
using System.Text.Json;
using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Verifies licensing compliance and schema validity for the Ludusavi community game save manifest.
/// </summary>
public sealed class LudusaviComplianceService : ILudusaviComplianceService
{
    public LudusaviLicenseAudit GetLicensingAudit()
    {
        return new LudusaviLicenseAudit
        {
            RulesetName = "Ludusavi Manifest",
            RepositoryUrl = "https://github.com/mtkennerly/ludusavi-manifest",
            PrimaryLicense = "CC0-1.0 (Creative Commons Zero v1.0 Universal)",
            SecondaryLicense = "MIT License",
            IsCommercialAllowed = true,
            IsModificationAllowed = true,
            IsDistributionAllowed = true,
            RequiredAttribution = "Ludusavi game save directory specifications (c) mtkennerly and contributors, CC0 1.0 / MIT.",
            RecommendedNotice = "Game save detection rules powered in part by the open-source Ludusavi community manifest (https://github.com/mtkennerly/ludusavi)."
        };
    }

    public bool ValidateRuleSchema(string manifestSample)
    {
        if (string.IsNullOrWhiteSpace(manifestSample))
        {
            return false;
        }

        try
        {
            // Ludusavi manifests are serialized as JSON or YAML maps where each key is a game title
            // containing files, registry, or steam entries.
            using var doc = JsonDocument.Parse(manifestSample);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Verify at least one game title with recognizable schema properties
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var val = prop.Value;
                    var hasFiles = val.TryGetProperty("files", out _);
                    var hasRegistry = val.TryGetProperty("registry", out _);
                    var hasSteam = val.TryGetProperty("steam", out _);
                    var hasInstallDir = val.TryGetProperty("installDir", out _);

                    if (hasFiles || hasRegistry || hasSteam || hasInstallDir)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

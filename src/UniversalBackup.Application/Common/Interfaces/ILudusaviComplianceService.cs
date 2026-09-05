using UniversalBackup.Domain.Models;

namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Compliance and schema validation service for community game-save manifest rulesets.
/// </summary>
public interface ILudusaviComplianceService
{
    /// <summary>
    /// Returns the formal legal audit regarding Ludusavi manifest licensing, permissions, and attribution.
    /// </summary>
    LudusaviLicenseAudit GetLicensingAudit();

    /// <summary>
    /// Validates that a manifest sample matches the required Ludusavi game save specification schema.
    /// </summary>
    bool ValidateRuleSchema(string manifestSample);
}


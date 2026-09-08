namespace UniversalBackup.Domain.Enums;

/// <summary>
/// Dictates whether a matched path/component is included or excluded from backup.
/// </summary>
public enum SelectionType
{
    Include,
    Exclude
}

/// <summary>
/// Precedence level for rule evaluation. Evaluated strictly from highest (0) to lowest (3).
/// Higher priority tiers override lower priority tiers.
/// </summary>
public enum SelectionPrecedence
{
    /// <summary>
    /// Mandatory safety exclusions (system files, /proc, /sys, recycle bin, staging, repo paths, traversal).
    /// CANNOT be overridden by any user inclusion rule.
    /// </summary>
    MandatorySafetyExclusion = 0,

    /// <summary>
    /// Explicit user check/uncheck override on a specific file, directory, or component.
    /// </summary>
    ExplicitUserOverride = 1,

    /// <summary>
    /// Selection inherited from the closest ancestor directory's explicit choice.
    /// </summary>
    ParentInherited = 2,

    /// <summary>
    /// Default selection policy defined by the active backup preset (e.g. Game Saves vs Full Installations).
    /// </summary>
    PresetDefault = 3
}

/// <summary>
/// Scope at which a selection rule operates.
/// </summary>
public enum SelectionScope
{
    PhysicalPath,
    LogicalComponent,
    Category,
    Global
}

/// <summary>
/// Policy for handling newly discovered files, games, or apps after a plan is created.
/// </summary>
public enum FutureMatchPolicy
{
    AutoInclude,
    PromptReview,
    IgnoreNew
}

/// <summary>
/// High-level backup presets available to the user.
/// </summary>
public enum BackupPreset
{
    PersonalEssentials,
    GameSavesOnly,
    GamesWithInstallations,
    ApplicationMigration,
    EntireAccessibleComputer,
    Custom
}

/// <summary>
/// Role of a snapshot within a dual-snapshot commit protocol.
/// </summary>
public enum SnapshotRole
{
    /// <summary>
    /// Main payload containing actual files, folders, and frozen backup set descriptor.
    /// </summary>
    Payload,

    /// <summary>
    /// Small control snapshot containing verifiable completion receipt referencing the BackupSetId.
    /// </summary>
    ReceiptControl,

    /// <summary>
    /// Alias for ReceiptControl.
    /// </summary>
    Control = ReceiptControl,

    /// <summary>
    /// Standalone snapshot created without dual-snapshot control protocol.
    /// </summary>
    Standalone
}

/// <summary>
/// Verification status of a stored snapshot replica.
/// </summary>
public enum SnapshotVerificationState
{
    Unverified,
    QuickVerified,
    FullReadVerified,
    Failed
}

/// <summary>
/// Physical or cloud location type of a repository.
/// </summary>
public enum RepositoryLocationType
{
    Local,
    NetworkShare,
    GoogleDrive,
    OneDrive
}


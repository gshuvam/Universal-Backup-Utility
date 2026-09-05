using UniversalBackup.Domain.Enums;

namespace UniversalBackup.Domain.Models;

/// <summary>
/// Represents an authoritative rule dictating whether a physical path, logical component,
/// or category is included or excluded from backup.
/// </summary>
public sealed record SelectionRule
{
    public string Id { get; init; }
    public string PathOrPattern { get; init; }
    public SelectionScope Scope { get; init; }
    public SelectionType Type { get; init; }
    public SelectionPrecedence Precedence { get; init; }
    public int Specificity { get; init; }
    public FutureMatchPolicy FutureMatchPolicy { get; init; }
    public string Reason { get; init; }
    public bool IsUserOverride => Precedence == SelectionPrecedence.ExplicitUserOverride;

    public SelectionRule(
        string id,
        string pathOrPattern,
        SelectionScope scope,
        SelectionType type,
        SelectionPrecedence precedence,
        int specificity,
        string reason,
        FutureMatchPolicy futureMatchPolicy = FutureMatchPolicy.AutoInclude)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrPattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        Id = id;
        PathOrPattern = pathOrPattern;
        Scope = scope;
        Type = type;
        Precedence = precedence;
        Specificity = specificity;
        Reason = reason;
        FutureMatchPolicy = futureMatchPolicy;
    }

    /// <summary>
    /// Creates an explicit user override rule.
    /// </summary>
    public static SelectionRule CreateUserOverride(
        string pathOrPattern,
        SelectionType type,
        string reason = "Explicit user selection",
        SelectionScope scope = SelectionScope.PhysicalPath)
    {
        int specificity = CalculateSpecificity(pathOrPattern);
        return new SelectionRule(
            id: Guid.NewGuid().ToString("N"),
            pathOrPattern: pathOrPattern,
            scope: scope,
            type: type,
            precedence: SelectionPrecedence.ExplicitUserOverride,
            specificity: specificity,
            reason: reason);
    }

    /// <summary>
    /// Creates a mandatory safety exclusion rule that cannot be overridden by user inclusions.
    /// </summary>
    public static SelectionRule CreateSafetyExclusion(
        string pathOrPattern,
        string reason = "System safety exclusion")
    {
        int specificity = CalculateSpecificity(pathOrPattern);
        return new SelectionRule(
            id: Guid.NewGuid().ToString("N"),
            pathOrPattern: pathOrPattern,
            scope: SelectionScope.PhysicalPath,
            type: SelectionType.Exclude,
            precedence: SelectionPrecedence.MandatorySafetyExclusion,
            specificity: specificity + 1000, // Higher specificity boost for mandatory safety
            reason: reason);
    }

    /// <summary>
    /// Creates a preset default rule.
    /// </summary>
    public static SelectionRule CreatePresetDefault(
        string pathOrPattern,
        SelectionType type,
        string reason,
        SelectionScope scope = SelectionScope.LogicalComponent)
    {
        int specificity = CalculateSpecificity(pathOrPattern);
        return new SelectionRule(
            id: Guid.NewGuid().ToString("N"),
            pathOrPattern: pathOrPattern,
            scope: scope,
            type: type,
            precedence: SelectionPrecedence.PresetDefault,
            specificity: specificity,
            reason: reason);
    }

    public static int CalculateSpecificity(string pathOrPattern)
    {
        if (string.IsNullOrWhiteSpace(pathOrPattern)) return 0;

        char[] separators = ['/', '\\'];
        string[] parts = pathOrPattern.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        // More path segments mean higher specificity
        int score = parts.Length * 10;

        // Exact extensions or filenames add specificity
        if (Path.HasExtension(pathOrPattern))
        {
            score += 5;
        }

        return score;
    }

    public override string ToString() => $"[{Precedence}] {Type} '{PathOrPattern}' (Specificity: {Specificity}) - {Reason}";
}


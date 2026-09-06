using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalBackup.Desktop.Models;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Desktop.ViewModels;

public sealed record ComponentDetailItem(string Name, string Type, string FormattedSize, string Consistency);

/// <summary>
/// View model managing the Right Contextual Details Pane on the Backup Selection view.
/// Shows detection confidence, physical path, component breakdown, exclusions, and inclusion rationale.
/// </summary>
public partial class ItemDetailsViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _nodeType = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _formattedSize = "0 B";

    [ObservableProperty]
    private string _primaryPath = string.Empty;

    [ObservableProperty]
    private string _providerId = string.Empty;

    [ObservableProperty]
    private string _confidenceText = string.Empty;

    [ObservableProperty]
    private string _consistencyText = string.Empty;

    [ObservableProperty]
    private string _portabilityText = string.Empty;

    [ObservableProperty]
    private string _inclusionReason = string.Empty;

    [ObservableProperty]
    private string _exclusionReason = string.Empty;

    [ObservableProperty]
    private bool _hasExclusion;

    [ObservableProperty]
    private ObservableCollection<ComponentDetailItem> _components = [];

    public void PopulateFromNode(TreeNodeItem? node)
    {
        if (node == null)
        {
            Clear();
            return;
        }

        HasSelection = true;
        Title = node.Name;
        NodeType = node.NodeType;
        Category = node.Category ?? "General";
        FormattedSize = node.FormattedSize;
        PrimaryPath = node.Path;
        ProviderId = node.ProviderId ?? (node.DiscoveredItem?.ProviderId ?? "Catalog");

        ConfidenceText = node.Confidence.HasValue
            ? FormatConfidence(node.Confidence.Value)
            : "Direct Filesystem";

        ConsistencyText = node.Consistency.HasValue
            ? FormatConsistency(node.Consistency.Value)
            : "Filesystem Snapshot (Default)";

        PortabilityText = node.Portability.HasValue
            ? FormatPortability(node.Portability.Value)
            : "Standard Local";

        InclusionReason = !string.IsNullOrWhiteSpace(node.InclusionReason)
            ? node.InclusionReason
            : "Discovered as part of standard filesystem and provider inventory scan.";

        if (!string.IsNullOrWhiteSpace(node.ExclusionReason))
        {
            HasExclusion = true;
            ExclusionReason = node.ExclusionReason;
        }
        else
        {
            HasExclusion = false;
            ExclusionReason = string.Empty;
        }

        Components.Clear();
        if (node.DiscoveredItem?.Components.Count > 0)
        {
            foreach (var comp in node.DiscoveredItem.Components)
            {
                string sizeStr = CategoryCardModel.FormatBytes(comp.EstimatedSizeBytes ?? 0);
                Components.Add(new ComponentDetailItem(
                    comp.DisplayName,
                    comp.Type.ToString(),
                    sizeStr,
                    comp.Consistency.ToString()));
            }
        }
        else if (node.LogicalComponent != null)
        {
            var comp = node.LogicalComponent;
            string sizeStr = CategoryCardModel.FormatBytes(comp.EstimatedSizeBytes ?? 0);
            Components.Add(new ComponentDetailItem(
                comp.DisplayName,
                comp.Type.ToString(),
                sizeStr,
                comp.Consistency.ToString()));
        }
    }

    public void Clear()
    {
        HasSelection = false;
        Title = string.Empty;
        NodeType = string.Empty;
        Category = string.Empty;
        FormattedSize = "0 B";
        PrimaryPath = string.Empty;
        ProviderId = string.Empty;
        ConfidenceText = string.Empty;
        ConsistencyText = string.Empty;
        PortabilityText = string.Empty;
        InclusionReason = string.Empty;
        ExclusionReason = string.Empty;
        HasExclusion = false;
        Components.Clear();
    }

    private static string FormatConfidence(DiscoveryConfidence confidence) => confidence switch
    {
        DiscoveryConfidence.ProviderConfirmed => "Provider Confirmed (Highest)",
        DiscoveryConfidence.KnownRecipe => "Known Recipe / Rule Match (Verified)",
        DiscoveryConfidence.Heuristic => "Heuristic Analysis",
        DiscoveryConfidence.UserDefined => "User Defined",
        _ => confidence.ToString()
    };

    private static string FormatConsistency(ConsistencyClass consistency) => consistency switch
    {
        ConsistencyClass.ApplicationConsistent => "Application-Consistent (App paused/notified)",
        ConsistencyClass.FilesystemSnapshot => "Filesystem Snapshot (VSS / locked-file safe)",
        ConsistencyClass.LiveBestEffort => "Live Best Effort (Active file stream)",
        ConsistencyClass.Uncaptured => "Uncaptured (Skipped)",
        _ => consistency.ToString()
    };

    private static string FormatPortability(ComponentPortability portability) => portability switch
    {
        ComponentPortability.CrossPlatform => "Cross-Platform Compatible",
        ComponentPortability.WindowsOnly => "Windows Platform Specific",
        ComponentPortability.LinuxOnly => "Linux Platform Specific",
        ComponentPortability.MachineBound => "Machine / Hardware Bound",
        _ => portability.ToString()
    };
}

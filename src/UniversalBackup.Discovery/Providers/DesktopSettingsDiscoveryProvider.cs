using UniversalBackup.Application.Common.Interfaces;
using UniversalBackup.Application.DTOs;
using UniversalBackup.Domain.Enums;
using UniversalBackup.Domain.Models;

namespace UniversalBackup.Discovery.Providers;

/// <summary>
/// Discovers desktop application settings, developer configurations, shell profiles,
/// Git configurations, SSH client state, and personalization preferences.
/// </summary>
public sealed class DesktopSettingsDiscoveryProvider : IDiscoveryProvider
{
    public string ProviderId => "desktop-settings";
    public string DisplayName => "Desktop Application Settings & Developer Configs";

    public record SettingsTarget(
        string TargetKey,
        string DisplayName,
        string ComponentName,
        string Path,
        LogicalComponentType ComponentType,
        ComponentPortability Portability = ComponentPortability.CrossPlatform);

    private readonly IReadOnlyList<SettingsTarget>? _customTargets;

    public DesktopSettingsDiscoveryProvider(IEnumerable<SettingsTarget>? customTargets = null)
    {
        _customTargets = customTargets?.ToList();
    }

    public Task<IReadOnlyList<DiscoveredItem>> DiscoverAsync(DiscoveryContext context, CancellationToken ct = default)
    {
        var items = new List<DiscoveredItem>();
        var targets = _customTargets ?? ResolveTargets(context);

        var grouped = targets.GroupBy(t => t.TargetKey, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            ct.ThrowIfCancellationRequested();

            var validComponents = new List<LogicalComponent>();
            var evidence = new List<string>();
            string title = group.First().DisplayName;

            int compIdx = 1;
            foreach (var target in group)
            {
                if (Directory.Exists(target.Path) || File.Exists(target.Path))
                {
                    string fullPath = Path.GetFullPath(target.Path);
                    evidence.Add(fullPath);

                    string? volumeGuid = context.Volumes.FirstOrDefault(v =>
                        fullPath.StartsWith(v.MountPath, StringComparison.OrdinalIgnoreCase))?.VolumeGuid;

                    var sourceRoot = SourceRoot.Create(
                        path: fullPath,
                        consistencyClass: ConsistencyClass.FilesystemSnapshot,
                        volumeGuid: volumeGuid);

                    validComponents.Add(new LogicalComponent(
                        id: $"setting:{group.Key}:{compIdx++}",
                        discoveredItemId: $"setting:{group.Key}",
                        type: target.ComponentType,
                        displayName: target.ComponentName,
                        sourceRoots: [sourceRoot],
                        portability: target.Portability,
                        consistency: ConsistencyClass.FilesystemSnapshot));
                }
            }

            if (validComponents.Count > 0)
            {
                items.Add(new DiscoveredItem(
                    id: $"setting:{group.Key}",
                    providerId: ProviderId,
                    title: title,
                    confidence: DiscoveryConfidence.ProviderConfirmed,
                    evidence: evidence,
                    components: validComponents,
                    category: "Settings to back up"));
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredItem>>(items);
    }

    private static IReadOnlyList<SettingsTarget> ResolveTargets(DiscoveryContext context)
    {
        var list = new List<SettingsTarget>();

        context.KnownFolders.TryGetValue("AppDataRoaming", out var roaming);
        context.KnownFolders.TryGetValue("AppDataLocal", out var local);
        context.KnownFolders.TryGetValue("Documents", out var documents);
        context.KnownFolders.TryGetValue("UserProfile", out var userProfile);

        // 1. Visual Studio Code
        if (!string.IsNullOrWhiteSpace(roaming))
        {
            list.Add(new SettingsTarget("vscode", "Visual Studio Code Settings", "User Settings & Keybindings",
                Path.Combine(roaming, "Code", "User"), LogicalComponentType.Configuration));
        }
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(new SettingsTarget("vscode", "Visual Studio Code Settings", "User Settings (Linux)",
                Path.Combine(userProfile, ".config", "Code", "User"), LogicalComponentType.Configuration));
        }

        // 2. Windows Terminal
        if (!string.IsNullOrWhiteSpace(local))
        {
            list.Add(new SettingsTarget("terminal", "Windows Terminal Settings", "Packaged Terminal Settings",
                Path.Combine(local, "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "settings.json"),
                LogicalComponentType.Configuration, ComponentPortability.WindowsOnly));

            list.Add(new SettingsTarget("terminal", "Windows Terminal Settings", "Unpackaged Terminal Settings",
                Path.Combine(local, "Microsoft", "Windows Terminal", "settings.json"),
                LogicalComponentType.Configuration, ComponentPortability.WindowsOnly));
        }

        // 3. PowerShell Profiles
        if (!string.IsNullOrWhiteSpace(documents))
        {
            list.Add(new SettingsTarget("powershell", "PowerShell Profiles & Modules", "PowerShell 7 Profile Folder",
                Path.Combine(documents, "PowerShell"), LogicalComponentType.Configuration));

            list.Add(new SettingsTarget("powershell", "PowerShell Profiles & Modules", "Windows PowerShell 5.1 Profile Folder",
                Path.Combine(documents, "WindowsPowerShell"), LogicalComponentType.Configuration, ComponentPortability.WindowsOnly));
        }
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(new SettingsTarget("powershell", "PowerShell Profiles & Modules", "Linux PowerShell Profile",
                Path.Combine(userProfile, ".config", "powershell"), LogicalComponentType.Configuration, ComponentPortability.LinuxOnly));
        }

        // 4. Git Global Configuration
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(new SettingsTarget("git", "Git Global Configuration", ".gitconfig File",
                Path.Combine(userProfile, ".gitconfig"), LogicalComponentType.Configuration));

            list.Add(new SettingsTarget("git", "Git Global Configuration", "Git Config Directory",
                Path.Combine(userProfile, ".config", "git"), LogicalComponentType.Configuration));
        }

        // 5. SSH Configuration & Keys
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(new SettingsTarget("ssh", "SSH User Keys & Config", "SSH Directory (~/.ssh)",
                Path.Combine(userProfile, ".ssh"), LogicalComponentType.SystemSettings));
        }

        return list;
    }
}


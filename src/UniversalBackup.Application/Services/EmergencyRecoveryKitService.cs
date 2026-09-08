using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Application.Services;

/// <summary>
/// Service responsible for generating the Emergency Recovery Kit in Markdown and HTML formats.
/// Provides exact copy-pasteable, standalone restic CLI commands to enable 100% offline
/// disaster recovery on clean machines without requiring the Universal Backup application.
/// </summary>
public sealed class EmergencyRecoveryKitService : IEmergencyRecoveryKitService
{
    public Task<string> GenerateRecoveryKitMarkdownAsync(EmergencyRecoveryKitOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var repo = string.IsNullOrWhiteSpace(options.RepositoryPath) ? "<REPOSITORY_PATH_OR_URI>" : options.RepositoryPath;
        var plan = string.IsNullOrWhiteSpace(options.PlanName) ? "Primary Backup Plan" : options.PlanName;
        var machine = string.IsNullOrWhiteSpace(options.MachineName) ? Environment.MachineName : options.MachineName;
        var user = string.IsNullOrWhiteSpace(options.UserName) ? Environment.UserName : options.UserName;
        var payloadId = string.IsNullOrWhiteSpace(options.LatestPayloadSnapshotId) ? "latest" : options.LatestPayloadSnapshotId;
        var receiptId = string.IsNullOrWhiteSpace(options.LatestReceiptSnapshotId) ? "<RECEIPT_SNAPSHOT_ID>" : options.LatestReceiptSnapshotId;
        var now = DateTimeOffset.UtcNow;

        var sb = new StringBuilder();
        sb.AppendLine("# Universal Backup — Emergency Recovery Kit");
        sb.AppendLine("### Complete Disaster Recovery Guide (Zero Application Dependencies)");
        sb.AppendLine();
        sb.AppendLine("> **GUARANTEED CATALOG INDEPENDENCE (ADR-004)**: Universal Backup creates standard, encrypted [Restic](https://restic.net) repositories.");
        sb.AppendLine("> In a complete disaster scenario (disk failure, clean OS installation, hardware replacement), you do **NOT** need");
        sb.AppendLine("> the Universal Backup application, the .NET runtime, or the original SQLite database to recover your files.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 1. Protected System & Repository Profile");
        sb.AppendLine();
        sb.AppendLine("| Parameter | Value |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Repository Location** | `{repo}` |");
        sb.AppendLine($"| **Protected Hostname** | `{machine}` |");
        sb.AppendLine($"| **Protected User Profile** | `{user}` |");
        sb.AppendLine($"| **Plan Name** | `{plan}` |");
        sb.AppendLine($"| **Latest Payload Snapshot** | `{payloadId}` |");
        sb.AppendLine($"| **Latest Receipt Snapshot** | `{receiptId}` |");
        sb.AppendLine($"| **Kit Generated Date (UTC)** | `{now:yyyy-MM-dd HH:mm:ss} UTC` |");
        if (!string.IsNullOrWhiteSpace(options.RepositoryPasswordHint))
        {
            sb.AppendLine($"| **Password Hint** | `{options.RepositoryPasswordHint}` |");
        }
        sb.AppendLine();

        if (options.ProtectedComponents != null && options.ProtectedComponents.Count > 0)
        {
            sb.AppendLine("### Key Protected Components in this Set:");
            foreach (var comp in options.ProtectedComponents)
            {
                sb.AppendLine($"- `{comp}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 2. Step-by-Step Clean Machine Disaster Recovery");
        sb.AppendLine();
        sb.AppendLine("### Step 1: Install or Download Standalone Restic");
        sb.AppendLine("Obtain official Restic binary on your recovery machine:");
        sb.AppendLine("- **Windows**: Run `winget install restic.restic` or download `restic.exe` from `https://github.com/restic/restic/releases`");
        sb.AppendLine("- **Linux**: `sudo apt install restic` (Debian/Ubuntu) or `sudo dnf install restic` (Fedora) or download binary");
        sb.AppendLine("- **macOS**: `brew install restic`");
        sb.AppendLine();
        sb.AppendLine("### Step 2: Set Environment Variables (Optional for convenience)");
        sb.AppendLine("```bash");
        sb.AppendLine("# Windows PowerShell:");
        sb.AppendLine($"$env:RESTIC_REPOSITORY = \"{repo}\"");
        sb.AppendLine("# $env:RESTIC_PASSWORD = \"<YOUR_PASSWORD>\" # Or let restic prompt you interactively");
        sb.AppendLine();
        sb.AppendLine("# Linux / macOS Bash:");
        sb.AppendLine($"export RESTIC_REPOSITORY=\"{repo}\"");
        sb.AppendLine("# export RESTIC_PASSWORD=\"<YOUR_PASSWORD>\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Step 3: Inspect Repository Health & Snapshots");
        sb.AppendLine("```bash");
        sb.AppendLine("# Verify repository encryption key and structural integrity:");
        sb.AppendLine($"restic -r \"{repo}\" check");
        sb.AppendLine();
        sb.AppendLine("# List all snapshots recorded in this repository:");
        sb.AppendLine($"restic -r \"{repo}\" snapshots");
        sb.AppendLine();
        sb.AppendLine("# Filter for actual payload data snapshots (excluding control receipts):");
        sb.AppendLine($"restic -r \"{repo}\" snapshots --tag role:payload");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Step 4: Inspect Frozen Descriptor Metadata");
        sb.AppendLine("Each verified backup set includes an immutable `descriptor.json` documenting the exact source mappings:");
        sb.AppendLine("```bash");
        sb.AppendLine($"# View original source directories recorded inside the backup:");
        sb.AppendLine($"restic -r \"{repo}\" dump {receiptId} descriptor.json");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Step 5: Restore Protected Files to Target Disk");
        sb.AppendLine("```bash");
        sb.AppendLine("# Full Restore to a target directory:");
        sb.AppendLine($"restic -r \"{repo}\" restore {payloadId} --target \"C:\\RestoredBackup\"");
        sb.AppendLine();
        sb.AppendLine("# Selective Component Restore (e.g., restore only game saves):");
        sb.AppendLine($"restic -r \"{repo}\" restore {payloadId} --target \"C:\\RestoredBackup\" --include \"*Saved Games*\" --include \"*AppData*\"");
        sb.AppendLine();
        sb.AppendLine("# Selective File or Game Restore (e.g., restore specific game title):");
        sb.AppendLine($"restic -r \"{repo}\" restore {payloadId} --target \"C:\\RestoredBackup\" --include \"*Skyrim*\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 3. Headless Recovery via UniversalBackup.Cli (1-Command Rebuild)");
        sb.AppendLine("If you have `UniversalBackup.Cli` available on the clean machine:");
        sb.AppendLine();
        sb.AppendLine("```bash");
        sb.AppendLine("# 1. Rebuild SQLite local catalog database from scratch:");
        sb.AppendLine($"UniversalBackup.Cli rebuild-catalog --repo \"{repo}\"");
        sb.AppendLine();
        sb.AppendLine("# 2. List reconstructed snapshots and verification states:");
        sb.AppendLine($"UniversalBackup.Cli list-snapshots --repo \"{repo}\"");
        sb.AppendLine();
        sb.AppendLine("# 3. Restore snapshot payload directly:");
        sb.AppendLine($"UniversalBackup.Cli restore --repo \"{repo}\" --snapshot {payloadId} --target \"C:\\RestoredData\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## 4. Cloud Repository Recovery via Rclone Bridge");
        sb.AppendLine("If your repository is stored in Google Drive or Microsoft OneDrive:");
        sb.AppendLine("1. Install `rclone` (`https://rclone.org/downloads/`).");
        sb.AppendLine("2. Run `rclone config` to link your cloud drive account.");
        sb.AppendLine("3. Access and restore through the rclone bridge syntax:");
        sb.AppendLine("```bash");
        sb.AppendLine("restic -r rclone:<remote_name>:UniversalBackup/default snapshots");
        sb.AppendLine("restic -r rclone:<remote_name>:UniversalBackup/default restore latest --target \"C:\\RestoredData\"");
        sb.AppendLine("```");
        sb.AppendLine();

        return Task.FromResult(sb.ToString());
    }

    public async Task<string> GenerateRecoveryKitHtmlAsync(EmergencyRecoveryKitOptions options, CancellationToken ct = default)
    {
        var md = await GenerateRecoveryKitMarkdownAsync(options, ct);

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine("  <title>Universal Backup — Emergency Recovery Kit</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root {");
        sb.AppendLine("      --bg: #0F172A; --card: #1E293B; --border: #334155;");
        sb.AppendLine("      --text: #F8FAFC; --muted: #94A3B8; --accent: #38BDF8;");
        sb.AppendLine("      --code-bg: #0B0F19; --green: #10B981; --amber: #F59E0B;");
        sb.AppendLine("    }");
        sb.AppendLine("    @media print {");
        sb.AppendLine("      body { background: white !important; color: black !important; }");
        sb.AppendLine("      .card { border: 1px solid #ccc !important; background: #fafafa !important; }");
        sb.AppendLine("      pre, code { background: #f0f0f0 !important; color: black !important; }");
        sb.AppendLine("    }");
        sb.AppendLine("    * { box-sizing: border-box; margin: 0; padding: 0; }");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; background: var(--bg); color: var(--text); padding: 32px; line-height: 1.6; }");
        sb.AppendLine("    .container { max-width: 900px; margin: 0 auto; }");
        sb.AppendLine("    h1 { font-size: 26px; font-weight: 800; color: var(--text); margin-bottom: 6px; }");
        sb.AppendLine("    h2 { font-size: 18px; font-weight: 700; color: var(--accent); margin-top: 28px; margin-bottom: 12px; border-bottom: 1px solid var(--border); padding-bottom: 6px; }");
        sb.AppendLine("    h3 { font-size: 14px; font-weight: 600; color: var(--text); margin-top: 16px; margin-bottom: 8px; }");
        sb.AppendLine("    p, li { font-size: 13px; color: var(--text); margin-bottom: 8px; }");
        sb.AppendLine("    ul { margin-left: 20px; margin-bottom: 12px; }");
        sb.AppendLine("    .badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 11px; font-weight: bold; background: rgba(56,189,248,0.2); color: var(--accent); }");
        sb.AppendLine("    .card { background: var(--card); border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 20px; }");
        sb.AppendLine("    .notice { background: rgba(245, 158, 11, 0.1); border-left: 4px solid var(--amber); padding: 12px; border-radius: 4px; margin-bottom: 20px; font-size: 12px; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin-bottom: 16px; font-size: 12px; }");
        sb.AppendLine("    th, td { border: 1px solid var(--border); padding: 8px 12px; text-align: left; }");
        sb.AppendLine("    th { background: rgba(255,255,255,0.05); color: var(--muted); font-weight: 600; }");
        sb.AppendLine("    pre { background: var(--code-bg); border: 1px solid var(--border); border-radius: 6px; padding: 12px; overflow-x: auto; font-family: Consolas, 'Courier New', monospace; font-size: 12px; color: #E2E8F0; margin-bottom: 16px; }");
        sb.AppendLine("    code { font-family: Consolas, 'Courier New', monospace; font-size: 12px; }");
        sb.AppendLine("    .footer { text-align: center; font-size: 11px; color: var(--muted); margin-top: 40px; border-top: 1px solid var(--border); padding-top: 16px; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <div class=\"container\">");
        sb.AppendLine("    <div style=\"display:flex; justify-content:space-between; align-items:center; margin-bottom:16px;\">");
        sb.AppendLine("      <div>");
        sb.AppendLine("        <h1>Universal Backup — Emergency Recovery Kit</h1>");
        sb.AppendLine("        <p style=\"color:var(--muted); font-size:12px;\">Autonomous Disaster Recovery Guide for Clean Systems (Zero Application Dependencies)</p>");
        sb.AppendLine("      </div>");
        sb.AppendLine("      <span class=\"badge\">STANDALONE RESTORE</span>");
        sb.AppendLine("    </div>");
        sb.AppendLine();
        sb.AppendLine("    <div class=\"notice\">");
        sb.AppendLine("      <strong>GUARANTEED CATALOG INDEPENDENCE (ADR-004)</strong><br>");
        sb.AppendLine("      Universal Backup stores all data inside pure, encrypted Restic repositories. Even if this computer is destroyed, stolen, or clean-installed, you can restore 100% of your data on any OS using the official open-source <code>restic</code> command line tool without requiring Universal Backup.");
        sb.AppendLine("    </div>");
        sb.AppendLine();
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2 style=\"margin-top:0;\">1. System &amp; Repository Profile</h2>");
        sb.AppendLine("      <table>");
        sb.AppendLine("        <tr><th>Parameter</th><th>Value</th></tr>");
        sb.AppendLine($"        <tr><td><strong>Repository URI / Path</strong></td><td><code>{EscapeHtml(options.RepositoryPath)}</code></td></tr>");
        sb.AppendLine($"        <tr><td><strong>Backup Plan Name</strong></td><td><code>{EscapeHtml(options.PlanName ?? "Primary Backup Plan")}</code></td></tr>");
        sb.AppendLine($"        <tr><td><strong>Protected Machine</strong></td><td><code>{EscapeHtml(options.MachineName ?? Environment.MachineName)}</code></td></tr>");
        sb.AppendLine($"        <tr><td><strong>User Profile</strong></td><td><code>{EscapeHtml(options.UserName ?? Environment.UserName)}</code></td></tr>");
        sb.AppendLine($"        <tr><td><strong>Latest Payload Snapshot</strong></td><td><code>{EscapeHtml(options.LatestPayloadSnapshotId ?? "latest")}</code></td></tr>");
        sb.AppendLine($"        <tr><td><strong>Latest Control Receipt Snapshot</strong></td><td><code>{EscapeHtml(options.LatestReceiptSnapshotId ?? "N/A")}</code></td></tr>");
        sb.AppendLine($"        <tr><td><strong>Document Generated (UTC)</strong></td><td>{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} UTC</td></tr>");
        if (!string.IsNullOrWhiteSpace(options.RepositoryPasswordHint))
        {
            sb.AppendLine($"        <tr><td><strong>Password Hint</strong></td><td><code>{EscapeHtml(options.RepositoryPasswordHint)}</code></td></tr>");
        }
        sb.AppendLine("      </table>");
        sb.AppendLine("    </div>");
        sb.AppendLine();
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2 style=\"margin-top:0;\">2. Disaster Recovery Terminal Commands</h2>");
        sb.AppendLine("      <h3>Step 1: Verify Repository Connection &amp; Health</h3>");
        sb.AppendLine($"      <pre>restic -r \"{EscapeHtml(options.RepositoryPath)}\" check</pre>");
        sb.AppendLine();
        sb.AppendLine("      <h3>Step 2: List Data Payload Snapshots</h3>");
        sb.AppendLine($"      <pre>restic -r \"{EscapeHtml(options.RepositoryPath)}\" snapshots --tag role:payload</pre>");
        sb.AppendLine();
        sb.AppendLine("      <h3>Step 3: Full Disaster Recovery Restore</h3>");
        sb.AppendLine($"      <pre>restic -r \"{EscapeHtml(options.RepositoryPath)}\" restore {EscapeHtml(options.LatestPayloadSnapshotId ?? "latest")} --target \"C:\\RestoredData\"</pre>");
        sb.AppendLine();
        sb.AppendLine("      <h3>Step 4: Selective Game Saves Restore</h3>");
        sb.AppendLine($"      <pre>restic -r \"{EscapeHtml(options.RepositoryPath)}\" restore {EscapeHtml(options.LatestPayloadSnapshotId ?? "latest")} --target \"C:\\RestoredData\" --include \"*Saved Games*\" --include \"*AppData*\"</pre>");
        sb.AppendLine("    </div>");
        sb.AppendLine();
        sb.AppendLine("    <div class=\"card\">");
        sb.AppendLine("      <h2 style=\"margin-top:0;\">3. Headless UniversalBackup.Cli Rebuild</h2>");
        sb.AppendLine($"      <pre>UniversalBackup.Cli rebuild-catalog --repo \"{EscapeHtml(options.RepositoryPath)}\"\nUniversalBackup.Cli restore --repo \"{EscapeHtml(options.RepositoryPath)}\" --snapshot latest --target \"C:\\RestoredData\"</pre>");
        sb.AppendLine("    </div>");
        sb.AppendLine();
        sb.AppendLine("    <div class=\"footer\">Universal Backup &bull; Clean Machine Disaster Recovery Kit &bull; Keep this document safely on a USB drive or printed on paper.</div>");
        sb.AppendLine("  </div>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    public async Task SaveRecoveryKitAsync(EmergencyRecoveryKitOptions options, string outputPath, bool html = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var parent = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var content = html
            ? await GenerateRecoveryKitHtmlAsync(options, ct)
            : await GenerateRecoveryKitMarkdownAsync(options, ct);

        await File.WriteAllTextAsync(outputPath, content, Encoding.UTF8, ct);
    }

    private static string EscapeHtml(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return input.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}

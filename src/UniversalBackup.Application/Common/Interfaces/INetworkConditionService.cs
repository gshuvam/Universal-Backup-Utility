namespace UniversalBackup.Application.Common.Interfaces;

/// <summary>
/// Service inspecting active network connectivity and metered connection states
/// to safeguard user cellular or restricted bandwidth during background replication.
/// </summary>
public interface INetworkConditionService
{
    /// <summary>
    /// Checks whether an active network connection is currently available.
    /// </summary>
    bool IsNetworkAvailable();

    /// <summary>
    /// Checks whether the active internet connection is flagged as metered (e.g. cellular hotspot, data-capped network).
    /// </summary>
    bool IsMeteredConnection();
}

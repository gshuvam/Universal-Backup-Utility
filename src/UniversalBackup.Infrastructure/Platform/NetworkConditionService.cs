using System;
using System.Net.NetworkInformation;
using UniversalBackup.Application.Common.Interfaces;

namespace UniversalBackup.Infrastructure.Platform;

/// <summary>
/// Cross-platform network condition monitor reporting connectivity and metered data cap states.
/// </summary>
public sealed class NetworkConditionService : INetworkConditionService
{
    private readonly Func<bool>? _isMeteredProvider;
    private readonly Func<bool>? _isNetworkAvailableProvider;

    public NetworkConditionService(
        Func<bool>? isMeteredProvider = null,
        Func<bool>? isNetworkAvailableProvider = null)
    {
        _isMeteredProvider = isMeteredProvider;
        _isNetworkAvailableProvider = isNetworkAvailableProvider;
    }

    /// <inheritdoc />
    public bool IsNetworkAvailable()
    {
        if (_isNetworkAvailableProvider != null)
        {
            return _isNetworkAvailableProvider();
        }

        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch
        {
            return true;
        }
    }

    /// <inheritdoc />
    public bool IsMeteredConnection()
    {
        if (_isMeteredProvider != null)
        {
            return _isMeteredProvider();
        }

        // On desktop .NET without WinRT projection, default to false unless configured
        return false;
    }
}

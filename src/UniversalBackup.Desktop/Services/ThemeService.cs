using System;
using Avalonia.Styling;
using Avalonia.Threading;

namespace UniversalBackup.Desktop.Services;

/// <summary>
/// Implements theme switching for Avalonia desktop runtime.
/// </summary>
public class ThemeService : IThemeService
{
    private AppTheme _currentTheme = AppTheme.System;

    public AppTheme CurrentTheme => _currentTheme;

    public event EventHandler<AppTheme>? ThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        if (_currentTheme == theme && Avalonia.Application.Current != null)
        {
            return;
        }

        _currentTheme = theme;

        void ApplyTheme()
        {
            if (Avalonia.Application.Current is null) return;

            Avalonia.Application.Current.RequestedThemeVariant = theme switch
            {
                AppTheme.Dark => ThemeVariant.Dark,
                AppTheme.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default
            };
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyTheme();
        }
        else
        {
            Dispatcher.UIThread.Post(ApplyTheme);
        }

        ThemeChanged?.Invoke(this, _currentTheme);
    }

    public void ToggleTheme()
    {
        var nextTheme = _currentTheme switch
        {
            AppTheme.System => AppTheme.Dark,
            AppTheme.Dark => AppTheme.Light,
            AppTheme.Light => AppTheme.System,
            _ => AppTheme.System
        };

        SetTheme(nextTheme);
    }
}


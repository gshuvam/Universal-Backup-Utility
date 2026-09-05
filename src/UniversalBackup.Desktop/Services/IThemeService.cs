using System;

namespace UniversalBackup.Desktop.Services;

/// <summary>
/// Supported visual theme variants for the desktop application.
/// </summary>
public enum AppTheme
{
    System,
    Dark,
    Light
}

/// <summary>
/// Controls the active theme variant across the application.
/// </summary>
public interface IThemeService
{
    /// <summary>
    /// Gets the current theme variant setting.
    /// </summary>
    AppTheme CurrentTheme { get; }

    /// <summary>
    /// Event fired when the active theme variant changes.
    /// </summary>
    event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>
    /// Sets the application theme to the specified variant.
    /// </summary>
    /// <param name="theme">The target theme variant.</param>
    void SetTheme(AppTheme theme);

    /// <summary>
    /// Cycles through theme variants (System -> Dark -> Light -> System).
    /// </summary>
    void ToggleTheme();
}


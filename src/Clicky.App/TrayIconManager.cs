using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace Clicky.App;

/// <summary>
/// Represents a (provider, model) pair for the tray Model submenu.
/// </summary>
public sealed class ModelMenuEntry
{
    public string Provider { get; init; } = "";
    public string Model { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public string? DisabledTooltip { get; init; }
}

/// <summary>
/// Event args for when the user selects a model from the tray submenu.
/// </summary>
public sealed class ModelSelectedEventArgs : EventArgs
{
    public string Provider { get; }
    public string Model { get; }

    public ModelSelectedEventArgs(string provider, string model)
    {
        Provider = provider;
        Model = model;
    }
}

/// <summary>
/// Event args for a local overlay calibration request from the tray menu.
/// </summary>
public sealed class OverlayTestRequestedEventArgs : EventArgs
{
    public string PresetId { get; }

    public OverlayTestRequestedEventArgs(string presetId)
    {
        PresetId = presetId;
    }
}

/// <summary>
/// Manages the system tray icon, context menu, and left-click events.
/// Mirrors the Mac MenuBarPanelManager status item behavior.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private bool _disposed;
    private MenuItem? _modelSubmenu;

    /// <summary>
    /// Raised when the user left-clicks the tray icon.
    /// The Companion layer subscribes to this to toggle the control panel.
    /// </summary>
    public event EventHandler? TrayIconClicked;

    /// <summary>Raised when the user clicks Settings... in the context menu.</summary>
    public event EventHandler? SettingsClicked;

    /// <summary>Raised when the user clicks a model in the Model submenu.</summary>
    public event EventHandler<ModelSelectedEventArgs>? ModelSelected;

    /// <summary>Raised when the user clicks 'Test Overlay' in the context menu.</summary>
    public event EventHandler<OverlayTestRequestedEventArgs>? OverlayTestRequested;

    /// <summary>Raised when the user opens the desktop pointing smoke fixture.</summary>
    public event EventHandler? DesktopSmokeTestRequested;

    /// <summary>Raised when the user runs provider timing diagnostics.</summary>
    public event EventHandler? ProviderTimingDiagnosticsRequested;

    public TrayIconManager()
    {
        _trayIcon = new TaskbarIcon
        {
            Icon = CreateDefaultIcon(),
            ToolTipText = $"Clicky {VersionInfo.Current}",
            ContextMenu = BuildContextMenu(),
        };

        _trayIcon.TrayLeftMouseUp += OnTrayLeftClick;
    }

    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        TrayIconClicked?.Invoke(this, EventArgs.Empty);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open" };
        openItem.Click += (_, _) => TrayIconClicked?.Invoke(this, EventArgs.Empty);

        var settingsItem = new MenuItem { Header = "Settings..." };
        settingsItem.Click += (_, _) => SettingsClicked?.Invoke(this, EventArgs.Empty);

        _modelSubmenu = new MenuItem { Header = "Model" };

        var testOverlayItem = BuildOverlayTestSubmenu();
        var desktopSmokeItem = new MenuItem { Header = "Desktop Smoke Test" };
        desktopSmokeItem.Click += (_, _) => DesktopSmokeTestRequested?.Invoke(this, EventArgs.Empty);
        var diagnosticsItem = new MenuItem { Header = "Provider Timing Diagnostics" };
        diagnosticsItem.Click += (_, _) => ProviderTimingDiagnosticsRequested?.Invoke(this, EventArgs.Empty);

        var versionItem = new MenuItem
        {
            Header = $"Version {VersionInfo.Current}",
            IsEnabled = false,
        };

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => Application.Current.Shutdown();

        menu.Items.Add(openItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(_modelSubmenu);
        menu.Items.Add(testOverlayItem);
        menu.Items.Add(desktopSmokeItem);
        menu.Items.Add(diagnosticsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(versionItem);
        menu.Items.Add(quitItem);

        return menu;
    }

    private MenuItem BuildOverlayTestSubmenu()
    {
        var parent = new MenuItem { Header = "Test Overlay" };

        AddOverlayTestItem(parent, "Center", "center");
        AddOverlayTestItem(parent, "Top Left", "top-left");
        AddOverlayTestItem(parent, "Top Right", "top-right");
        AddOverlayTestItem(parent, "Bottom Left", "bottom-left");
        AddOverlayTestItem(parent, "Bottom Right", "bottom-right");
        AddOverlayTestItem(parent, "Upper Mid", "upper-mid");
        AddOverlayTestItem(parent, "Lower Mid", "lower-mid");
        AddOverlayTestItem(parent, "Left Mid", "left-mid");
        AddOverlayTestItem(parent, "Right Mid", "right-mid");
        AddOverlayTestItem(parent, "Mouse Position", "mouse");

        return parent;
    }

    private void AddOverlayTestItem(MenuItem parent, string header, string presetId)
    {
        var item = new MenuItem { Header = header, Tag = presetId };
        item.Click += OnOverlayTestItemClicked;
        parent.Items.Add(item);
    }

    private void OnOverlayTestItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string presetId })
        {
            OverlayTestRequested?.Invoke(this, new OverlayTestRequestedEventArgs(presetId));
        }
    }

    /// <summary>
    /// Rebuilds the Model submenu items. Call after startup and after settings change.
    /// </summary>
    public void UpdateModelMenu(IReadOnlyList<ModelMenuEntry> entries, string activeProvider, string activeModel)
    {
        if (_modelSubmenu is null) return;

        _modelSubmenu.Items.Clear();

        foreach (var entry in entries)
        {
            bool isActive = entry.Provider == activeProvider && entry.Model == activeModel;
            var item = new MenuItem
            {
                Header = (isActive ? "\u25CF " : "   ") + entry.DisplayName,
                IsEnabled = entry.IsEnabled,
                Tag = entry,
            };

            if (!entry.IsEnabled && entry.DisabledTooltip is not null)
            {
                item.ToolTip = entry.DisabledTooltip;
            }

            item.Click += OnModelItemClicked;
            _modelSubmenu.Items.Add(item);
        }
    }

    private void OnModelItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: ModelMenuEntry entry })
        {
            ModelSelected?.Invoke(this, new ModelSelectedEventArgs(entry.Provider, entry.Model));
        }
    }

    public void HideOpenPopups()
    {
        if (_trayIcon.ContextMenu is { } menu)
        {
            menu.IsOpen = false;
        }
    }

    /// <summary>
    /// Creates a simple blue triangle icon matching the Clicky branding.
    /// </summary>
    private static Icon CreateDefaultIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Blue triangle cursor shape
        var points = new[]
        {
            new PointF(6, 4),
            new PointF(6, 28),
            new PointF(26, 16),
        };

        using var brush = new SolidBrush(Color.FromArgb(255, 59, 130, 246));
        g.FillPolygon(brush, points);

        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _trayIcon.TrayLeftMouseUp -= OnTrayLeftClick;
        _trayIcon.Dispose();
    }
}

using System.Windows;
using System.Windows.Controls;
using Clicky.Companion;

namespace Clicky.App;

/// <summary>
/// Local-only deterministic desktop fixture for validating the live pointing path.
/// </summary>
public partial class PointingSmokeWindow : Window
{
    private readonly CompanionManager _companionManager;

    public PointingSmokeWindow(CompanionManager companionManager)
    {
        _companionManager = companionManager;
        InitializeComponent();
    }

    private async void Box_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        var label = button.Content?.ToString() ?? "smoke target";
        var target = GetElementCenterInScreenPixels(button);
        StatusText.Text = $"Running smoke test for {label} at {target.X:F0},{target.Y:F0}...";

        await _companionManager.TestPointingAtScreenPointAsync(target, label);

        StatusText.Text = $"Ran smoke test for {label}. Check %APPDATA%\\Clicky\\point-debug and debug.log.";
    }

    internal static Point GetElementCenterInScreenPixels(FrameworkElement element)
    {
        element.UpdateLayout();
        return element.PointToScreen(new Point(element.ActualWidth / 2.0, element.ActualHeight / 2.0));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

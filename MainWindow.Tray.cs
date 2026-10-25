using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The notification area on the bar: the chevron beside the clock and the flyout that lists the
/// icons background applications registered through Shell_NotifyIcon. The icons behave the way they
/// do under Explorer - a left click asks the owning window to activate itself and a right click asks
/// it for its menu, so a background application receives exactly the same messages either way.
/// </summary>
public partial class MainWindow
{
    /// <summary>Width of the icon flyout, in DIPs.</summary>
    private const double TrayWidth = 264;

    private void InitializeTray()
    {
        TrayItems.ItemsSource = NotificationArea.Icons;

        // Registrations arrive on this thread, because the host window is pumped by the bar's own
        // dispatcher, but the refresh is queued so a change can never re-enter the window procedure.
        NotificationArea.Changed += () => Dispatcher.BeginInvoke(new Action(RefreshTray));

        RefreshTray();
    }

    /// <summary>
    /// Shows the chevron only while something has registered an icon, the way Explorer hides its
    /// overflow button when the notification area is empty.
    /// </summary>
    private void RefreshTray()
    {
        var count = NotificationArea.Icons.Count;
        TrayButton.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (count == 0 && TrayPopup.IsOpen)
        {
            TrayPopup.IsOpen = false;
        }
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        if (NotificationArea.Icons.Count == 0)
        {
            RefreshTray();
            return;
        }

        ShowFlyoutLeft(TrayPopup, TrayWidth, TrayButton);
    }

    /// <summary>Left click on an icon: the owner activates itself, as it does under Explorer.</summary>
    private void TrayItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrayIcon icon)
        {
            return;
        }

        e.Handled = true;
        CloseFlyouts();
        NotificationArea.Activate(icon);
    }

    /// <summary>
    /// Right click on an icon: the owner is asked for its menu. Explorer closes its overflow first,
    /// because the menu belongs to the other application and would otherwise sit under the flyout.
    /// </summary>
    private void TrayItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not TrayIcon icon)
        {
            return;
        }

        e.Handled = true;
        CloseFlyouts();
        NotificationArea.ShowMenu(icon);
    }
}

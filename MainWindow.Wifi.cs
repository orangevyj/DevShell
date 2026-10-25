using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The Wi-Fi half of the network flyout: what the radio can see, joining a network with a password
/// and asking for a fresh scan. The section only exists on a machine with a wireless adapter, so an
/// ethernet-only box shows the adapter list exactly as before.
/// </summary>
public partial class MainWindow
{
    private readonly List<WifiNetwork> _wifiNetworks = new();
    private WifiNetwork? _wifiTarget;
    private bool _wifiScanning;

    /// <summary>Fills the Wi-Fi part of the network flyout, or hides it when there is no radio.</summary>
    private void RefreshWifi()
    {
        try
        {
            if (!Wlan.IsAvailable())
            {
                WifiSection.Visibility = Visibility.Collapsed;
                return;
            }

            WifiSection.Visibility = Visibility.Visible;

            _wifiNetworks.Clear();
            _wifiNetworks.AddRange(Wlan.Networks());
            WifiList.ItemsSource = _wifiNetworks.ToArray();

            var connected = _wifiNetworks.FirstOrDefault(n => n.Connected);
            WifiStatusText.Text = connected is null
                ? _wifiNetworks.Count == 0 ? Strings.T("Wifi_NoneInRange") : Strings.T("Status_NotConnected")
                : Strings.Format("Wifi_ConnectedTo", connected.Name, connected.Signal);

            // Once the join has landed the panel has nothing left to ask for.
            if (connected is not null && _wifiTarget?.Ssid == connected.Ssid)
            {
                CloseWifiJoin();
            }
        }
        catch (Exception ex)
        {
            App.Log("Wifi.Refresh", ex);
            WifiSection.Visibility = Visibility.Collapsed;
        }
    }

    private void WifiScan_Click(object sender, RoutedEventArgs e) => _ = ScanWifiAsync();

    private async Task ScanWifiAsync()
    {
        if (_wifiScanning)
        {
            return;
        }

        _wifiScanning = true;
        var previous = WifiStatusText.Text;
        WifiStatusText.Text = Strings.T("Wifi_Scanning");

        try
        {
            Wlan.Scan();

            // WlanScan returns immediately; Windows reports what it found a few seconds later.
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (NetworkPopup.IsOpen)
            {
                RefreshWifi();
            }
            else
            {
                WifiStatusText.Text = previous;
            }
        }
        catch (Exception ex)
        {
            App.Log("Wifi.Scan", ex);
            WifiStatusText.Text = Strings.T("Wifi_ScanFailed");
        }
        finally
        {
            _wifiScanning = false;
        }
    }

    private void WifiRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not WifiNetwork network || network.Connected)
        {
            return;
        }

        _wifiTarget = network;
        WifiJoinTitle.Text = network.Name;
        WifiPassword.Password = string.Empty;
        WifiPassword.Visibility = network.NeedsPassword ? Visibility.Visible : Visibility.Collapsed;
        WifiJoinButton.Content = Strings.T("Wifi_Connect");
        WifiJoinHint.Text = network.NeedsPassword
            ? Strings.T("Wifi_EnterPassword")
            : network.HasProfile
                ? Strings.T("Wifi_AlreadySaved")
                : Strings.T("Wifi_OpenNetwork");
        WifiJoinPanel.Visibility = Visibility.Visible;

        if (network.NeedsPassword)
        {
            WifiPassword.Focus();
        }
    }

    private void WifiPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            WifiJoin_Click(sender, e);
        }
    }

    private async void WifiJoin_Click(object sender, RoutedEventArgs e)
    {
        if (_wifiTarget is not { } target)
        {
            return;
        }

        WifiJoinHint.Text = $"Connecting to {target.Name}…";
        WifiJoinButton.IsEnabled = false;

        string? error;
        try
        {
            error = Wlan.Join(target, WifiPassword.Password);
        }
        catch (Exception ex)
        {
            App.Log("Wifi.Join", ex);
            error = ex.Message;
        }
        finally
        {
            WifiJoinButton.IsEnabled = true;
        }

        if (error is not null)
        {
            WifiJoinHint.Text = error;
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            App.Log("Wifi.JoinWait", ex);
            return;
        }

        if (NetworkPopup.IsOpen)
        {
            RefreshWifi();
            if (WifiJoinPanel.Visibility == Visibility.Visible)
            {
                WifiJoinHint.Text = $"Windows has not joined {target.Name} yet. Check the password and try again.";
            }
        }
    }

    private void WifiJoinCancel_Click(object sender, RoutedEventArgs e) => CloseWifiJoin();

    private void CloseWifiJoin()
    {
        _wifiTarget = null;
        WifiPassword.Password = string.Empty;
        WifiJoinPanel.Visibility = Visibility.Collapsed;
    }
}

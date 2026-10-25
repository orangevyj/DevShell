using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// DevShell's own settings, reached from the rail of the Start menu. Everything here writes straight
/// to the shared <see cref="Services.Config"/> and then tells whoever cares to catch up, so the
/// desktop surface, the bar and the file on disk never disagree with each other.
/// </summary>
public partial class MainWindow
{
    private void StartDevShell_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();
        SynchronizeSettingsMenu();
        ShowFlyoutLeft(SettingsPopup, 396, StartButton);
    }

    /// <summary>
    /// The settings button wears the icon the build ships with. It is embedded in this assembly
    /// (Assets\DevShell.png, the same artwork Assets\DevShell.ico is generated from), so it is the
    /// same picture on every machine and there is nothing on disk for it to go looking for.
    /// </summary>
    private void InitializeSettingsMenu() => StartDevShellIcon.Source = SettingsIcon();

    private static ImageSource? SettingsIcon()
        => EmbeddedImage("pack://application:,,,/Assets/DevShell.png", "Settings.Icon");

    /// <summary>A picture baked into this assembly, or null (with a line in the log) if it cannot be read.</summary>
    private static ImageSource? EmbeddedImage(string uri, string source)
    {
        try
        {
            var packed = new BitmapImage(new Uri(uri));
            packed.Freeze();
            return packed;
        }
        catch (Exception ex)
        {
            App.Log(source, ex);
            return null;
        }
    }

    /// <summary>
    /// True while the menu is being filled in from the config. Both sliders raise ValueChanged as
    /// their values are set, and a handler that wrote the config back would turn opening the flyout
    /// into a write - with the tick snapping on the icon size able to move a stored value on its own.
    /// </summary>
    private bool _synchronizingSettings;

    private void SynchronizeSettingsMenu()
    {
        _synchronizingSettings = true;

        try
        {
            SetShowDesktopIcons.IsChecked = _config.DesktopShowIcons;
            SetIncludePublic.IsChecked = _config.DesktopIncludePublic;
            SetShowRecycleBin.IsChecked = _config.DesktopShowRecycleBin;
            SetShowHidden.IsChecked = _config.DesktopShowHiddenItems;
            SetShowExtensions.IsChecked = _config.DesktopShowFileExtensions;
            SetAcrylic.IsChecked = _config.Acrylic;
            SetSeconds.IsChecked = _config.ShowSecondsInClock;

            SetIconSize.Value = _config.DesktopIconSize;
            SetIconSizeText.Text = $"{_config.DesktopIconSize:0} px";

            SetBarTransparency.Value = Math.Round((1 - Math.Clamp(_config.BarOpacity, 0.15, 1.0)) * 100);
            SetBarTransparencyText.Text = $"{SetBarTransparency.Value:0}%";
        }
        finally
        {
            _synchronizingSettings = false;
        }

        SynchronizeShellMode();
        SynchronizeTroubleshooters();
        SynchronizeLanguage();
    }

    // ---- language -------------------------------------------------------

    /// <summary>Marks the language the setting asks for; "auto" follows Windows.</summary>
    private void SynchronizeLanguage()
    {
        _synchronizingSettings = true;

        try
        {
            var setting = string.IsNullOrWhiteSpace(_config.Language) ? "auto" : _config.Language;

            SetLanguageAuto.IsChecked = setting == "auto";
            SetLanguageEn.IsChecked = setting == "en";
            SetLanguageRu.IsChecked = setting == "ru";
            SetLanguageDe.IsChecked = setting == "de";
            SetLanguageFr.IsChecked = setting == "fr";
            SetLanguageEs.IsChecked = setting == "es";
        }
        finally
        {
            _synchronizingSettings = false;
        }
    }

    private void SetLanguage_Changed(object sender, RoutedEventArgs e)
    {
        if (_synchronizingSettings || !IsLoaded || _config is null)
        {
            return;
        }

        var code = (sender as FrameworkElement)?.Tag as string ?? "auto";

        _config.Language = code;
        _config.Save();

        // Every element the XAML binds relabels itself, and MainWindow.ApplyLanguage re-runs the
        // strings set from code, so the change is visible without a restart.
        Strings.Apply(code);
        SynchronizeLanguage();
    }

    // ---- which troubleshooters the status buttons run --------------------

    /// <summary>
    /// Shows which family the right-click on the network and volume buttons opens, and says in one
    /// line what the other one would do instead.
    /// </summary>
    private void SynchronizeTroubleshooters()
    {
        _synchronizingSettings = true;

        try
        {
            SetTroubleshootClassic.IsChecked = _config.UseClassicTroubleshooters;
            SetTroubleshootGetHelp.IsChecked = !_config.UseClassicTroubleshooters;
        }
        finally
        {
            _synchronizingSettings = false;
        }

        SetTroubleshooterHint.Text = _config.UseClassicTroubleshooters
            ? Strings.T("Settings_TroubleshooterHintClassic")
            : Strings.T("Settings_TroubleshooterHintGetHelp");
    }

    private void SetTroubleshooter_Changed(object sender, RoutedEventArgs e)
    {
        // The XAML raises this while it is still being read, and the mirror sets the boxes itself.
        if (_synchronizingSettings || !IsLoaded || _config is null)
        {
            return;
        }

        _config.UseClassicTroubleshooters = SetTroubleshootClassic.IsChecked == true;
        _config.Save();
        SynchronizeTroubleshooters();
    }

    // ---- desktop --------------------------------------------------------

    private void SetShowDesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        _config.DesktopShowIcons = SetShowDesktopIcons.IsChecked == true;
        SaveAndApplyDesktop();
    }

    private void SetIncludePublic_Click(object sender, RoutedEventArgs e)
    {
        _config.DesktopIncludePublic = SetIncludePublic.IsChecked == true;
        SaveAndApplyDesktop();
    }

    private void SetShowRecycleBin_Click(object sender, RoutedEventArgs e)
    {
        _config.DesktopShowRecycleBin = SetShowRecycleBin.IsChecked == true;
        SaveAndApplyDesktop();
    }

    private void SetShowHidden_Click(object sender, RoutedEventArgs e)
    {
        _config.DesktopShowHiddenItems = SetShowHidden.IsChecked == true;
        SaveAndApplyDesktop();
    }

    /// <summary>
    /// Whether a file keeps its extension in its label. Windows starts this off in step with Explorer's
    /// folder option; from here on it is DevShell's own.
    /// </summary>
    private void SetShowExtensions_Click(object sender, RoutedEventArgs e)
    {
        _config.DesktopShowFileExtensions = SetShowExtensions.IsChecked == true;
        SaveAndApplyDesktop();
    }

    // ---- how DevShell and Windows share the machine ---------------------

    private bool _synchronizingShellMode;

    /// <summary>
    /// Shows which of the two lives the machine is set up for. The registry is what Windows acts on,
    /// so it is read here rather than remembered: a config file can be out of date, a key cannot.
    /// </summary>
    private void SynchronizeShellMode()
    {
        var shell = Services.WindowsIntegration.IsDevShellTheShell();

        _synchronizingShellMode = true;
        SetShellReplace.IsChecked = shell;
        SetShellAlongside.IsChecked = !shell;
        SetShellHint.Text = shell
            ? Strings.T("Settings_ShellHintOn")
            : Strings.T("Settings_ShellHintOff");
        _synchronizingShellMode = false;
    }

    /// <summary>
    /// Either of the two choices, written to the registry and to the desktop layer at once. Both are
    /// per-user, so neither needs an administrator and both can be undone from here or by the setup.
    /// </summary>
    private void SetShellMode_Changed(object sender, RoutedEventArgs e)
    {
        // The XAML raises this while it is still being read, and the mirror sets the boxes itself.
        if (_synchronizingShellMode || !IsLoaded || _config is null)
        {
            return;
        }

        if (SetShellReplace.IsChecked == true)
        {
            Services.WindowsIntegration.UseAsShell();
        }
        else
        {
            Services.WindowsIntegration.RunAlongside();
        }

        Services.WindowsIntegration.ApplyDesktopLayerMode(_config);

        if (Application.Current is App app)
        {
            app.ShowDesktopLayer();
        }

        SynchronizeShellMode();
    }

    // ---- the bar's own look ---------------------------------------------

    /// <summary>
    /// The least of the bar the transparency slider is allowed to leave: below this the bar's own text
    /// would sit on the wallpaper with nothing behind it. The slider stops at the same place, which is
    /// 85% see-through, and the paint itself is in <see cref="ApplyBackdrop"/>.
    /// </summary>
    private const double LeastBarOpacity = 0.15;

    /// <summary>
    /// How much of the desktop shows through the bar. What is stored is the bar's opacity, so 1 is the
    /// bar exactly as designed; the slider is its complement, which is why dragging it right makes the
    /// bar see-through. Honouring it is <see cref="ApplyBackdrop"/>'s job.
    /// </summary>
    private void SetBarTransparency_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_synchronizingSettings || !IsLoaded || _config is null)
        {
            return;
        }

        _config.BarOpacity = Math.Round((100 - SetBarTransparency.Value) / 100.0, 2);
        SetBarTransparencyText.Text = $"{SetBarTransparency.Value:0}%";
        _config.Save();
        ApplyBackdrop();
    }

    private void SetIconSize_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // The slider raises this while the XAML is still being read and while the menu is being filled
        // in from the config, and in neither case is there anything to write back.
        if (_synchronizingSettings || !IsLoaded || _config is null)
        {
            return;
        }

        _config.DesktopIconSize = Math.Round(SetIconSize.Value);
        SetIconSizeText.Text = $"{_config.DesktopIconSize:0} px";
        SaveAndApplyDesktop();
    }

    private void SaveAndApplyDesktop()
    {
        _config.Save();
        DesktopSurface()?.ApplySettingsChange();
    }

    private void SettingsArrange_Click(object sender, RoutedEventArgs e)
    {
        DesktopSurface()?.ArrangeIconsFromSettings();
        CloseFlyouts();
    }

    private void SettingsRefresh_Click(object sender, RoutedEventArgs e)
    {
        DesktopSurface()?.RefreshFromSettings();
        CloseFlyouts();
    }

    // ---- the bar's own bits and pieces ----------------------------------

    private void SetAcrylic_Click(object sender, RoutedEventArgs e)
    {
        _config.Acrylic = SetAcrylic.IsChecked == true;
        _config.Save();
        SynchronizeMenuState();
        ApplyBackdrop();
    }

    private void SetSeconds_Click(object sender, RoutedEventArgs e)
    {
        _config.ShowSecondsInClock = SetSeconds.IsChecked == true;
        _config.Save();
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();

        try
        {
            Process.Start(new ProcessStartInfo(App.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Log("Settings.OpenFolder", ex);
        }
    }

    /// <summary>The desktop surface, or null while the layer is switched off.</summary>
    private static DesktopWindow? DesktopSurface() => (Application.Current as App)?.DesktopSurface;
}

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The right-hand status cluster: clock and calendar, network type, volume with its bar,
/// the live throughput monitor and the keyboard language.
/// </summary>
public partial class MainWindow
{
    private const string GlyphVolume = "\uE767";
    private const string GlyphVolumeMuted = "\uE74F";
    private const string GlyphWifi = "\uE701";
    private const string GlyphEthernet = "\uE839";

    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _calendarAnchorDay = DateTime.Today;
    private DateTime _activitySince = DateTime.Now;
    private int _statusTick;
    private bool _suppressVolumeEvents;
    private NetworkStatus? _networkStatus;
    private MenuItem? _rateUnitsBytes;
    private MenuItem? _rateUnitsBits;

    private void InitializeStatus()
    {
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += OnStatusTick;

        ClockPopup.Opened += (_, _) => BuildCalendar();
        BuildCalendar();

        InitializeActivityMenu();

        SampleVolume(force: true);
    }

    private void StartStatusTimer()
    {
        if (!_statusTimer.IsEnabled)
        {
            _statusTimer.Start();
        }
    }

    private void RefreshStatus()
    {
        _activitySince = DateTime.Now;
        RefreshClock();
        RefreshNetwork();
        RefreshVolume();
        RefreshLanguage();
        RefreshActivity();
    }

    private void OnStatusTick(object? sender, EventArgs e)
    {
        _statusTick++;

        try
        {
            RefreshClock();
            RefreshActivity();
            RefreshLanguage();

            // Cheap enough at 1 Hz, but the slower widgets do not need it.
            if (_statusTick % 2 == 0)
            {
                RefreshNetwork();
                RefreshVolume();
            }
        }
        catch (Exception ex)
        {
            App.Log("StatusTick", ex);
        }
    }

    // ---- clock ----------------------------------------------------------

    private void RefreshClock()
    {
        var now = DateTime.Now;

        ClockTime.Text = _config.ShowSecondsInClock ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");
        ClockDate.Text = now.ToString("d", CultureInfo.CurrentCulture);
        ClockButton.ToolTip = now.ToString("F", CultureInfo.CurrentCulture);

        if (ClockPopup.IsOpen)
        {
            ClockPopupTime.Text = now.ToString("HH:mm:ss");
            ClockPopupDate.Text = now.ToString("D", CultureInfo.CurrentCulture);

            // Follow the date rollover, but never yank the user back from another month.
            if (_calendarAnchorDay != DateTime.Today)
            {
                var followsToday = _calendarMonth.Year == _calendarAnchorDay.Year && _calendarMonth.Month == _calendarAnchorDay.Month;
                _calendarAnchorDay = DateTime.Today;
                if (followsToday)
                {
                    BuildCalendar();
                }
            }
        }
    }

    private void Clock_Click(object sender, RoutedEventArgs e)
    {
        RefreshClock();
        BuildCalendar();
        ShowFlyoutRight(ClockPopup, 272, 0);
    }

    private void CalendarPrev_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(-1);
        BuildCalendar();
    }

    private void CalendarNext_Click(object sender, RoutedEventArgs e)
    {
        _calendarMonth = _calendarMonth.AddMonths(1);
        BuildCalendar();
    }

    private void BuildCalendar()
    {
        try
        {
            CalendarGrid.Children.Clear();
            CalendarGrid.ColumnDefinitions.Clear();
            CalendarGrid.RowDefinitions.Clear();

            for (var column = 0; column < 7; column++)
            {
                CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            for (var row = 0; row < 7; row++)
            {
                CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var culture = CultureInfo.CurrentCulture;
            CalendarTitle.Text = _calendarMonth.ToString("Y", culture);

            var firstDayOfWeek = (int)culture.DateTimeFormat.FirstDayOfWeek;
            var offset = ((int)_calendarMonth.DayOfWeek - firstDayOfWeek + 7) % 7;
            var start = _calendarMonth.AddDays(-offset);
            var isCurrentMonth = _calendarMonth.Year == DateTime.Today.Year && _calendarMonth.Month == DateTime.Today.Month;

            for (var column = 0; column < 7; column++)
            {
                var dayName = culture.DateTimeFormat.AbbreviatedDayNames[(firstDayOfWeek + column) % 7];
                if (dayName.Length > 3)
                {
                    dayName = dayName[..3];
                }

                var header = new TextBlock
                {
                    Text = dayName,
                    Style = (Style)FindResource("CaptionDim"),
                    FontSize = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4),
                };

                Grid.SetRow(header, 0);
                Grid.SetColumn(header, column);
                CalendarGrid.Children.Add(header);
            }

            for (var cell = 0; cell < 42; cell++)
            {
                var day = start.AddDays(cell);
                var element = BuildDayCell(day, day.Month == _calendarMonth.Month);
                Grid.SetRow(element, 1 + (cell / 7));
                Grid.SetColumn(element, cell % 7);
                CalendarGrid.Children.Add(element);
            }

            if (!isCurrentMonth)
            {
                // Give the user a way back to today.
                CalendarTitle.ToolTip = Strings.Format("Calendar_Today", DateTime.Today.ToString("Y", culture));
            }
        }
        catch (Exception ex)
        {
            App.Log("Calendar", ex);
        }
    }

    private FrameworkElement BuildDayCell(DateTime day, bool inMonth)
    {
        var isToday = day.Date == DateTime.Today;

        var text = new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.CurrentCulture),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = inMonth
                ? (System.Windows.Media.Brush)FindResource(isToday ? "TextPrimary" : "TextSecondary")
                : (System.Windows.Media.Brush)FindResource("TextFaint"),
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(1),
            Padding = new Thickness(0, 5, 0, 5),
            Background = isToday ? (System.Windows.Media.Brush)FindResource("AccentSoft") : System.Windows.Media.Brushes.Transparent,
            Child = text,
        };

        if (isToday)
        {
            border.BorderBrush = (System.Windows.Media.Brush)FindResource("Accent");
            border.BorderThickness = new Thickness(1);
        }

        return border;
    }

    // ---- network --------------------------------------------------------

    private void RefreshNetwork()
    {
        try
        {
            var status = NetworkProbe.Read();
            _networkStatus = status;

            NetworkGlyph.Text = status.Kind == NetworkKind.Wifi ? GlyphWifi : GlyphEthernet;

            NetworkGlyph.Opacity = status.Kind == NetworkKind.None ? 0.45 : 1.0;
            NetworkLabel.Text = status.Kind == NetworkKind.None ? "offline" : status.Title;

            NetworkButton.ToolTip = status.Kind == NetworkKind.None
                ? Strings.T("Status_NotConnected")
                : $"{status.Detail}{(status.LinkSpeedBitsPerSecond > 0 ? " · " + NetworkProbe.FormatSpeed(status.LinkSpeedBitsPerSecond) : string.Empty)}";

            if (NetworkPopup.IsOpen)
            {
                UpdateNetworkPopup(status);
            }
        }
        catch (Exception ex)
        {
            App.Log("Network", ex);
        }
    }

    private void UpdateNetworkPopup(NetworkStatus status)
    {
        NetworkPopupTitle.Text = status.Kind == NetworkKind.None ? Strings.T("Status_NotConnected") : status.Title;

        var parts = new List<string> { status.Detail };
        if (status.LinkSpeedBitsPerSecond > 0)
        {
            parts.Add(NetworkProbe.FormatSpeed(status.LinkSpeedBitsPerSecond));
        }

        if (status.Addresses.Count > 0)
        {
            parts.Add(status.Addresses[0]);
        }

        parts.Add(status.HasGateway ? "gateway reachable" : "no default gateway");
        NetworkPopupSummary.Text = string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        AdapterList.ItemsSource = status.Adapters;
    }

    // ---- the status buttons' right-click ---------------------------------

    private void Network_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowTroubleshooterMenu(NetworkButton, TroubleshooterTarget.Network);
    }

    private void Volume_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowTroubleshooterMenu(VolumeButton, TroubleshooterTarget.Sound);
    }

    /// <summary>
    /// The one item Windows also puts on these two buttons. Which family of troubleshooter it opens
    /// is DevShell's own setting, so the same menu serves a machine with the classic packs and one
    /// where only the Get Help app is left.
    /// </summary>
    private void ShowTroubleshooterMenu(FrameworkElement anchor, TroubleshooterTarget target)
    {
        CloseFlyouts();

        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            PlacementTarget = anchor,
        };

        var run = new MenuItem { Header = Strings.T("Menu_RunTroubleshooter") };
        run.Click += (_, _) => Troubleshooters.Run(target, _config.UseClassicTroubleshooters);
        menu.Items.Add(run);

        PushAboveFlyout(menu);
        menu.IsOpen = true;
    }

    private void Network_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetwork();

        if (_networkStatus is not null)
        {
            UpdateNetworkPopup(_networkStatus);
        }

        // A fresh list and a fresh join panel every time the flyout opens, and both before the
        // flyout is measured so its height is right the first time.
        RefreshWifi();
        CloseWifiJoin();

        ShowFlyoutRight(NetworkPopup, 400, 0);
    }

    // ---- volume ---------------------------------------------------------

    private bool SampleVolume(bool force)
    {
        if (!_volume.Refresh())
        {
            VolumeGlyph.Text = GlyphVolumeMuted;
            VolumeGlyph.Opacity = 0.45;
            VolumeBar.IsEnabled = false;
            return false;
        }

        var level = _volume.Muted ? 0f : _volume.Level * 100f;
        VolumeGlyph.Text = level <= 0.5f ? GlyphVolumeMuted : GlyphVolume;
        VolumeGlyph.Opacity = 1.0;
        VolumeBar.IsEnabled = true;

        // Do not fight the user while a slider is being dragged.
        var dragging = Mouse.LeftButton == MouseButtonState.Pressed
                       && (VolumeBar.IsMouseOver || VolumePopupSlider.IsMouseOver);

        if (force || !dragging)
        {
            _suppressVolumeEvents = true;
            VolumeBar.Value = level;
            VolumePopupSlider.Value = level;
            _suppressVolumeEvents = false;
        }

        VolumePopupPercent.Text = $"{level:0}%";
        VolumePopupMuteButton.Content = _volume.Muted || level <= 0.5f ? GlyphVolumeMuted : GlyphVolume;
        VolumePopupMuteButton.ToolTip = _volume.Muted ? Strings.T("Audio_Unmute") : Strings.T("Audio_Mute");
        VolumePopupDevice.Text = Strings.T("Audio_DefaultDevice");

        VolumeButton.ToolTip = _volume.Muted
            ? Strings.T("Audio_Muted")
            : Strings.Format("Audio_VolumePercent", level);

        return true;
    }

    private void RefreshVolume()
    {
        try
        {
            SampleVolume(force: false);
        }
        catch (Exception ex)
        {
            App.Log("Volume", ex);
        }
    }

    private void Volume_Click(object sender, RoutedEventArgs e)
    {
        SampleVolume(force: true);
        ShowFlyoutRight(VolumePopup, 300, 0);
    }

    private void VolumeMute_Click(object sender, RoutedEventArgs e)
    {
        _volume.ToggleMute();
        SampleVolume(force: true);
    }

    private void VolumeBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_suppressVolumeEvents)
        {
            ApplyVolume((float)(e.NewValue / 100.0), VolumeBar);
        }
    }

    private void VolumePopupSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_suppressVolumeEvents)
        {
            ApplyVolume((float)(e.NewValue / 100.0), VolumePopupSlider);
        }
    }

    private void ApplyVolume(float level, Slider source)
    {
        try
        {
            level = Math.Clamp(level, 0f, 1f);

            if (!_volume.SetLevel(level))
            {
                return;
            }

            var percent = level * 100f;

            _suppressVolumeEvents = true;
            if (!ReferenceEquals(source, VolumeBar))
            {
                VolumeBar.Value = percent;
            }

            if (!ReferenceEquals(source, VolumePopupSlider))
            {
                VolumePopupSlider.Value = percent;
            }

            _suppressVolumeEvents = false;

            VolumeGlyph.Text = percent <= 0.5f ? GlyphVolumeMuted : GlyphVolume;
            VolumePopupPercent.Text = $"{percent:0}%";
            VolumeButton.ToolTip = $"Volume {percent:0}%";
        }
        catch (Exception ex)
        {
            App.Log("ApplyVolume", ex);
        }
    }

    // ---- activity -------------------------------------------------------

    private void RefreshActivity()
    {
        try
        {
            _monitor.Sample();

            DownRateText.Text = NetworkMonitor.FormatRate(_monitor.DownloadBytesPerSecond, RateUnitsInBits);
            UpRateText.Text = NetworkMonitor.FormatRate(_monitor.UploadBytesPerSecond, RateUnitsInBits);

            RenderSparkline(SparkCanvas, DownloadSpark, UploadSpark, _monitor.DownloadHistory, _monitor.UploadHistory);

            if (ActivityPopup.IsOpen)
            {
                RenderSparkline(ActivityCanvas, ActivityDownloadSpark, ActivityUploadSpark, _monitor.DownloadHistory, _monitor.UploadHistory);

                ActivityDown.Text = $"▼  {NetworkMonitor.FormatRate(_monitor.DownloadBytesPerSecond, RateUnitsInBits)} now · {NetworkMonitor.FormatVolume(_monitor.SessionReceived)} received";
                ActivityUp.Text = $"▲  {NetworkMonitor.FormatRate(_monitor.UploadBytesPerSecond, RateUnitsInBits)} now · {NetworkMonitor.FormatVolume(_monitor.SessionSent)} sent";
                ActivityTotals.Text = $"This session: {NetworkMonitor.FormatVolume(_monitor.SessionReceived)} down, {NetworkMonitor.FormatVolume(_monitor.SessionSent)} up";
                ActivityPeak.Text = $"Peak: {NetworkMonitor.FormatRate((long)Math.Round(_monitor.PeakBytesPerSecond), RateUnitsInBits)}";
                ActivitySince.Text = $"Monitoring since {_activitySince.ToShortTimeString()}";
            }
        }
        catch (Exception ex)
        {
            App.Log("Activity", ex);
        }
    }

    private void Activity_Click(object sender, RoutedEventArgs e)
    {
        RefreshActivity();
        ShowFlyoutRight(ActivityPopup, 392, 0);
    }

    /// <summary>
    /// The monitor's right-click menu: the one choice that changes what its numbers mean. Bytes is
    /// what a file size is quoted in, bits is what a link speed is quoted in, and both are one click
    /// apart here instead of being argued about in a bug report.
    /// </summary>
    private void InitializeActivityMenu()
    {
        _rateUnitsBytes = new MenuItem { Header = Strings.T("Net_RateBytes"), IsCheckable = true };
        _rateUnitsBits = new MenuItem { Header = Strings.T("Net_RateBits"), IsCheckable = true };

        _rateUnitsBytes.Click += (_, _) => SetRateUnits(bits: false);
        _rateUnitsBits.Click += (_, _) => SetRateUnits(bits: true);

        var units = new MenuItem { Header = Strings.T("Net_RateUnits") };
        units.Items.Add(_rateUnitsBytes);
        units.Items.Add(_rateUnitsBits);

        var menu = new ContextMenu();
        menu.Items.Add(units);
        menu.Opened += (_, _) => SynchronizeRateUnitsMenu();
        ActivityButton.ContextMenu = menu;

        // The menu is a topmost window of its own; the same push the Start menu's menus get.
        PushAboveFlyout(menu);
        SynchronizeRateUnitsMenu();
    }

    /// <summary>True while the monitor is set to bits rather than bytes.</summary>
    private bool RateUnitsInBits
        => string.Equals(_config.NetworkRateUnits, "Bits", StringComparison.OrdinalIgnoreCase);

    private void SynchronizeRateUnitsMenu()
    {
        if (_rateUnitsBytes is null || _rateUnitsBits is null)
        {
            return;
        }

        var bits = RateUnitsInBits;
        _rateUnitsBytes.IsChecked = !bits;
        _rateUnitsBits.IsChecked = bits;
    }

    private void SetRateUnits(bool bits)
    {
        _config.NetworkRateUnits = bits ? "Bits" : "Bytes";
        _config.Save();

        SynchronizeRateUnitsMenu();

        // Redraw now rather than on the next tick, so the widget changes the moment the choice is made.
        RefreshActivity();
    }

    private static void RenderSparkline(
        FrameworkElement surface,
        Polyline download,
        Polyline upload,
        IReadOnlyList<double> downloadValues,
        IReadOnlyList<double> uploadValues)
    {
        var width = surface.ActualWidth;
        var height = surface.ActualHeight;

        if (width <= 1 || height <= 1)
        {
            return;
        }

        // A floor keeps an idle connection from drawing noise as a full-scale graph.
        var peak = 8 * 1024.0;
        foreach (var value in downloadValues)
        {
            peak = Math.Max(peak, value);
        }

        foreach (var value in uploadValues)
        {
            peak = Math.Max(peak, value);
        }

        download.Points = BuildPoints(downloadValues, width, height, peak);
        upload.Points = BuildPoints(uploadValues, width, height, peak);
    }

    private static PointCollection BuildPoints(IReadOnlyList<double> values, double width, double height, double peak)
    {
        var points = new PointCollection(values.Count <= 0 ? 0 : values.Count);
        if (values.Count == 0)
        {
            return points;
        }

        var step = values.Count == 1 ? 0 : width / (values.Count - 1);
        var usable = Math.Max(1.0, height - 3.0);

        for (var i = 0; i < values.Count; i++)
        {
            var value = Math.Clamp(values[i], 0, peak);
            var y = height - 1 - (value / peak * usable);
            points.Add(new Point(i * step, Math.Max(1, y)));
        }

        return points;
    }

    // ---- keyboard language ---------------------------------------------

    private void RefreshLanguage()
    {
        try
        {
            var layout = KeyboardLayouts.Current();
            if (!string.Equals(LanguageText.Text, layout.Code, StringComparison.Ordinal))
            {
                LanguageText.Text = layout.Code;
            }

            var installed = KeyboardLayouts.Installed();
            LanguageButton.ToolTip = installed.Count > 1
                ? $"Keyboard language: {layout.Name}\nClick to switch ({installed.Count} installed)"
                : $"Keyboard language: {layout.Name}";
        }
        catch (Exception ex)
        {
            App.Log("KeyboardLanguage", ex);
        }
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        CloseFlyouts();

        if (!KeyboardLayouts.CycleNext())
        {
            // Single layout installed (or the call was refused): nothing to switch to.
            RefreshLanguage();
            return;
        }

        RefreshLanguage();
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>File search: index lifecycle, the query loop and what you can do with a hit.</summary>
public partial class MainWindow
{
    private const int MaxResults = 60;
    private static readonly TimeSpan CacheRefreshAfter = TimeSpan.FromHours(24);

    private FileSearch? _search;
    private DispatcherTimer? _searchDebounce;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _indexCts;
    private Task? _indexTask;
    private string _indexStatus = "not indexed";
    private long _lastSearchMs = -1;

    private void InitializeSearch()
    {
        _search = new FileSearch(_indexer);
        _indexer.Progress += OnIndexProgress;

        ResultsList.ItemTemplate = (DataTemplate)Resources["HitTemplate"];
        AppResultsList.ItemTemplate = (DataTemplate)Resources["AppHitTemplate"];
        ResultsList.SelectionChanged += (_, _) => UpdateSearchStatus();
        AppResultsList.SelectionChanged += (_, _) => UpdateSearchStatus();

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140),
        };
        _searchDebounce.Tick += OnSearchDebounceTick;

        SearchBox.PreviewKeyDown += OnSearchBoxKeyDown;

        // The box searched files before it grew tabs; it still opens on them.
        ShowSearchTab(apps: false);
        UpdateSearchStatus();
    }

    // ---- index lifecycle -------------------------------------------------

    private async void BeginIndexing(bool rebuild)
    {
        try
        {
            if (rebuild && _indexTask is not null)
            {
                // Let the previous pass notice the cancellation before a new one starts.
                CancelIndexing();
                try
                {
                    await _indexTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    App.Log("IndexWait", ex);
                }
            }
            else if (_indexer.IsIndexing)
            {
                return;
            }

            var loadedFromCache = false;
            if (!rebuild && _config.UseIndexCache && _indexer.TryLoadCache())
            {
                loadedFromCache = true;
                _indexStatus = $"loaded from cache ({_indexer.Count:n0} entries)";
                App.LogInfo($"File index restored from cache: {_indexer.Count:n0} entries");
                UpdateSearchStatus();
            }

            if (!rebuild && !_config.IndexOnStartup)
            {
                if (!loadedFromCache)
                {
                    _indexStatus = "startup indexing is off";
                    UpdateSearchStatus();
                }

                return;
            }

            // A cached index is used immediately, then refreshed in the background when stale.
            if (loadedFromCache && !rebuild)
            {
                var age = CacheAge();
                if (age is null || age < CacheRefreshAfter)
                {
                    return;
                }

                _indexStatus = $"cache is {DescribeAge(age.Value)} old - refreshing in the background";
                UpdateSearchStatus();
            }

            _indexCts = new CancellationTokenSource();
            var token = _indexCts.Token;

            var task = _indexer.RebuildAsync(_config, token);
            _indexTask = task;
            await task;

            if (token.IsCancellationRequested)
            {
                return;
            }

            _indexStatus = $"{_indexer.Count:n0} files indexed";
            App.LogInfo($"File index ready: {_indexer.Count:n0} entries");
            UpdateSearchStatus();
            _ = RunSearchNow();
        }
        catch (OperationCanceledException)
        {
            _indexStatus = "indexing cancelled";
            UpdateSearchStatus();
        }
        catch (Exception ex)
        {
            App.Log("Index", ex);
            _indexStatus = "index failed - see log";
            UpdateSearchStatus();
        }
    }

    private static TimeSpan? CacheAge()
    {
        try
        {
            if (!File.Exists(FileIndexer.CachePath))
            {
                return null;
            }

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(FileIndexer.CachePath);
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeAge(TimeSpan age)
        => age.TotalDays >= 1 ? $"{age.TotalDays:0.#} days" : $"{age.TotalHours:0.#} hours";

    private void CancelIndexing()
    {
        try
        {
            _indexCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnIndexProgress(IndexProgress progress)
    {
        // Raised from the indexing thread.
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _indexStatus = progress.Done
                ? $"{progress.Entries:n0} files indexed"
                : $"indexing drive {progress.DriveIndex + 1}/{Math.Max(progress.DriveCount, 1)} · {progress.Entries:n0} files · {Shorten(progress.CurrentFolder)}";

            UpdateSearchStatus();
        }));
    }

    private static string Shorten(string folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return string.Empty;
        }

        return folder.Length <= 48 ? folder : "…" + folder[^47..];
    }

    // ---- query loop ------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var hasText = SearchBox.Text.Length > 0;
        SearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        SearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (!hasText)
        {
            _searchDebounce?.Stop();
            _searchCts?.Cancel();
            ResultsList.ItemsSource = null;
            AppResultsList.ItemsSource = null;
            NoResults.Visibility = Visibility.Collapsed;
            SearchPopup.IsOpen = false;
            UpdateSearchStatus();
            return;
        }

        if (SearchPopup.IsOpen)
        {
            OpenFlyout(SearchPopup, 940, Math.Max(6, OffsetOf(SearchPill)));
        }

        _searchDebounce?.Stop();
        _searchDebounce?.Start();
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (SearchBox.Text.Length > 0)
        {
            OpenFlyout(SearchPopup, 940, Math.Max(6, OffsetOf(SearchPill)));
        }
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        _ = RunSearchNow();
    }

    private async Task RunSearchNow()
    {
        if (_search is null)
        {
            return;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            return;
        }

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<SearchHit> hits;

        try
        {
            hits = await Task.Run(() => _search.Search(query, MaxResults, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Log("Search", ex);
            _indexStatus = "search failed - see log";
            UpdateSearchStatus();
            return;
        }

        stopwatch.Stop();

        if (token.IsCancellationRequested || !string.Equals(SearchBox.Text.Trim(), query, StringComparison.Ordinal))
        {
            return;
        }

        _lastSearchMs = stopwatch.ElapsedMilliseconds;
        ResultsList.ItemsSource = hits;
        ResultsList.SelectedIndex = hits.Count > 0 ? 0 : -1;

        // The app list is a few hundred names, so it is matched right here; the Start menu's own
        // list is loaded on first use, and the tab fills in again when it arrives.
        LoadStartApps();
        RefreshAppResults(query);

        UpdateNoResults(query);

        OpenFlyout(SearchPopup, 940, Math.Max(6, OffsetOf(SearchPill)));
        UpdateSearchStatus();
    }

    /// <summary>Matches the Start menu's app list against a query. Never touches the file index.</summary>
    private void RefreshAppResults(string query)
    {
        var hits = AppSearch.Search(_startApps, query, MaxResults);
        AppResultsList.ItemsSource = hits;
        AppResultsList.SelectedIndex = hits.Count > 0 ? 0 : -1;
    }

    /// <summary>Re-runs the app tab once the Start menu's list has been read for the first time.</summary>
    private void RefreshAppSearch()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            return;
        }

        RefreshAppResults(query);

        if (SearchPopup.IsOpen)
        {
            UpdateNoResults(query);
            UpdateSearchStatus();
        }
    }

    private void UpdateNoResults(string query)
    {
        var count = _searchShowsApps ? AppResultsList.Items.Count : ResultsList.Items.Count;
        if (count > 0)
        {
            NoResults.Visibility = Visibility.Collapsed;
            return;
        }

        NoResults.Visibility = Visibility.Visible;
        NoResults.Text = _searchShowsApps
            ? Strings.Format("Search_NoApp", query) + "."
            : _indexer.Count == 0
                ? Strings.T("Search_NoIndex")
                : Strings.Format("Search_NoMatch", query) + ".";
    }

    private void UpdateSearchStatus()
    {
        var head = _searchShowsApps ? DescribeAppResults() : DescribeFileResults();
        var tail = _indexStatus;

        SearchStatus.Text = string.IsNullOrEmpty(head)
            ? tail
            : string.IsNullOrEmpty(tail) ? head : $"{head} · {tail}";
    }

    private string DescribeAppResults()
    {
        if (AppResultsList.ItemsSource is null)
        {
            return _startApps.Count > 0 ? $"{_startApps.Count:n0} apps known" : string.Empty;
        }

        var count = AppResultsList.Items.Count;
        return $"{count} app result{(count == 1 ? string.Empty : "s")}";
    }

    private string DescribeFileResults()
    {
        var hits = ResultsList.ItemsSource as IReadOnlyList<SearchHit>;
        return hits is null
            ? (_indexer.Count > 0 ? $"{_indexer.Count:n0} files indexed" : string.Empty)
            : $"{hits.Count} result{(hits.Count == 1 ? string.Empty : "s")} in {_lastSearchMs} ms · {_indexer.Count:n0} files indexed";
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.PageDown:
                MoveSelection(8);
                e.Handled = true;
                break;

            case Key.PageUp:
                MoveSelection(-8);
                e.Handled = true;
                break;

            case Key.Enter:
            {
                var asAdministrator = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

                if (_searchShowsApps)
                {
                    if (SelectedApp() is not { } app)
                    {
                        return;
                    }

                    e.Handled = true;
                    OpenAppHit(app, asAdministrator);
                    break;
                }

                if (SelectedHit() is not { } hit)
                {
                    return;
                }

                e.Handled = true;
                OpenHit(hit, asAdministrator);
                break;
            }
        }
    }

    private void MoveSelection(int delta)
    {
        var list = ActiveResults;
        if (list.Items.Count == 0)
        {
            return;
        }

        var index = list.SelectedIndex + delta;
        index = Math.Max(0, Math.Min(list.Items.Count - 1, index));
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
    }

    private SearchHit? SelectedHit()
        => ResultsList.SelectedItem as SearchHit ?? (ResultsList.Items.Count > 0 ? ResultsList.Items[0] as SearchHit : null);

    // ---- acting on a hit -------------------------------------------------

    private static SearchHit? HitFrom(object sender) => sender switch
    {
        FrameworkElement { Tag: SearchHit tagged } => tagged,
        FrameworkElement { DataContext: SearchHit bound } => bound,
        _ => null,
    };

    private void HitOpen_Click(object sender, RoutedEventArgs e)
    {
        if (HitFrom(sender) is { } hit)
        {
            OpenHit(hit, asAdministrator: false);
        }
    }

    private void HitAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (HitFrom(sender) is { } hit)
        {
            OpenHit(hit, asAdministrator: true);
        }
    }

    private void HitLocation_Click(object sender, RoutedEventArgs e)
    {
        if (HitFrom(sender) is not { } hit)
        {
            return;
        }

        CloseFlyouts();
        var target = hit.Entry.IsDirectory ? Directory.GetParent(hit.Entry.Path)?.FullName ?? hit.Entry.Path : hit.Entry.Path;
        ShellActions.OpenFileLocation(target);
    }

    private void HitCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (HitFrom(sender) is not { } hit)
        {
            return;
        }

        try
        {
            Clipboard.SetText(hit.Entry.Path);
        }
        catch (Exception ex)
        {
            App.Log("Clipboard", ex);
        }
    }

    /// <summary>
    /// Opens the row menu by hand instead of leaving it to ContextMenuService: the row lives in
    /// the always-on-top flyout popup, and a menu that WPF opens on its own cannot be pushed
    /// above other windows before it is shown.
    /// </summary>
    private void Hit_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row)
        {
            ShowRowMenu(row, e);
        }
    }

    private void AppHit_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement row)
        {
            ShowRowMenu(row, e);
        }
    }

    private void ShowRowMenu(FrameworkElement row, MouseButtonEventArgs e)
    {
        if (row.ContextMenu is not ContextMenu menu)
        {
            return;
        }

        menu.DataContext = row.DataContext;
        menu.PlacementTarget = row;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;

        // The flyout itself is topmost, so its menu has to be as well.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => WindowEffects.AssertTopmost(menu)));
    }

    private void OpenHit(SearchHit hit, bool asAdministrator)
    {
        CloseFlyouts();

        try
        {
            if (hit.Entry.IsDirectory)
            {
                // "runas" makes no sense for a folder: open it elevated through explorer instead.
                if (asAdministrator)
                {
                    ShellActions.Run("explorer.exe", $"\"{hit.Entry.Path}\"");
                }
                else
                {
                    ShellActions.Open(hit.Entry.Path);
                }

                return;
            }

            if (!asAdministrator)
            {
                ShellActions.Open(hit.Entry.Path);
                return;
            }

            var result = ShellActions.OpenAsAdministrator(hit.Entry.Path, out var error);
            if (result == LaunchResult.Failed && error is not null)
            {
                ShellActions.ShowError(error);
            }
        }
        catch (Exception ex)
        {
            App.Log("OpenHit", ex);
            ShellActions.ShowError(ex.Message);
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedHit() is { } hit)
        {
            OpenHit(hit, asAdministrator: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
    }

    // ---- the Apps tab ----------------------------------------------------

    private bool _searchShowsApps;

    private ListBox ActiveResults => _searchShowsApps ? AppResultsList : ResultsList;

    private void SearchTabApps_Click(object sender, RoutedEventArgs e) => ShowSearchTab(apps: true);

    private void SearchTabFiles_Click(object sender, RoutedEventArgs e) => ShowSearchTab(apps: false);

    /// <summary>
    /// Switches the list under the tabs. The query is shared, so both tabs are matched on every
    /// keystroke and switching one never has to run the file index again.
    /// </summary>
    private void ShowSearchTab(bool apps)
    {
        _searchShowsApps = apps;

        AppResultsList.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
        SearchTabAppsUnderline.Visibility = apps ? Visibility.Visible : Visibility.Collapsed;
        SearchTabFilesUnderline.Visibility = apps ? Visibility.Collapsed : Visibility.Visible;
        SearchTabAppsButton.Opacity = apps ? 1.0 : 0.6;
        SearchTabFilesButton.Opacity = apps ? 0.6 : 1.0;

        var list = ActiveResults;
        if (list.SelectedIndex < 0 && list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            UpdateNoResults(query);
        }

        UpdateSearchStatus();
    }

    private static AppHit? AppFrom(object sender) => sender switch
    {
        FrameworkElement { Tag: AppHit tagged } => tagged,
        FrameworkElement { DataContext: AppHit bound } => bound,
        _ => null,
    };

    private AppHit? SelectedApp()
        => AppResultsList.SelectedItem as AppHit
           ?? (AppResultsList.Items.Count > 0 ? AppResultsList.Items[0] as AppHit : null);

    private void AppHitOpen_Click(object sender, RoutedEventArgs e)
    {
        if (AppFrom(sender) is { } hit)
        {
            OpenAppHit(hit, asAdministrator: false);
        }
    }

    private void AppHitAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (AppFrom(sender) is { } hit)
        {
            OpenAppHit(hit, asAdministrator: true);
        }
    }

    private void AppHitLocation_Click(object sender, RoutedEventArgs e)
    {
        if (AppFrom(sender) is not { } hit)
        {
            return;
        }

        CloseFlyouts();
        StartMenuApps.OpenLocation(hit.App);
    }

    private void AppHitCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (AppFrom(sender) is not { } hit)
        {
            return;
        }

        try
        {
            Clipboard.SetText(hit.App.ShortcutPath);
        }
        catch (Exception ex)
        {
            App.Log("Clipboard", ex);
        }
    }

    private void AppResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedApp() is { } hit)
        {
            OpenAppHit(hit, asAdministrator: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        }
    }

    private void OpenAppHit(AppHit hit, bool asAdministrator)
    {
        CloseFlyouts();

        try
        {
            if (!asAdministrator)
            {
                StartMenuApps.Launch(hit.App);
                return;
            }

            var result = StartMenuApps.LaunchAsAdministrator(hit.App, out var error);
            if (result == LaunchResult.Failed && error is not null)
            {
                ShellActions.ShowError(error);
            }
        }
        catch (Exception ex)
        {
            App.Log("OpenAppHit", ex);
            ShellActions.ShowError(ex.Message);
        }
    }

    private void CancelSearch()
    {        try
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        _searchCts = null;
        _searchDebounce?.Stop();
    }
}

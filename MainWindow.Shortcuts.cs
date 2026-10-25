using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using DevShell.Interop;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The Windows-key combinations the bar answers itself.
/// </summary>
/// <remarks>
/// Every one of these belongs to Explorer: it is the shell that maps Win+R onto its Run dialog, Win+E
/// onto a file window, Win+D onto the desktop peek, Win+I onto Settings and Win+X onto the Start
/// button's menu. A machine whose shell is DevShell has nobody to ask - the key arrives, the system
/// finds no shell of its own, and the combination goes nowhere - so the keyboard hook offers DevShell
/// any bare Win+key chord while it is the shell and this is the list it answers.
///
/// A chord that is not here is left alone, which is what keeps Win+L, Win+Ctrl+arrow, Win+Shift+S, the
/// snapping combinations and the rest exactly where they were: with Windows.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Virtual-key codes of the letters taken off the system's hands.</summary>
    private const int VkD = 0x44;
    private const int VkE = 0x45;
    private const int VkI = 0x49;
    private const int VkR = 0x52;
    private const int VkS = 0x53;
    private const int VkX = 0x58;

    /// <summary>The windows Win+D put away, so that the key can bring those same ones back.</summary>
    private readonly List<IntPtr> _peekedAway = [];

    /// <summary>
    /// Answers the chord when it is one of ours. Called from inside the keyboard hook on the UI thread,
    /// so the answer has to be reached without waiting for anything: the work itself is posted.
    /// </summary>
    private bool OnWindowsKeyChord(int vk)
    {
        if (!WindowsIntegration.IsDevShellTheShell())
        {
            // Beside Explorer the hook hands chords back to Windows instead of asking here.
            return false;
        }

        switch (vk)
        {
            case VkR: Answer(ShowRunDialog, "Win+R", "the Run dialog"); return true;
            case VkE: Answer(OpenFileWindow, "Win+E", "a file window"); return true;
            case VkD: Answer(ToggleDesktopPeek, "Win+D", "the desktop"); return true;
            case VkI: Answer(OpenWindowsSettings, "Win+I", "Settings"); return true;
            case VkS: Answer(FocusSearchBox, "Win+S", "the search box"); return true;
            case VkX: Answer(ShowStartButtonMenuFromKey, "Win+X", "the Start button menu"); return true;
            default: return false;
        }
    }

    private void Answer(Action action, string chord, string what)
    {
        // One line per answered chord, so the log says what the key did rather than only that it fired.
        App.LogInfo($"Windows key: {chord} opens {what}");
        Dispatcher.BeginInvoke(action);
    }

    /// <summary>Win+R: shell32's own Run dialog, the one Explorer shows for this key.</summary>
    private static void ShowRunDialog() => ShellActions.ShowRunDialog();

    /// <summary>Win+E: a file window, with "This PC" named explicitly so no second desktop starts.</summary>
    private static void OpenFileWindow() => ShellActions.OpenExplorerWindow(null);

    private static void OpenWindowsSettings()
    {
        if (ShellActions.Open("ms-settings:") == LaunchResult.Failed)
        {
            ShellActions.ShowError(Strings.T("Msg_SettingsFailed"));
        }
    }

    /// <summary>Win+S: the bar's search takes the keyboard, which opens its results above it.</summary>
    private void FocusSearchBox()
    {
        BringBarBackForUser();
        BringBarToFront();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    /// <summary>Win+X: the Start button's menu, put up above the button as Windows puts it up.</summary>
    private void ShowStartButtonMenuFromKey()
    {
        BringBarBackForUser();
        BringBarToFront();
        ShowStartButtonMenu(PlacementMode.Top);
    }

    /// <summary>
    /// Win+D: every application's windows are folded away, and a second press brings exactly those
    /// back. Windows' own peek is a toggle for the same reason - that is the only way back to the
    /// windows that were up before.
    /// </summary>
    private void ToggleDesktopPeek()
    {
        try
        {
            if (_peekedAway.Count > 0)
            {
                foreach (var window in _peekedAway)
                {
                    if (NativeMethods.IsWindow(window) && NativeMethods.IsIconic(window))
                    {
                        NativeMethods.ShowWindowAsync(window, NativeMethods.SW_RESTORE);
                    }
                }

                _peekedAway.Clear();
                return;
            }

            foreach (var window in AppWindows())
            {
                WindowActivation.Minimize(window);
                _peekedAway.Add(window);
            }
        }
        catch (Exception ex)
        {
            App.Log("MainWindow.ToggleDesktopPeek", ex);
            _peekedAway.Clear();
        }
    }

    /// <summary>
    /// Every top-level window that counts as an application of its own: the same rule the taskbar uses
    /// for its buttons - visible, titled, not a tool window, owned by nobody else - without the icons
    /// this time, because the peek wants the windows and nothing else.
    /// </summary>
    private static List<IntPtr> AppWindows()
    {
        var windows = new List<IntPtr>();
        var self = (uint)Environment.ProcessId;

        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)
                || NativeMethods.IsIconic(window)
                || NativeMethods.GetWindowTextLength(window) == 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || processId == self)
            {
                // The bar, its flyouts and the desktop layer are the shell rather than one of its tasks.
                return true;
            }

            var exStyle = (uint)NativeMethods.GetWindowLong(window, NativeMethods.GWL_EXSTYLE);

            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0
                && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            {
                return true;
            }

            if (NativeMethods.GetWindow(window, NativeMethods.GW_OWNER) != IntPtr.Zero
                && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
            {
                return true;
            }

            windows.Add(window);
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// Hands the bar the foreground before it puts a flyout or a menu up. A click does that for itself,
    /// a key does not: a flyout opened from the background is a window of a background process, and
    /// Windows keeps whatever the user was working in in front of it.
    /// </summary>
    private void BringBarToFront()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;

            if (handle != IntPtr.Zero)
            {
                WindowActivation.BringToFront(handle);
            }
        }
        catch (Exception ex)
        {
            App.Log("MainWindow.BringBarToFront", ex);
        }
    }
}

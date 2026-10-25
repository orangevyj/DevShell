using System.IO;
using System.Windows;
using System.Windows.Input;

namespace DevShell;

/// <summary>
/// Copy and paste on the desktop, the way Explorer does it: the icon's file goes on the clipboard
/// as a file drop plus the copy intent, so pasting works in Explorer too - and pasting from Explorer
/// works here, including the move that a cut puts on the clipboard.
/// </summary>
public partial class DesktopWindow
{
    /// <summary>The clipboard format Explorer uses to say whether a file drop means copy or move.</summary>
    private const string PreferredDropEffectFormat = "Preferred DropEffect";

    private const int DropEffectCopy = 1;
    private const int DropEffectMove = 2;
    private const int DropEffectCopyMove = 5;

    private void Surface_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F2 renames the selected item, the shortcut Explorer has always used for it.
        if (e.Key == Key.F2 && RenameSelected())
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && CtrlHeld)
        {
            SelectAllIcons();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _selection.Count > 0)
        {
            ClearSelection();
            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.C:
                e.Handled = CopySelection();
                break;

            case Key.V:
                e.Handled = PasteIntoDesktop();
                break;
        }
    }

    /// <summary>Puts the selected icons on the clipboard as copies, the way Ctrl+C does in Explorer.</summary>
    private bool CopySelection()
    {
        var paths = _selection.Select(icon => icon.Item.Path).OfType<string>().ToArray();
        if (paths.Length == 0)
        {
            return false;
        }

        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, paths);
            data.SetData(PreferredDropEffectFormat, DropEffectStream(DropEffectCopy));

            Clipboard.SetDataObject(data, copy: true);
            App.LogInfo(paths.Length == 1
                ? $"Desktop: '{Path.GetFileName(paths[0])}' copied to the clipboard"
                : $"Desktop: {paths.Length} items copied to the clipboard");
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Desktop.Copy", ex);
            return false;
        }
    }

    /// <summary>
    /// Copies whatever files the clipboard holds into the Desktop folder. A cut - ours or Explorer's -
    /// carries the move intent, and then the files are moved rather than copied.
    /// </summary>
    private bool PasteIntoDesktop()
    {
        try
        {
            if (FilesIn(Clipboard.GetDataObject()) is not { Length: > 0 } files)
            {
                return false;
            }

            var move = DropEffectOf(Clipboard.GetDataObject()) == DropEffectMove;
            var added = Transfer(files, PersonalDesktopFolder, copy: !move, at: null);

            if (added > 0)
            {
                App.LogInfo($"Desktop: pasted {added} item(s) in from the clipboard");
                _ = RefreshIconsAsync();
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log("Desktop.Paste", ex);
            return false;
        }
    }

    /// <summary>The four bytes Explorer writes for a copy/move intent.</summary>
    private static MemoryStream DropEffectStream(int effect) => new(BitConverter.GetBytes(effect));

    private static int DropEffectOf(IDataObject? data)
    {
        if (data is null || !data.GetDataPresent(PreferredDropEffectFormat))
        {
            return DropEffectCopy;
        }

        if (data.GetData(PreferredDropEffectFormat) is not Stream stream)
        {
            return DropEffectCopy;
        }

        try
        {
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }

            var buffer = new byte[4];
            return stream.Read(buffer, 0, buffer.Length) == buffer.Length
                ? BitConverter.ToInt32(buffer, 0)
                : DropEffectCopy;
        }
        catch (Exception ex)
        {
            App.Log("Desktop.DropEffect", ex);
            return DropEffectCopy;
        }
    }
}

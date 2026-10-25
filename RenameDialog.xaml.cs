using System.IO;
using System.Windows;
using System.Windows.Input;
using DevShell.Services;

namespace DevShell;

/// <summary>
/// The box that asks for a new name, built like DevShell's own flyouts rather than like a message
/// box: it opens with the current name in it and, for a file, only the part before the extension is
/// selected, so typing replaces the name and keeps the file's type - the way Explorer's rename does.
/// </summary>
public partial class RenameDialog : Window
{
    private readonly string _path;

    public RenameDialog(string path)
    {
        InitializeComponent();

        _path = path;

        var current = Path.GetFileName(path);
        NewName = current;

        PromptText.Text = Strings.Format("Dlg_RenamePrompt", current);
        NameBox.Text = current;
        NameBox.Select(0, NamePartLength(current));

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>The name the user typed. Only meaningful when the dialog was accepted.</summary>
    public string NewName { get; private set; }

    /// <summary>
    /// The rules Windows itself enforces, checked before the file is moved so the reason can be
    /// shown in the box instead of in an error after the fact.
    /// </summary>
    /// <returns>The reason the name cannot be used, or null when it can.</returns>
    public static string? Validate(string path, string candidate)
    {
        if (candidate.Length == 0)
        {
            return Strings.T("Dlg_NameRequired");
        }

        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Strings.T("Dlg_NameInvalidChars");
        }

        if (candidate != candidate.TrimEnd(' ', '.'))
        {
            return Strings.T("Dlg_NameTrailing");
        }

        // The same item - either the name did not change at all, or only its case did, which Windows
        // treats as a rename - so a name that is "already taken" can only be a different item.
        if (string.Equals(candidate, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
        {
            var target = Path.Combine(folder, candidate);
            if (File.Exists(target) || Directory.Exists(target))
            {
                return Strings.Format("Dlg_NameTaken", candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Explorer selects the name but leaves the extension out of the selection, so a plain rename
    /// keeps the type. A folder, or a file with no extension, is selected in full.
    /// </summary>
    private static int NamePartLength(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length == 0 ? name.Length : name.Length - extension.Length;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var candidate = NameBox.Text.Trim();

        if (Validate(_path, candidate) is { } reason)
        {
            ErrorText.Text = reason;
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }

        NewName = candidate;
        DialogResult = true;
    }

    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}

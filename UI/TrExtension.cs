using System.Windows.Data;
using System.Windows.Markup;
using DevShell.Services;

namespace DevShell.UI;

/// <summary>
/// Shorthand for a translated text in XAML: <c>Text="{l:Tr Bar_Start}"</c>. It expands to a one-way
/// binding against <see cref="Strings.Instance"/>, so changing the language in the settings popup
/// updates every label in place - no restart of the shell, which matters because DevShell is one.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Strings.Instance,
            Mode = BindingMode.OneWay,
        };

        return binding.ProvideValue(serviceProvider);
    }
}

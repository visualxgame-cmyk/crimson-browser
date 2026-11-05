using System.Windows.Data;
using System.Windows.Markup;

namespace CrimsonBrowser.Services;

/// <summary>
/// XAML markup extension used as <c>{loc:Loc Key}</c>. It returns a one-way
/// binding to the localization indexer, so every element updates automatically
/// when <see cref="LocalizationService.CurrentLanguage"/> changes.
/// </summary>
[MarkupExtensionReturnType(typeof(Binding))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        string key = Key;

        // Bind to LocalizationService.Instance[key]; the service raises
        // PropertyChanged("Item[]") on language changes, refreshing this binding.
        var binding = new Binding($"[{key}]")
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            FallbackValue = key
        };

        return binding.ProvideValue(serviceProvider);
    }
}

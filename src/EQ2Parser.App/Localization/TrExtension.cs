using System.Windows.Markup;

namespace EQ2Parser.App.Localization;

/// <summary>XAML lookup: <c>Text="{loc:Tr Settings_Rate}"</c>. Resolves
/// once at XAML load — language changes apply on restart.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}

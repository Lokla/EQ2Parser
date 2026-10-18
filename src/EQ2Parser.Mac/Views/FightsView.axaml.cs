using Avalonia.Controls;
using Avalonia.Input;
using EQ2Parser.Mac.ViewModels;

namespace EQ2Parser.Mac.Views;

public sealed partial class FightsView : UserControl
{
    public FightsView() => InitializeComponent();

    private FightsViewModel? Vm => DataContext as FightsViewModel;

    /// <summary>Double-click a combatant: open the drill on them.</summary>
    private void OnCombatantDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: CombatantRowVm row } && row.Key.Length > 0)
            Vm?.OpenDrill(row.Key);
    }

    /// <summary>Double-click a bucket or ability: one level deeper.</summary>
    private void OnAbilityDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: AbilityRowVm row })
            Vm?.DrillInto(row);
    }
}

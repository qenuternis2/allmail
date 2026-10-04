using System.Windows.Controls;
using ProtonProfiles.Core.Model;

namespace ProtonProfiles.App.Dialogs;

public sealed record GroupChoice(Guid? Id, string Label, bool All = false)
{
    public override string ToString() => Label;
    public static ComboBox Picker(IReadOnlyList<ProfileGroup> groups, Guid? selected = null)
    {
        var choices = new[] { new GroupChoice(null, "Без группы") }.Concat(groups.Select(g => new GroupChoice(g.Id, g.Name))).ToArray();
        return new ComboBox { ItemsSource = choices, SelectedItem = choices.FirstOrDefault(g => g.Id == selected) ?? choices[0] };
    }
}

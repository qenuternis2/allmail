using System.Windows;
using System.Windows.Controls;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Persistence;

namespace ProtonProfiles.App.Dialogs;

public sealed class ProfileGroupsWindow : Window
{
    public ProfileGroupsWindow(Window owner, IProfileRepository repository)
    {
        Owner = owner; Title = "Группы — All Mails"; Width = 460; Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var root = new DockPanel { Margin = new Thickness(24) };
        var controls = new StackPanel(); DockPanel.SetDock(controls, Dock.Bottom); root.Children.Add(controls);
        var name = new TextBox { MaxLength = 80, Margin = new Thickness(0, 8, 0, 8) };
        controls.Children.Add(new TextBlock { Text = "Название группы" }); controls.Children.Add(name);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkRed };
        controls.Children.Add(error);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) }; controls.Children.Add(buttons);
        var list = new ListBox { DisplayMemberPath = nameof(ProfileGroup.Name) }; root.Children.Add(list);
        void Refresh(Guid? selected = null) {
            list.ItemsSource = repository.ListGroups();
            list.SelectedItem = list.Items.Cast<ProfileGroup>().FirstOrDefault(g => g.Id == selected);
        }
        void Add(string caption, Action action) {
            var button = new Button { Content = caption, Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => { try { error.Text = ""; action(); } catch (ArgumentException e) { error.Text = e.Message; } };
            buttons.Children.Add(button);
        }
        Add("Создать", () => Refresh(repository.CreateGroup(name.Text).Id));
        Add("Переименовать", () => { if (list.SelectedItem is ProfileGroup g) { repository.RenameGroup(g.Id, name.Text); Refresh(g.Id); } });
        Add("Удалить", () => {
            if (list.SelectedItem is not ProfileGroup g) return;
            if (ChoiceDialog.Show(this, "Удалить группу", $"Удалить группу «{g.Name}»? Профили останутся в приложении без группы.", ["Удалить группу", "Отмена"], 1, 1) != 0) return;
            repository.DeleteGroup(g.Id); Refresh(); name.Clear();
        });
        Add("Закрыть", Close);
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is ProfileGroup g) name.Text = g.Name; };
        Content = root; Refresh();
    }

    public static bool Choose(Window owner, IReadOnlyList<ProfileGroup> groups, int count, Guid? current, out Guid? selected)
    {
        var dialog = new Window { Owner = owner, Title = "Переместить в группу — All Mails", Width = 420,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize };
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = $"Выбрано профилей: {count}. Группа:" });
        var picker = GroupChoice.Picker(groups, current); picker.Margin = new Thickness(0, 8, 0, 12); root.Children.Add(picker);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "Применить", IsDefault = true }; apply.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(apply); buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true }); root.Children.Add(buttons); dialog.Content = root;
        var accepted = dialog.ShowDialog() == true; selected = (picker.SelectedItem as GroupChoice)?.Id; return accepted;
    }
}

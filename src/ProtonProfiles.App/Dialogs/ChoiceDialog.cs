using System.Windows;
using System.Windows.Controls;
using ProtonProfiles.App.Controls;

namespace ProtonProfiles.App.Dialogs;

/// <summary>Small Russian-language modal with explicit buttons. Returns the chosen index or null when closed.</summary>
public static class ChoiceDialog
{
    public static int? Show(Window? owner, string title, string message, IReadOnlyList<string> buttons, int? defaultIndex = null, int? cancelIndex = null)
    {
        int? result = null;
        var window = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = 640,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), MaxWidth = 600 });
        var row = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = new Button { Content = buttons[i], Margin = new Thickness(6, 4, 0, 0), IsDefault = defaultIndex == i, IsCancel = cancelIndex == i };
            b.Click += (_, _) => { result = index; window.DialogResult = true; };
            row.Children.Add(b);
        }
        root.Children.Add(row);
        window.Content = root;
        window.ShowDialog();
        return result;
    }

    public static string? Prompt(Window? owner, string title, string message, bool password = false)
    {
        string? value = null;
        var window = new Window
        {
            Title = title, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var root = new StackPanel { Margin = new Thickness(24), MinWidth = 360 };
        Control input = password ? new PasswordBox() : new TextBox();
        var label = UiAccessibility.LabelFor(message, input); label.Margin = new Thickness(0, 0, 0, 8);
        root.Children.Add(label);
        root.Children.Add(input);
        var ok = new Button { Content = "ОК", IsDefault = true, Margin = new Thickness(0, 12, 6, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => { value = input is PasswordBox pb ? pb.Password : ((TextBox)input).Text; window.DialogResult = true; };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok);
        row.Children.Add(cancel);
        root.Children.Add(row);
        window.Content = root;
        window.Loaded += (_, _) => input.Focus();
        return window.ShowDialog() == true ? value : null;
    }
}

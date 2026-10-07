using System.Windows;
using System.Windows.Controls;
using ProtonProfiles.Core.Model;
using ProtonProfiles.Core.Navigation;
using ProtonProfiles.Core.Validation;

namespace ProtonProfiles.App.Dialogs;

public sealed class NewProfileWindow : Window
{
    public ProfileConfig? Result { get; private set; }
    public Guid? GroupId { get; private set; }

    public NewProfileWindow(Window owner, string color, IReadOnlyList<ProfileGroup>? groups = null, Guid? selectedGroup = null)
    {
        Owner = owner; Title = "Новый профиль — SecureBrowser"; Width = 540;
        SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        MaxHeight = Math.Max(240, SystemParameters.WorkArea.Height - 40);
        var layout = new DockPanel { Margin = new Thickness(28) };
        var root = new StackPanel();
        root.Children.Add(new TextBlock {Text="Новое пространство", FontSize=24, FontWeight=FontWeights.SemiBold});
        root.Children.Add(new TextBlock {Text="Свои вкладки, настройки и отдельная сессия.", Foreground=System.Windows.Media.Brushes.DimGray, Margin=new Thickness(0,6,0,8)});
        var name = new TextBox(); var label = new TextBox();
        var url = new TextBox {Text = NavigationPolicy.StartPage.AbsoluteUri};
        void Add(string caption, TextBox input)
        {
            root.Children.Add(new TextBlock {Text=caption,Margin=new Thickness(0,14,0,6)});
            root.Children.Add(input);
        }
        Add("Название профиля",name);
        Add("Метка адреса (необязательно)",label);
        Add("Начальный URL (HTTP/HTTPS)",url);
        root.Children.Add(new TextBlock {Text="Группа",Margin=new Thickness(0,14,0,6)});
        var group = GroupChoice.Picker(groups ?? [], selectedGroup); root.Children.Add(group);
        var error = new TextBlock {TextWrapping=TextWrapping.Wrap,Foreground=System.Windows.Media.Brushes.DarkRed,Margin=new Thickness(0,8,0,0)};
        root.Children.Add(error);
        var buttons = new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        var create = new Button {Content="Создать профиль",IsDefault=true,Style=(Style)FindResource("PrimaryButton")};
        buttons.Children.Add(create); buttons.Children.Add(new Button {Content="Отмена",IsCancel=true});
        // Keep actions reachable when DPI or the available height reduces the
        // viewport; only the fields scroll, rather than the entire dialog.
        DockPanel.SetDock(buttons, Dock.Bottom); layout.Children.Add(buttons);
        layout.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content=layout;
        create.Click += (_,_) => {
            ProfileConfig profile;
            try { profile = ProfileStartPage.WithUrl(new ProfileConfig {Id=Guid.NewGuid(),DisplayName=name.Text.Trim(),
                EmailLabel=string.IsNullOrWhiteSpace(label.Text)?null:label.Text.Trim(),Color=color},url.Text); }
            catch (ArgumentException e) {error.Text=e.Message;error.BringIntoView();return;}
            var errors=ProfileValidator.Validate(profile);
            if (errors.Count>0) {error.Text=string.Join("\n",errors);error.BringIntoView();return;}
            Result=profile; GroupId=(group.SelectedItem as GroupChoice)?.Id; DialogResult=true;
        };
        Loaded += (_,_) => name.Focus();
    }
}

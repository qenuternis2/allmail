using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ProtonProfiles.App.Controls;

internal static class UiAccessibility
{
    public static Label LabelFor(string caption, Control target)
    {
        var label = new Label { Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap },
            Target = target, Padding = new Thickness(0) };
        AutomationProperties.SetName(label, caption);
        AutomationProperties.SetLabeledBy(target, label);
        AutomationProperties.SetName(target, caption);
        return label;
    }

    // Announce state transitions, never every byte/progress tick. The visible
    // text stays independent so the live region can also be a panel heading.
    public static void Announce(FrameworkElement region, string message)
    {
        AutomationProperties.SetLiveSetting(region, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(region, message);
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            (UIElementAutomationPeer.FromElement(region) ?? UIElementAutomationPeer.CreatePeerForElement(region))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}

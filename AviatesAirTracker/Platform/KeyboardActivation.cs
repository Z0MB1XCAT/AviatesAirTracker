using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Collections.Concurrent;
using System.Reflection;
using Windows.System;

namespace AviatesAirTracker.Platform;

/// <summary>
/// Keyboard support for this app's Border-as-button controls. The app avoids WinUI's Button (its
/// template can't be restyled here without a Style element, which crashes this SDK's XAML
/// compiler), so its clickable surfaces only handled Tapped: no Tab focus, no Enter/Space, and
/// nothing for a screen reader to announce.
///
/// In XAML each such element now carries IsTabStop, UseSystemFocusVisuals, an
/// AutomationProperties.Name, KeyDown="OnActivateKey", and its Tapped handler's name as its
/// AutomationProperties.AutomationId. Each page's one-line OnActivateKey calls <see cref="Handle"/>,
/// which runs that same Tapped handler, so the mouse and the keyboard can never do different
/// things.
/// </summary>
public static class KeyboardActivation
{
    private static readonly ConcurrentDictionary<(Type, string), MethodInfo?> Handlers = new();

    public static bool IsActivationKey(VirtualKey key) =>
        key is VirtualKey.Enter or VirtualKey.Space or VirtualKey.GamepadA;

    /// <summary>Runs the Tapped handler named by the sender's AutomationId on Enter/Space.</summary>
    public static void Handle(Page page, object sender, KeyRoutedEventArgs e)
    {
        if (!IsActivationKey(e.Key) || sender is not UIElement element) return;

        var name = AutomationProperties.GetAutomationId(element);
        if (string.IsNullOrEmpty(name)) return;

        var method = Handlers.GetOrAdd((page.GetType(), name), key => key.Item1.GetMethod(key.Item2,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(object), typeof(TappedRoutedEventArgs)]));
        if (method == null) return;

        e.Handled = true;
        method.Invoke(page, [sender, new TappedRoutedEventArgs()]);
    }

    /// <summary>For clickable elements built in code: click, Enter or Space runs
    /// <paramref name="onActivate"/>, it takes focus, and screen readers read <paramref name="name"/>.</summary>
    public static T Enable<T>(T element, Action onActivate, string name) where T : UIElement
    {
        element.IsTabStop = true;
        element.UseSystemFocusVisuals = true;
        AutomationProperties.SetName(element, name);
        element.KeyDown += (_, e) =>
        {
            if (!IsActivationKey(e.Key)) return;
            e.Handled = true;
            onActivate();
        };
        return element;
    }
}

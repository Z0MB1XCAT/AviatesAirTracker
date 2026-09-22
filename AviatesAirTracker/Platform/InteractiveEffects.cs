using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace AviatesAirTracker.Platform;

// ============================================================
// INTERACTIVE EFFECTS
//
// A small reusable hover/press animation for this app's Border-not-Button
// clickable surfaces (the whole app avoids WinUI's Button control so its
// native template can't be retemplated without a <Style> element, which
// silently crashes XamlCompiler.exe on this SDK -- see CLAUDE.md). Every
// plain Tapped-wired Border in the app looks static; this gives them the
// same hover/press "alive" feeling a real Button gets for free.
//
// Uses the Composition API's native spring animation (Compositor.
// CreateSpringVector3Animation) directly on the element's Visual.Scale --
// matches the Frontend Design Rules' "only animate transform and opacity,
// spring-style easing" exactly, and needs no Storyboard/RenderTransform
// wiring. Deliberately scales ONLY (not Opacity): many buttons already
// drive UIElement.Opacity themselves for enabled/disabled dimming (e.g.
// SettingsPage's Check for Updates/Save buttons), and that DP maps onto
// the same underlying Visual -- animating Opacity here too would fight
// those existing writes.
// ============================================================

public static class InteractiveEffects
{
    private const float HoverScale = 1.03f;
    private const float PressScale = 0.96f;
    private const float IdleScale  = 1.0f;

    /// <summary>Wires hover (pointer enter/exit) and press (pointer down/up) scale-spring
    /// animations onto the given element. Safe to call multiple times on the same element only
    /// once -- callers should attach exactly once per element, typically right after
    /// InitializeComponent().</summary>
    public static void Attach(UIElement element)
    {
        element.PointerEntered += (_, _) => AnimateScale(element, HoverScale);
        element.PointerExited += (_, _) => AnimateScale(element, IdleScale);
        element.PointerPressed += (_, _) => AnimateScale(element, PressScale);
        element.PointerReleased += (_, _) => AnimateScale(element, HoverScale);
        element.PointerCanceled += (_, _) => AnimateScale(element, IdleScale);
        element.PointerCaptureLost += (_, _) => AnimateScale(element, IdleScale);
    }

    /// <summary>Convenience overload for attaching to several elements at once.</summary>
    public static void Attach(params UIElement[] elements)
    {
        foreach (var element in elements)
            Attach(element);
    }

    private static void AnimateScale(UIElement element, float target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var size = element.ActualSize;
        visual.CenterPoint = new Vector3(size.X / 2f, size.Y / 2f, 0f);

        var compositor = visual.Compositor;
        var spring = compositor.CreateSpringVector3Animation();
        spring.FinalValue = new Vector3(target, target, 1f);
        spring.DampingRatio = 0.6f;
        spring.Period = TimeSpan.FromMilliseconds(40);
        visual.StartAnimation("Scale", spring);
    }
}

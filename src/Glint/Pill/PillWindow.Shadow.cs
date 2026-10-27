using System.ComponentModel;
using System.Windows.Controls;
using Glint.Core;

namespace Glint.Pill;

// ───────────── Shadow ─────────────
// A ring shadow (see SoftShadow) instead of a blur effect, so it can stay on while the shell animates.

public partial class PillWindow
{
    bool _shadowFull;

    void BuildShadow()
    {
        // Flashes and agent cards change the corners without touching the shadow; follow them.
        DependencyPropertyDescriptor.FromProperty(Border.CornerRadiusProperty, typeof(Border))
            .AddValueChanged(Shell, (_, _) => LayoutShadow());
        SetShadow(full: false);
    }

    /// <summary>
    /// A layered window takes clicks wherever alpha is non-zero, so a wide shadow around the small pill
    /// would swallow clicks on browser tabs underneath. Keep it tight unless the panel is open.
    /// </summary>
    void SetShadow(bool full)
    {
        _shadowFull = full;
        LayoutShadow();
    }

    void LayoutShadow()
    {
        if (_shadowFull) SoftShadow.Fill(ShadowRings, Shell.CornerRadius.TopLeft, rings: 10, depth: 5, strength: 0.30);
        else SoftShadow.Fill(ShadowRings, Shell.CornerRadius.TopLeft, rings: 4, depth: 1.5, strength: 0.26);
    }
}

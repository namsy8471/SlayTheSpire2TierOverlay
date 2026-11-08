using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace Sts2Overlay.Bridge.Badges;

/// <summary>
/// Builds the small tier badge drawn on a card holder.
/// Only built-in Godot nodes (PanelContainer + Label) are used: the mod DLL is loaded without Godot
/// script registration, so custom Node subclasses may not get their virtual callbacks.
/// </summary>
internal static class TierBadge
{
    public const string NodeName = "Sts2OverlayTierBadge";

    /// <summary>Global F8 toggle. Badges already on screen are updated by <see cref="SetAllVisible"/>.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly List<WeakReference<Control>> Live = new();

    /// <summary>
    /// SPIKE ONLY: a stand-in tier from rarity, so we can see per-card data reach the badge.
    /// M3 replaces this with the data pack / app scores.
    /// </summary>
    public static string DemoTier(CardModel card) => card.Rarity switch
    {
        CardRarity.Rare => "S",
        CardRarity.Uncommon => "A",
        CardRarity.Common => "B",
        _ => "C",
    };

    public static Color TierColor(string tier) => tier switch
    {
        "S" => new Color("d4af37"), // gold
        "A" => new Color("9b59b6"), // purple
        "B" => new Color("3498db"), // blue
        "C" => new Color("7f8c8d"), // grey
        _ => new Color("8b1e1e"),   // dark red (D)
    };

    /// <summary>Adds (or replaces) a badge on <paramref name="holder"/> at <paramref name="topRight"/>.</summary>
    public static Control Attach(Control holder, string tier, Vector2 topRight)
    {
        RemoveFrom(holder);

        var style = new StyleBoxFlat
        {
            BgColor = TierColor(tier),
            BorderColor = new Color(0, 0, 0, 0.85f),
        };
        style.SetBorderWidthAll(3);
        style.SetCornerRadiusAll(10);
        style.SetContentMarginAll(4);

        var panel = new PanelContainer
        {
            Name = NodeName,
            MouseFilter = Control.MouseFilterEnum.Ignore, // never block clicks on the card
            ZIndex = 10,
            Visible = Enabled,
        };
        panel.AddThemeStyleboxOverride("panel", style);

        var label = new Label
        {
            Text = tier,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            CustomMinimumSize = new Vector2(44, 44),
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 32);
        label.AddThemeColorOverride("font_color", Colors.White);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 8);
        panel.AddChild(label);

        holder.AddChild(panel);
        // Holder origin is the card centre; put the badge's centre on the card's top-right corner.
        panel.Position = topRight - new Vector2(26, 26);

        lock (Live) Live.Add(new WeakReference<Control>(panel));
        return panel;
    }

    public static void RemoveFrom(Node holder)
    {
        Node? old = holder.GetNodeOrNull(NodeName);
        if (old is null) return;
        holder.RemoveChild(old);
        old.QueueFree();
    }

    public static void SetAllVisible(bool visible)
    {
        lock (Live)
        {
            Live.RemoveAll(w => !w.TryGetTarget(out Control? c) || !GodotObject.IsInstanceValid(c));
            foreach (WeakReference<Control> w in Live)
            {
                if (w.TryGetTarget(out Control? c)) c.Visible = visible;
            }
        }
    }
}

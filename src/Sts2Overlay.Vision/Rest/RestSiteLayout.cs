namespace Sts2Overlay.Vision.Rest;

/// <summary>
/// Where the rest-site option buttons sit, in 1920x1080 design units.
///
/// WHY a formula instead of measured positions: the buttons live in a Godot <c>HBoxContainer</c>
/// (scenes/rooms/rest_site_room.tscn, node ChoicesContainer) that is centred on the screen and packs its
/// children with a fixed gap. So the positions are not "fixed per screen", they depend only on how many
/// options there are (2 normally, 3 in co-op with Mend, more with relics such as Shovel or Girya).
/// Reading the numbers from the scene gives every count at once, including counts we have never seen
/// on a real frame; the two-button case was checked against 24 real screenshots (icons within ~2 px).
///
/// Numbers from v0.107.1:
/// <list type="bullet">
/// <item>ChoicesContainer: anchored at the screen centre, offsets left -399 / right 400, top -285 / bottom -122.121,
/// separation 100, alignment centre, grows both ways when the buttons need more than its 799 px.</item>
/// <item>rest_site_button.tscn: minimum size 246.786 x 162.879; the Icon TextureRect is inset 8 px at the top and
/// 3 px at the bottom and keeps the 256x169 texture's aspect ratio (stretch mode "keep aspect centred");
/// hover scales the Visuals node by 1.1 (press by 0.9) around the pivot (125, 82).</item>
/// </list>
/// </summary>
public static class RestSiteLayout
{
    /// <summary>Size of one button slot (the HBox child) in design px.</summary>
    public const float SlotWidth = 246.786f, SlotHeight = 162.879f;

    /// <summary>Gap the HBox leaves between two buttons.</summary>
    public const float Separation = 100f;

    /// <summary>Top of the button row: screen centre (540) + offset_top (-285).</summary>
    public const float RowTop = 255f;

    /// <summary>Horizontal centre of the row: midpoint of the container's offsets (-399 and 400) around 960.</summary>
    public const float RowCenterX = 960.5f;

    /// <summary>Distance between the left edges of neighbouring buttons.</summary>
    public const float Pitch = SlotWidth + Separation;

    /// <summary>Source size of every option icon (images/ui/rest_site/option_*.png).</summary>
    public const float IconTextureWidth = 256f, IconTextureHeight = 169f;

    /// <summary>Pivot of the hover/press scale animation, relative to the slot's top-left corner.</summary>
    public const float PivotX = 125f, PivotY = 82f;

    /// <summary>The game has at most a handful of options; 6 already overflows a 1920-wide screen.</summary>
    public const int MaxOptions = 6;

    /// <summary>
    /// Slot (layout box) of option <paramref name="index"/> when <paramref name="count"/> options are shown.
    /// The HBox centres its content, so the row starts half the total width left of the centre.
    /// </summary>
    public static RectF Slot(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        float total = count * SlotWidth + (count - 1) * Separation;
        float x = RowCenterX - total / 2 + index * Pitch;
        return new RectF(x, RowTop, SlotWidth, SlotHeight);
    }

    /// <summary>
    /// The visible picture of a button (what the player sees and clicks), for a slot and the current animation
    /// scale (1 = idle, 1.1 = hovered, 0.9 = pressed).
    /// WHY "keep aspect": the icon box is 246.8 x 151.9 but the texture is 256x169 (wider aspect is 1.515 vs
    /// 1.625), so the picture fills the height and is centred horizontally, about 8 px in from each side.
    /// </summary>
    public static RectF Icon(RectF slot, float scale = 1f)
    {
        float boxH = slot.Height - 8f - 3f;
        float h = boxH, w = boxH * IconTextureWidth / IconTextureHeight;
        float x = slot.X + (slot.Width - w) / 2, y = slot.Y + 8f;
        float px = slot.X + PivotX, py = slot.Y + PivotY;
        return new RectF(px + (x - px) * scale, py + (y - py) * scale, w * scale, h * scale);
    }

    /// <summary>Grid used to summarise an icon: 4 columns x 3 rows of mean colours.</summary>
    public const int CellColumns = 4, CellRows = 3;

    /// <summary>
    /// Fraction of the icon trimmed on every side before cutting it into cells. The icons have a hand-painted,
    /// uneven frame, and the background shows through its rounded corners; trimming 10% keeps every cell on
    /// the picture itself, and it also absorbs a few px of position or scale error.
    /// </summary>
    public const float CellInset = 0.10f;

    /// <summary>Cell (<paramref name="col"/>, <paramref name="row"/>) of an icon rectangle (any units).</summary>
    public static RectF Cell(RectF icon, int col, int row)
    {
        float x0 = icon.X + icon.Width * CellInset, y0 = icon.Y + icon.Height * CellInset;
        float w = icon.Width * (1 - 2 * CellInset) / CellColumns, h = icon.Height * (1 - 2 * CellInset) / CellRows;
        return new RectF(x0 + col * w, y0 + row * h, w, h);
    }
}

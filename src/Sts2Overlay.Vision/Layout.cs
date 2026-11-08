namespace Sts2Overlay.Vision;

/// <summary>
/// Geometry of a card node at scale 1, relative to the card's centre (the holder's pivot), in 1920x1080
/// reference units. Values from the game's scenes/cards/card.tscn (v0.107.1, NCard.defaultSize 300x422).
/// </summary>
public static class CardGeometry
{
    /// <summary>Whole card (frame).</summary>
    public static readonly RectF Card = new(-150, -211, 300, 422);
    /// <summary>%Portrait TextureRect: the 250x190 art.</summary>
    public static readonly RectF Art = new(-125, -168, 250, 190);
    /// <summary>%TitleLabel: where the name is drawn (cream normally, green #7FFF00 when upgraded).</summary>
    public static readonly RectF Title = new(-105, -204, 210, 54);

    /// <summary>A card-local rectangle placed at (cx, cy) with the given node scale.</summary>
    public static RectF Place(RectF local, float cx, float cy, float scale) =>
        new(cx + local.X * scale, cy + local.Y * scale, local.Width * scale, local.Height * scale);
}

/// <summary>Where a card can sit on a given screen, before the local search refines it.</summary>
public readonly record struct SlotLayout(int Index, CardSlotKind Kind, float CenterX, float CenterY, float SmallScale, float HoverScale);

/// <summary>Layout tables derived from the decompiled screens (see per-member comments).</summary>
public static class ScreenLayouts
{
    // ---- Card reward selection (NCardRewardSelectionScreen + card_reward_selection_screen.tscn) ----
    // CardRow is anchored at the screen centre with offset_top 76 → row centre (960, 616).
    // RefreshOptions: x = -(n-1) * 350 / 2 + 350 * i. Holders start at NCardHolder.smallScale 0.8 and jump to
    // HoverScale 1.0 while hovered (scaling about the card centre). Positions tween from the centre outwards
    // for 0.5 s when the screen opens (see Spread) and the cards fade in from black over 1 s.
    public const float RewardRowX = 960f, RewardRowY = 616f, RewardSpacing = 350f;
    public const float RewardSmallScale = 0.8f, RewardHoverScale = 1.0f;

    /// <param name="count">Number of offered cards (the game is tuned for 3; 2..5 occur with relics/modifiers).</param>
    /// <param name="spread">1 = final layout; 0..1 while the opening tween moves the cards apart.</param>
    public static SlotLayout[] Reward(int count, float spread = 1f)
    {
        var slots = new SlotLayout[count];
        for (int i = 0; i < count; i++)
        {
            float x = RewardRowX + (i - (count - 1) / 2f) * RewardSpacing * spread;
            slots[i] = new SlotLayout(i, CardSlotKind.Reward, x, RewardRowY, RewardSmallScale, RewardHoverScale);
        }
        return slots;
    }

    // ---- Choose a card (NChooseACardSelectionScreen + choose_a_card_selection_screen.tscn) ----
    // Same banner and holders (NGridCardHolder, 0.8 → 1.0 on hover) as the reward screen, but CardRow has
    // offset_top 60 → row centre (960, 600), and _cardXSpacing is 340: x = -(n-1) * 340 / 2 + 340 * i.
    public const float ChoiceRowY = 600f, ChoiceSpacing = 340f;

    /// <param name="count">Number of cards (3 from relics/potions/Discovery; Splash and Quasar can differ).</param>
    /// <param name="spread">1 = final layout; 0..1 while the 0.5 s opening tween moves the cards apart.</param>
    public static SlotLayout[] Choice(int count, float spread = 1f)
    {
        var slots = new SlotLayout[count];
        for (int i = 0; i < count; i++)
        {
            float x = RewardRowX + (i - (count - 1) / 2f) * ChoiceSpacing * spread;
            slots[i] = new SlotLayout(i, CardSlotKind.Choice, x, ChoiceRowY, RewardSmallScale, RewardHoverScale);
        }
        return slots;
    }

    // ---- Card grid (NCardGrid + card_grid.tscn, inside simple_card_select_screen.tscn and the deck screens) ----
    // The grid Control is the full screen below offset_top 80 → y 80..1080 (height 1000). Its ScrollContainer has
    // offset_left 150 / offset_right -200 → x 150..1720 (width 1570). Holders are NGridCardHolder at
    // NCardHolder.smallScale 0.8 → card cell 240 x 337.6, CardPadding 40:
    //   Columns = (int)((1570 + 40) / (240 + 40)) = 5, and the grid block is always 5 columns wide
    //   (GetContainedCardsSize uses Columns, not the card count), so fewer cards start at the left column:
    //   x = 150 + (1570 - 1360) / 2 + 120 + 280 * col = 375 + 280 * col.
    //   y inside the container = YOffset(0) + 80 + 168.8 + 377.6 * row; the container is
    //   contentHeight + 400 tall and centred (CenterGrid) only when that fits in 1000, i.e. for one row:
    //   one row  → container top (1000 - 737.6) / 2 = 131.2 → y = 80 + 131.2 + 248.8 = 460;
    //   two+ rows → container top 0                    → y = 80 + 248.8 + 377.6 * row = 328.8, 706.4 (row 2 is
    //   at 1084, off screen until the player scrolls).
    public const float GridX0 = 375f, GridColumnStep = 280f, GridRowStep = 377.6f;
    public const float GridOneRowY = 460f, GridFirstRowY = 328.8f;
    public const int GridColumns = 5;

    /// <param name="twoRows">False: a single centred row (≤ 5 cards); true: the top two rows of a taller grid.</param>
    public static SlotLayout[] Grid(bool twoRows)
    {
        int rows = twoRows ? 2 : 1;
        var slots = new SlotLayout[rows * GridColumns];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < GridColumns; c++)
            {
                float y = twoRows ? GridFirstRowY + GridRowStep * r : GridOneRowY;
                int i = r * GridColumns + c;
                slots[i] = new SlotLayout(i, CardSlotKind.Grid, GridX0 + GridColumnStep * c, y, RewardSmallScale, RewardHoverScale);
            }
        return slots;
    }

    // ---- Shop (scenes/merchant/merchant_inventory.tscn, merchant_card.tscn, NMerchantInventory/NMerchantSlot) ----
    // SlotsContainer: anchored at the centre, offset_left -842 → x = 118; NMerchantInventory tweens its y to
    // _openPosition 80. CharacterCards container at (332, 303), ColorlessCards at (397, 676); each
    // MerchantCard is placed at its offset_left/offset_top with the card centred on that point, at
    // scale 0.65 (_smallScale) and 0.8 when hovered (_hoverScale).
    public const float ShopSmallScale = 0.65f, ShopHoverScale = 0.8f;
    private const float SlotsX = 960f - 842f, SlotsY = 80f;
    private static readonly (float X, float Y)[] CharacterOffsets = [(-13, 0), (250, 0), (511, 0), (770, 0), (1036, -3)];
    private static readonly (float X, float Y)[] ColorlessOffsets = [(-13, 0), (261, 0)];

    public static SlotLayout[] Shop()
    {
        var slots = new List<SlotLayout>();
        foreach ((float x, float y) in CharacterOffsets)
            slots.Add(new SlotLayout(slots.Count, CardSlotKind.ShopCharacter, SlotsX + 332 + x, SlotsY + 303 + y, ShopSmallScale, ShopHoverScale));
        foreach ((float x, float y) in ColorlessOffsets)
            slots.Add(new SlotLayout(slots.Count, CardSlotKind.ShopColorless, SlotsX + 397 + x, SlotsY + 676 + y, ShopSmallScale, ShopHoverScale));
        return slots.ToArray();
    }

    /// <summary>
    /// The price label under a shop card (merchant_card.tscn "Cost": x -148..149, y 220..278 in card units).
    /// The digits are read by <see cref="ShopPriceReader"/> (which pads this box slightly).
    /// </summary>
    public static RectF ShopPrice(float cx, float cy, float scale) => CardGeometry.Place(new RectF(-148, 220, 297, 58), cx, cy, scale);
}

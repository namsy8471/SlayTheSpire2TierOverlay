namespace Sts2Overlay.Vision.Tests;

/// <summary>
/// The occlusion-robust parts of the recogniser: per-tile statistics, the partial (tile) match, the tile-fit
/// check on whole-art accepts, the covered-title check and the wider box searches.
/// </summary>
public class PartialMatchTests
{
    // ------------------------------------------------------------------ TileGrid arithmetic

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tile_sums_and_dots_match_a_naive_loop(bool blocks)
    {
        TileGrid g = blocks ? TileGrid.Blocks : TileGrid.Rows;
        var rng = new Random(7);
        float[] a = Enumerable.Range(0, Ncc.FullLength).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        float[] b = Enumerable.Range(0, Ncc.FullLength).Select(_ => (float)rng.NextDouble() - 0.5f).ToArray();
        float[] sum = new float[g.Count], sq = new float[g.Count], dots = new float[g.Count];
        g.SumsOf(a, sum, sq);
        g.Dots(a, b, dots);

        double[] es = new double[g.Count], eq = new double[g.Count], ed = new double[g.Count];
        for (int y = 0; y < Ncc.FullH; y++)
            for (int x = 0; x < Ncc.FullW; x++)
            {
                int tile = y / g.CellsY * g.TilesX + x / g.CellsX;
                for (int c = 0; c < 3; c++)
                {
                    int i = (y * Ncc.FullW + x) * 3 + c;
                    es[tile] += a[i];
                    eq[tile] += a[i] * a[i];
                    ed[tile] += a[i] * b[i];
                }
            }
        for (int t = 0; t < g.Count; t++)
        {
            Assert.Equal(es[t], sum[t], 3);
            Assert.Equal(eq[t], sq[t], 3);
            Assert.Equal(ed[t], dots[t], 3);
        }
    }

    [Fact]
    public void A_card_fits_its_own_art_on_every_tile()
    {
        CardArtDatabase db = SyntheticDb.Ten();
        var m = new TileMatcher(db, TileGrid.Blocks);
        var stage = new RecognizerOptions().BlockStage;
        (int visible, float score) = m.Fit(db.FullVector(3), 3, stage);
        Assert.Equal(TileGrid.Blocks.Count, visible);
        Assert.Equal(1f, score, 3);
        // A different card agrees on few tiles, if any.
        (int other, _) = m.Fit(db.FullVector(3), 4, stage);
        Assert.True(other < stage.MinTiles, $"{other} tiles");
    }

    // ------------------------------------------------------------------ screens

    private static void PaintCard(Canvas c, float cx, float cy, float scale, ScreenFrame art, bool upgraded = false)
    {
        c.FillRef(CardGeometry.Place(CardGeometry.Card, cx, cy, scale), 55, 45, 35);
        c.DrawImageRef(CardGeometry.Place(CardGeometry.Art, cx, cy, scale), art);
        RectF title = CardGeometry.Place(CardGeometry.Title, cx, cy, scale);
        c.FillRef(title, 90, 70, 50);
        for (int k = 0; k < 4; k++)
        {
            var stroke = new RectF(title.X + title.Width * 0.15f, title.Y + title.Height * (0.3f + 0.12f * k), title.Width * 0.7f, title.Height * 0.06f);
            if (upgraded) c.FillRef(stroke, 127, 255, 0);
            else c.FillRef(stroke, 255, 246, 226);
        }
    }

    /// <summary>The merchant's hand: smooth pale blue over the given share of the art, from the right (the whole-art
    /// NCC of the synthetic art drops below every acceptance bar at 35 %).</summary>
    private static void CoverArtRight(Canvas c, float cx, float cy, float scale, float share)
    {
        RectF art = CardGeometry.Place(CardGeometry.Art, cx, cy, scale);
        c.FillRef(new RectF(art.Right - art.Width * share, art.Y - 40, art.Width * share + 10, art.Height + 80), 150, 200, 225);
    }

    private static Canvas Reward(params (ScreenFrame Art, float Dy)[] cards)
    {
        var c = new Canvas(1920, 1080);
        ClassifierAndUpgradeTests.PaintRewardBanner(c);
        SlotLayout[] slots = ScreenLayouts.Reward(cards.Length);
        for (int i = 0; i < cards.Length; i++) PaintCard(c, slots[i].CenterX, slots[i].CenterY + cards[i].Dy, 0.8f, cards[i].Art);
        return c;
    }

    [Fact]
    public void Half_covered_card_is_rescued_by_the_partial_match()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = Reward((Canvas.Texture(101), 0), (Canvas.Texture(102), 0), (Canvas.Texture(103), 0));
        CoverArtRight(c, 1310, 616, 0.8f, 0.35f);

        ScreenRecognition r = rec.Recognize(c.Frame);

        Assert.True(r.Slots[2].Accepted, r.Slots[2].ToString());
        Assert.Equal(ScreenKind.RewardCardSelect, r.Kind);
        Assert.Equal(["CARD.C1", "CARD.C2", "CARD.C3"], r.Cards.Select(x => x.CardId));
        RecognizedCard covered = r.Cards[2];
        Assert.NotNull(covered.Partial);
        Assert.True(covered.Partial!.Accepted);
        Assert.True(covered.Score < 0.85f, covered.ToString());
        // Most of the art is hidden: "no green" proves nothing about the upgrade.
        Assert.False(covered.UpgradeKnown);
    }

    [Fact]
    public void Covered_card_missing_from_the_database_is_not_replaced_by_another()
    {
        // The covered card's art (seed 900) is not in the database: every candidate is an impostor.
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = Reward((Canvas.Texture(101), 0), (Canvas.Texture(102), 0), (Canvas.Texture(900), 0));
        CoverArtRight(c, 1310, 616, 0.8f, 0.35f);

        ScreenRecognition r = rec.Recognize(c.Frame);

        Assert.Equal(["CARD.C1", "CARD.C2"], r.Cards.Select(x => x.CardId));
        Assert.False(r.Slots[2].Accepted, r.Slots[2].ToString());
    }

    [Fact]
    public void Partial_matches_alone_do_not_make_a_reward_screen()
    {
        // One clean card and two covered ones: the screen needs two whole-art accepts, so a partial
        // match can never be the reason a non-reward screen is taken for one.
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = Reward((Canvas.Texture(101), 0), (Canvas.Texture(102), 0), (Canvas.Texture(103), 0));
        CoverArtRight(c, 960, 616, 0.8f, 0.35f);
        CoverArtRight(c, 1310, 616, 0.8f, 0.35f);

        ScreenRecognition r = rec.Recognize(c.Frame);

        Assert.Equal(ScreenKind.Other, r.Kind);
        Assert.Empty(r.Cards);
    }

    [Fact]
    public void Smooth_occluder_does_not_hand_the_slot_to_a_card_with_a_smooth_sky()
    {
        // Card B's art is card A's with its right half replaced by a smooth pale-blue "sky" - exactly what card A
        // looks like under the merchant's hand. The whole-art NCC alone can prefer B; the tile-fit check must not
        // let it through, and the partial match then finds A.
        ScreenFrame a = Canvas.Texture(300);
        ScreenFrame b = WithSky(Canvas.Texture(301), 0.5f);
        CardArtDatabase db = FromTextures(("CARD.A", a), ("CARD.B", b), ("CARD.C1", Canvas.Texture(101)), ("CARD.C2", Canvas.Texture(102)));
        var rec = new CardRecognizer(db);
        Canvas c = Reward((Canvas.Texture(101), 0), (Canvas.Texture(102), 0), (a, 0));
        RectF art = CardGeometry.Place(CardGeometry.Art, 1310, 616, 0.8f);
        c.FillRef(new RectF(art.X + art.Width * 0.5f, art.Y, art.Width * 0.5f, art.Height), 150, 200, 225);

        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.True(r.Slots[2].CardId != "CARD.B" || !r.Slots[2].Accepted, r.Slots[2].ToString());
        RecognizedCard slot = r.Slots[2];
        Assert.True(!slot.Accepted || slot.CardId == "CARD.A", slot.ToString());
    }

    [Fact]
    public void Tooltip_over_the_title_makes_the_upgrade_unknown()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = Reward((Canvas.Texture(101), 0), (Canvas.Texture(102), 0), (Canvas.Texture(103), 0));
        // A neighbour's hover tooltip (dark slate panel) over the right card's name only; the art stays clean.
        c.FillRef(CardGeometry.Place(CardGeometry.Title, 1310, 616, 0.8f), 35, 45, 52);

        ScreenRecognition r = rec.Recognize(c.Frame);

        Assert.Equal("CARD.C3", r.Cards[2].CardId);
        Assert.True(r.Cards[2].Score > 0.9f);
        Assert.False(r.Cards[2].UpgradeKnown);
        Assert.True(r.Cards[0].UpgradeKnown);
    }

    [Fact]
    public void Reward_card_lifted_above_its_layout_position_is_found()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        Canvas c = Reward((Canvas.Texture(101), 0), (Canvas.Texture(102), -30), (Canvas.Texture(103), 0));
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(["CARD.C1", "CARD.C2", "CARD.C3"], r.Cards.Select(x => x.CardId));
        Assert.True(r.Cards[1].Score > 0.9f, r.Cards[1].ToString());
    }

    [Fact]
    public void Hovered_shop_card_off_its_layout_position_is_found()
    {
        var rec = new CardRecognizer(SyntheticDb.Ten());
        var c = new Canvas(1920, 1080);
        ClassifierAndUpgradeTests.PaintRug(c);
        SlotLayout[] slots = ScreenLayouts.Shop();
        for (int i = 0; i < slots.Length; i++)
        {
            bool hovered = i == 3;
            PaintCard(c, slots[i].CenterX + (hovered ? 18 : 0), slots[i].CenterY - (hovered ? 8 : 0),
                hovered ? slots[i].HoverScale : slots[i].SmallScale, Canvas.Texture(100 + i));
        }
        ScreenRecognition r = rec.Recognize(c.Frame);
        Assert.Equal(ScreenKind.Shop, r.Kind);
        Assert.Equal(Enumerable.Range(0, 7).Select(i => $"CARD.C{i}"), r.Cards.Select(x => x.CardId));
        Assert.True(r.Cards[3].Score > 0.9f, r.Cards[3].ToString());
    }

    // ------------------------------------------------------------------ helpers

    private static ScreenFrame WithSky(ScreenFrame tex, float share)
    {
        byte[] px = tex.Bgra.ToArray();
        int x0 = (int)(tex.Width * (1 - share));
        for (int y = 0; y < tex.Height; y++)
            for (int x = x0; x < tex.Width; x++)
            {
                int p = y * tex.Stride + x * 4;
                px[p] = 225;
                px[p + 1] = 200;
                px[p + 2] = 150;
            }
        return ScreenFrame.FromPacked(px, tex.Width, tex.Height);
    }

    private static CardArtDatabase FromTextures(params (string Id, ScreenFrame Art)[] cards)
    {
        var entries = new List<ArtEntry>();
        float[] full = new float[cards.Length * Ncc.FullLength];
        float[] coarse = new float[cards.Length * Ncc.CoarseLength];
        for (int i = 0; i < cards.Length; i++)
        {
            entries.Add(new ArtEntry(i, cards[i].Id, SyntheticDb.Ironclad, null, $"{cards[i].Id}.webp", false));
            CardArtDatabase.ComputeArtVectors(cards[i].Art, full.AsSpan(i * Ncc.FullLength, Ncc.FullLength), coarse.AsSpan(i * Ncc.CoarseLength, Ncc.CoarseLength));
        }
        return new CardArtDatabase(entries, full, coarse, [], "synthetic");
    }
}

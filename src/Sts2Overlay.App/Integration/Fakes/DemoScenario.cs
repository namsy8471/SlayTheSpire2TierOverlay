using Sts2Overlay.Core.Model;

namespace Sts2Overlay.App.Integration.Fakes;

/// <summary>Which screen the demo pretends is open.</summary>
public enum DemoScreen
{
    Reward,
    Shop,
    Other,
}

/// <summary>
/// Hard-coded demo data: an act-1 Defect run and offers that use the cards of <c>data/packs/sample</c>.
/// Card positions follow the real 1920x1080 layout measured in docs/SPIKE.md §4.2, scaled to the frame size.
/// </summary>
public static class DemoScenario
{
    public const string Character = "CHARACTER.DEFECT";

    public static RunState Run { get; } = new()
    {
        Character = Character,
        Ascension = 0,
        Act = 1,
        Floor = 7,
        Hp = 52,
        MaxHp = 75,
        Gold = 160,
        Relics = ["RELIC.CRACKED_CORE"],
        Deck =
        [
            .. Enumerable.Repeat(new DeckCard(new CardRef("CARD.STRIKE_DEFECT")), 4),
            .. Enumerable.Repeat(new DeckCard(new CardRef("CARD.DEFEND_DEFECT")), 4),
            new DeckCard(new CardRef("CARD.ZAP")),
            new DeckCard(new CardRef("CARD.DUALCAST")),
            new DeckCard(new CardRef("CARD.BALL_LIGHTNING"), FloorAdded: 2),
            new DeckCard(new CardRef("CARD.COOLHEADED", 1), FloorAdded: 4),
        ],
    };

    /// <summary>Reward cards; the third one is "hovered" (game scales it 0.8 → 1.0 and lifts it).</summary>
    public static IReadOnlyList<RecognizedCard> RewardCards(PixelRect frame)
    {
        double sx = frame.Width / 1920.0, sy = frame.Height / 1080.0;
        PixelRect Card(double cx, double top, double w, double h)
            => new(frame.X + (int)((cx - w / 2) * sx), frame.Y + (int)(top * sy), (int)(w * sx), (int)(h * sy));
        return
        [
            new RecognizedCard(new CardRef("CARD.BALL_LIGHTNING", 1), Card(610, 432, 240, 338), 0.97),
            new RecognizedCard(new CardRef("CARD.DEFRAGMENT"), Card(960, 432, 240, 338), 0.96),
            new RecognizedCard(new CardRef("CARD.HOLOGRAM"), Card(1310, 372, 300, 422), 0.99),
        ];
    }

    public static IReadOnlyList<RecognizedCard> ShopCards(PixelRect frame)
    {
        double sx = frame.Width / 1920.0, sy = frame.Height / 1080.0;
        PixelRect Card(double cx) => new(frame.X + (int)((cx - 98) * sx), frame.Y + (int)(190 * sy), (int)(195 * sx), (int)(274 * sy));
        return
        [
            new RecognizedCard(new CardRef("CARD.COOLHEADED"), Card(560), 0.95, 50, Unaffordable: false),
            new RecognizedCard(new CardRef("CARD.GLACIER"), Card(780), 0.93, 39, Unaffordable: false, OnSale: true),
            // red label although the save says 160 gold: the panel plans with ≤151
            new RecognizedCard(new CardRef("CARD.DEFRAGMENT"), Card(1000), 0.94, 152, Unaffordable: true),
            // label covered: price estimated from rarity
            new RecognizedCard(new CardRef("CARD.HOLOGRAM"), Card(1220), 0.92),
        ];
    }

    public static PathView Path { get; } = new(
        [
            new PathOptionView("균형", "엘리트 1 · 휴식 2 · 상점 1 · 보스 도착 HP 예상 64%", ["휴식 전 엘리트 없음", "상점에서 제거"], 71, IsRecommended: true),
            new PathOptionView("안전", "엘리트 0 · 휴식 3 · 미지 3 · 보스 도착 HP 예상 78%", ["HP 보존", "카드 보상 적음"], 63),
            new PathOptionView("공격", "엘리트 3 · 휴식 1 · 보스 도착 HP 예상 41%", ["위험: 엘리트 연속", "유물 +3"], 55),
        ],
        "다음: 오른쪽 위 휴식 (4,8)",
        "데모 데이터 (경로 추천 엔진 연결 전)");
}

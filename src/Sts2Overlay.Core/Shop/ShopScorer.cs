using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Shop;

/// <summary>PLAN §7.3: cards ranked by (score − skip) / price, card removal as a candidate, best affordable bundle.</summary>
public sealed class ShopScorer(DataPack pack)
{
    public DataPack Pack { get; } = pack;

    public ShopEvaluation Evaluate(RunState state, DeckProfile profile, ShopOffer offer)
        => throw new NotImplementedException();

    /// <summary>75 + 25 × removals so far (ascension ≥ 6: 100 + 50 ×). [확인, sts2.gg; 게임 코드로 재확인 필요]</summary>
    public static int RemovalPrice(RunState state, Weights weights)
        => throw new NotImplementedException();
}

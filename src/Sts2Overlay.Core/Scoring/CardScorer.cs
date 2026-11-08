using Sts2Overlay.Core.Data;
using Sts2Overlay.Core.Model;

namespace Sts2Overlay.Core.Scoring;

/// <summary>
/// PLAN §7.1: score = base + build + combo + need + situation − dilution − duplicates,
/// plus the DECK_COMPASS rules (requires gate, saturation, weakness-first build cap).
/// </summary>
public sealed class CardScorer(DataPack pack)
{
    public DataPack Pack { get; } = pack;

    public CardEvaluation Score(RunState state, DeckProfile profile, CardRef card, OfferContext context = OfferContext.CardReward)
        => throw new NotImplementedException();
}

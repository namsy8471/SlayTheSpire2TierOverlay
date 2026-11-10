namespace Sts2Overlay.App.Integration;

/// <summary>
/// Is the merchant's card-removal service still for sale this visit? The coin probe (<see cref="RecognitionResult.RemovalService"/>)
/// answers per frame, and "not seen" also happens when a tooltip, the merchant's hand or an enlarged card covers the coin
/// (or the player hovers it to think about it). So "not seen" alone never removes the service: only evidence that it was
/// USED does. Using it opens the deck grid to pick the card (Shop → Grid → Shop), so the service counts as gone when a
/// grid was seen after the coin was last seen for sale and the coin is still not seen back in the shop.
/// <para>Pure and UI-free (unit-tested); the controller feeds it every recognition result and ends the visit when the
/// tracker's shop hint goes away.</para>
/// </summary>
public sealed class RemovalAvailability
{
    private bool _inShop;
    private bool _gridSinceCoin;

    /// <summary>The removal service can be planned for. True when unknown (start of a visit, or never seen yet).</summary>
    public bool Available { get; private set; } = true;

    /// <summary>
    /// One recognition result: <paramref name="kind"/> as recognised (a deck grid before it is turned into "Other"),
    /// <paramref name="coinForSale"/> = the probe saw the coin still for sale. Returns <see cref="Available"/>.
    /// </summary>
    public bool Observe(ScreenKind kind, bool coinForSale)
    {
        switch (kind)
        {
            case ScreenKind.Shop:
                _inShop = true;
                if (coinForSale)
                {
                    Available = true;
                    _gridSinceCoin = false;
                }
                else if (_gridSinceCoin)
                {
                    Available = false; // the grid came and went and the coin did not come back: it was used
                }
                // else: covered or hovered; keep the last answer
                break;
            case ScreenKind.Grid:
                if (_inShop) _gridSinceCoin = true;
                break;
            // Other screens (map, a card reward from a relic bought here, ...) do not end the visit: only EndVisit does, so a
            // used service stays used while the player is still at this merchant. (Opening the deck view also shows a grid; if
            // the coin is then covered on return it reads as used until the coin is seen again.)
        }

        return Available;
    }

    /// <summary>The shop visit is over (the tracker left the merchant room): the next visit starts as available.</summary>
    public void EndVisit()
    {
        _inShop = false;
        _gridSinceCoin = false;
        Available = true;
    }
}

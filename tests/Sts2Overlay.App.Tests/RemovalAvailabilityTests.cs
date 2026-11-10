using Sts2Overlay.App.Integration;

namespace Sts2Overlay.App.Tests;

/// <summary>The removal service is gone only after evidence it was used (Shop → Grid → Shop without the coin), not while it is covered.</summary>
public class RemovalAvailabilityTests
{
    [Fact]
    public void Covered_coin_without_a_grid_stays_available()
    {
        var a = new RemovalAvailability();
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: true));
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: false)); // tooltip / hover / merchant's hand over the coin
        Assert.True(a.Observe(ScreenKind.Other, coinForSale: false));
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: false));
    }

    [Fact]
    public void Unknown_at_the_start_of_a_visit_counts_as_available()
    {
        var a = new RemovalAvailability();
        Assert.True(a.Available);
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: false));
    }

    [Fact]
    public void Shop_grid_shop_without_the_coin_means_it_was_used()
    {
        var a = new RemovalAvailability();
        a.Observe(ScreenKind.Shop, coinForSale: true);
        Assert.True(a.Observe(ScreenKind.Grid, coinForSale: false)); // picking the card: not decided yet
        Assert.False(a.Observe(ScreenKind.Shop, coinForSale: false));
        Assert.False(a.Observe(ScreenKind.Shop, coinForSale: false)); // stays used for the rest of the visit
        Assert.False(a.Observe(ScreenKind.Other, coinForSale: false));
        Assert.False(a.Available);
    }

    [Fact]
    public void Grid_opened_and_cancelled_is_available_again_once_the_coin_is_seen()
    {
        var a = new RemovalAvailability();
        a.Observe(ScreenKind.Shop, coinForSale: true);
        a.Observe(ScreenKind.Grid, coinForSale: false);
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: true)); // cancelled: the coin is back for sale
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: false)); // and a later cover does not count as used
    }

    [Fact]
    public void Grid_outside_a_shop_visit_is_no_evidence()
    {
        var a = new RemovalAvailability();
        a.Observe(ScreenKind.Grid, coinForSale: false); // rest-site upgrade, event, deck view before any shop
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: false));
    }

    [Fact]
    public void A_new_visit_starts_available()
    {
        var a = new RemovalAvailability();
        a.Observe(ScreenKind.Shop, coinForSale: true);
        a.Observe(ScreenKind.Grid, coinForSale: false);
        Assert.False(a.Observe(ScreenKind.Shop, coinForSale: false));

        a.EndVisit();
        Assert.True(a.Available);
        a.Observe(ScreenKind.Grid, coinForSale: false); // a grid between visits does not carry over
        Assert.True(a.Observe(ScreenKind.Shop, coinForSale: false));
    }
}

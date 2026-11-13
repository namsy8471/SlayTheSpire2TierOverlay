using Sts2Overlay.App.Controllers;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.Tests;

public class CardChoiceAdviceTests
{
    [Theory]
    [InlineData(ScreenKind.Reward, null, true)]
    [InlineData(ScreenKind.Choice, true, true)]
    [InlineData(ScreenKind.Grid, false, false)]
    [InlineData(ScreenKind.Choice, null, null)]
    [InlineData(ScreenKind.Grid, null, null)]
    public void Distinguishes_observed_skip_availability_from_an_unknown_choice(ScreenKind kind, bool? observed, bool? expected)
        => Assert.Equal(expected, CardChoiceAdvice.SkipAvailability(new(kind, []) { CanSkip = observed }));

    [Theory]
    [InlineData(Lang.Kor, "건너뛰기가 가능하면")]
    [InlineData(Lang.Eng, "If skipping is available")]
    public void Unknown_choice_only_describes_skip_conditionally(Lang language, string condition)
    {
        using var scope = Loc.Use(language);
        Assert.Contains(condition, CardChoiceAdvice.ConditionNote(null, true));
        Assert.NotNull(CardChoiceAdvice.ConditionNote(null, false));
        Assert.Null(CardChoiceAdvice.ConditionNote(false, true));
        Assert.Null(CardChoiceAdvice.ConditionNote(true, true));
    }
}

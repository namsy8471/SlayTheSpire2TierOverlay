using System.ComponentModel;
using System.Windows;
using Sts2Overlay.Core.Localization;

namespace Sts2Overlay.App.ViewModels;

/// <summary>
/// The fixed texts of the XAML views (tab names, empty-page hints, tags), in the current language.
/// <para>
/// XAML binds to it with <c>{Binding PanelTitle, Source={x:Static vm:UiText.Instance}}</c>: the binding does not depend on
/// the element's DataContext, and when <see cref="Loc.Changed"/> fires this object raises <c>PropertyChanged("")</c>
/// ("every property changed"), so WPF re-reads all of them and the open windows switch language without a restart.
/// </para>
/// Every property is a computed <c>Loc.T(…)</c> (never a stored string), so it can never be stuck in the old language.
/// </summary>
public sealed class UiText : INotifyPropertyChanged
{
    public static UiText Instance { get; } = new();

    private UiText()
    {
        Loc.Changed += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Tells WPF that every property changed. Runs on the UI thread (bindings must be updated there).</summary>
    public void RaiseAll()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
        else
        {
            dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)));
        }
    }

    private void OnLanguageChanged() => RaiseAll();

    // ---- names ----
    public string AppName => Loc.T("STS2 티어 오버레이", "STS2 Tier Overlay");
    public string PanelTitle => Loc.T("STS2 티어 패널", "STS2 Tier Panel");
    public string PanelHeader => Loc.T("STS2 티어", "STS2 Tier");

    // ---- panel header ----
    public string DemoTag => Loc.T("데모", "Demo");
    public string ClickThroughTag => Loc.T("클릭 통과", "Click-through");
    public string CollapseToolTip => Loc.T("접기/펼치기", "Collapse/expand");
    public string Collapse => Loc.T("▾ 접기", "▾ Collapse");
    public string Expand => Loc.T("▸ 펼치기", "▸ Expand");

    // ---- review banner ----
    public string Close => Loc.T("닫기", "Close");
    public string View => Loc.T("보기", "View");

    // ---- tabs ----
    public string TabOffer => Loc.T("추천", "Picks");
    public string TabDeck => Loc.T("덱", "Deck");
    public string TabBuild => Loc.T("빌드", "Build");
    public string TabPath => Loc.T("경로", "Path");
    public string TabReview => Loc.T("리뷰", "Review");

    // ---- 추천 tab ----
    public string OfferEmpty => Loc.T("보상 또는 상점 화면이 열리면 자동으로 표시됩니다.\n(Ctrl+F10: 지금 인식)",
        "Shows up when a reward or shop screen opens.\n(Ctrl+F10: recognize now)");
    public string OfferStale => Loc.T("화면이 닫힘 · 마지막 결과", "Screen closed · last result");

    // ---- tags on cards ----
    public string EstimateTag => Loc.T("추정", "Est.");
    public string UpgradedTag => Loc.T("강화", "Upgraded");
    public string NoArt => Loc.T("그림 없음", "No art");

    // ---- 덱 tab ----
    public string DeckEmpty => Loc.T("런 정보 없음\n세이브 파일을 읽으면 표시됩니다.", "No run info\nShown once the save file is read.");
    public string RoleSupplyHeading => Loc.T("역할 공급 / 목표", "Role supply / target");

    // ---- 빌드 tab ----
    public string BuildEmpty => Loc.T("런 정보 없음 또는 이 캐릭터의 아키타입 데이터가 없습니다.",
        "No run info, or no archetype data for this character.");
    public string CurrentDirectionTag => Loc.T("현재 방향", "Current");
    public string BuildCardsToolTip => Loc.T("빌드 카드 보기/접기", "Show/hide build cards");
    public string ShowBuildCards => Loc.T("▸ 빌드 카드 보기", "▸ Show build cards");
    public string HideBuildCards => Loc.T("▾ 빌드 카드 접기", "▾ Hide build cards");

    // ---- 경로 tab ----
    public string PathEmpty => Loc.T("경로 추천 준비 중\n지도 정보가 연결되면 표시됩니다.", "Path advice not ready\nShown once map info is available.");

    // ---- 리뷰 tab ----
    public string ReviewEmpty => Loc.T("끝난 런이 없습니다.\n런이 끝나면 카드 선택을 추천과 비교한 리뷰가 표시됩니다.",
        "No finished run yet.\nWhen a run ends, a review compares your card picks with the advice.");
    public string ReviewHeading => Loc.T("지난 런 리뷰", "Last run review");
    public string DisagreementsHeading => Loc.T("추천과 가장 크게 다른 선택", "Biggest differences from the advice");

    // ---- footer ----
    public string HotkeyHint => Loc.T("Ctrl+F7 클릭 통과 · F8 숨기기 · F9 캡처 · F10 인식",
        "Ctrl+F7 click-through · F8 hide · F9 capture · F10 recognize");

    // ---- badges / markers over the game ----
    public string PickTag => Loc.T("추천", "Pick");
    public string PickBang => Loc.T("추천!", "Pick!");
    public string SkipPick => Loc.T("스킵 추천!", "Skip!");
    public string Teammate => Loc.T("동료", "Teammate");

    /// <summary>"2위": rank of a non-recommended event option.</summary>
    public static string Rank(int rank) => Loc.T($"{rank}위", $"#{rank}");
}

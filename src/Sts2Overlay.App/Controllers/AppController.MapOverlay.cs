using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Interop;
using Sts2Overlay.App.Services;
using Sts2Overlay.App.Views;
using Sts2Overlay.Core.Localization;
using Sts2Overlay.Core.Map;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// "맵 위 추천": the recommended next node ("추천!") and path drawn on the game's own map screen.
///
/// <code>
///  save (map + seed) ──▶ MapTarget (where the game draws every node) ──▶ MapScreenWatcher.Target
///  CapturePipeline (worker) ──MapSighted──▶ OnMapSighted: is the map open? how far is it scrolled?
///  PathAdvisor result (RefreshPath) ─┐
///  partner votes, "?" odds ──────────┴──▶ MapMarkLayout.Build ──▶ MapOverlayWindow
/// </code>
/// Kept in its own partial so the main controller only carries one-line hooks (init, snapshot, path, cadence,
/// visibility, topmost, UI scale, dispose).
/// </summary>
public sealed partial class AppController
{
    private readonly MapScreenWatcher _mapWatcher = new();
    private MapOverlayWindow? _mapLayer;
    private PathRecommendation? _mapRec;
    private MapSighting? _lastSighting;
    private bool _mapOpen;
    private bool _mapMoving;

    /// <summary>The scroll moved more than this between two frames (design px): the map is still moving.</summary>
    private const float MapStillTolerance = 3f;

    private void InitMapOverlay()
    {
        _mapLayer = new MapOverlayWindow(_pipeline.CaptureVisibility);
        _pipeline.MapWatcher = _mapWatcher;
        _pipeline.MapSighted += s => _ui.BeginInvoke(() => OnMapSighted(s));
    }

    public bool MapOverlayOn => _settings.MapOverlay;

    /// <summary>Tray / Ctrl+F5: turns the marks on the game's map on or off (persisted).</summary>
    public void ToggleMapOverlay()
    {
        _settings.MapOverlay = !_settings.MapOverlay;
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
        AppLog.Info($"map overlay = {_settings.MapOverlay}");
        ShowMessage(_settings.MapOverlay
            ? Loc.T("게임 화면 위 추천 켜짐: 맵의 추천 노드·경로, 휴식처의 추천 버튼·강화 카드를 표시합니다",
                    "On-screen picks on: the recommended node and path on the Map, the Rest Site option and card to Upgrade")
            : Loc.T("게임 화면 위 추천 꺼짐 (맵·휴식처)", "On-screen picks off (Map and Rest Site)"));
        UpdateMapTarget(_snapshot);
        UpdateVisibility();
        UpdateCadence();
    }

    /// <summary>Every snapshot: the map (or seed) may have changed, e.g. a new act.</summary>
    private void UpdateMapTarget(GameStateSnapshot snap)
    {
        MapTarget? target = null;
        if (_settings.MapOverlay && Demo is null && snap.Map is { } map && snap.Seed is { } seed)
        {
            // Same act map: keep the layout (positions don't depend on where we are), just take the newer graph.
            target = _mapWatcher.Target is { } cur && cur.Key == MapTarget.KeyFor(map, seed)
                ? cur with { Map = map }
                : MapTarget.For(map, seed);
            if (target is not null && target.Key != _mapWatcher.Target?.Key) AppLog.Info($"map overlay: act {map.Act} layout, {target.Marks.Count} nodes");
        }
        _mapWatcher.Target = target;
        if (target is null)
        {
            _lastSighting = null;
            _mapOpen = _mapMoving = false;
        }
        RedrawMap();
    }

    /// <summary>RefreshPath computed a new recommendation (or none).</summary>
    private void SetMapRecommendation(PathRecommendation? rec)
    {
        _mapRec = rec;
        RedrawMap();
    }

    private void OnMapSighted(MapSighting s)
    {
        if (s.Target.Key != _mapWatcher.Target?.Key) return; // a frame from before the act changed
        var prev = _lastSighting;
        _lastSighting = s;
        var wasOpen = _mapOpen;
        _mapOpen = s.IsMap;
        // The game eases the scroll over a few frames (and slides the map in when it opens). Marks drawn at the scroll
        // of the previous frame would sit beside their nodes, so they are hidden until two frames agree.
        _mapMoving = s.IsMap && (prev is not { IsMap: true } || Math.Abs(prev.Scroll - s.Scroll) > MapStillTolerance);
        if (wasOpen != _mapOpen)
        {
            AppLog.Info($"map screen {(_mapOpen ? $"open (scroll {s.Scroll:0}, fit {s.Ms:0.0} ms)" : "closed")}");
            UpdateCadence();
        }
        RedrawMap();
    }

    private void RedrawMap()
    {
        if (_mapLayer is null) return;
        // Rest-site marks (AppController.RestOverlay.cs) use the same layer; the map can't be open at the same time.
        if (!_restPlan.IsEmpty)
        {
            _mapLayer.ShowPlan(_restPlan);
            return;
        }
        if (!_mapOpen || _mapMoving || _lastSighting is not { } s || _mapWatcher.Target is not { } t || s.Target.Key != t.Key)
        {
            _mapLayer.Clear();
            return;
        }
        // The recommendation must start where the map says we are (it lags a save behind right after a move). In co-op my
        // own vote does not move "Current" (only the room-entry save does), so "추천!" stays up while the votes are open.
        var rec = _mapRec is { } r && r.Origin == (t.Map.Current ?? t.Map.Start) ? r : null;
        _mapLayer.ShowPlan(MapMarkLayout.Build(t.Map, t.Layout, s.Scroll, rec, _snapshot.PartnerVotes, _snapshot.UnknownOdds, _snapshot.MyVote));
    }

    /// <summary>Capture rate the map needs: fast while it is open (or about to be), else 0.</summary>
    private double MapCadenceHz() =>
        _mapWatcher.Target is not null && (_mapOpen || _snapshot.Hint == ScreenHint.Map) ? Math.Clamp(_settings.MapHz, 1, 15) : 0;

    private void UpdateMapLayerVisibility(bool show, GameWindowInfo? info)
    {
        if (_mapLayer is null) return;
        if (show && _settings.MapOverlay && info is not null)
        {
            if (!_mapLayer.IsVisible) _mapLayer.Show();
            _mapLayer.SetLayerRect(info.CaptureRect);
        }
        else if (_mapLayer.IsVisible)
        {
            _mapLayer.Hide();
        }
    }

    private void ReassertMapTopmost()
    {
        if (_mapLayer is { IsVisible: true }) OverlayWindowHelper.AssertTopmost(_mapLayer.Hwnd);
    }

    private void SetMapScale(double multiplier)
    {
        if (_mapLayer is not null) _mapLayer.ScaleMultiplier = multiplier;
    }

    private void DisposeMapOverlay()
    {
        _pipeline.MapWatcher = null;
        _mapLayer?.Close();
    }
}

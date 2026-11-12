using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.App.Integration.CombatAdvice;
using Sts2Overlay.App.Services;
using Sts2Overlay.Core.Localization;
using RunOutcome = Sts2Overlay.GameState.Tracking.RunOutcome;

namespace Sts2Overlay.App.Controllers;

/// <summary>
/// "이번 턴 추천 순서": the cards to play this turn, in order, shown above the tabs during a fight.
/// <para>
/// The order is computed by a separate helper process (docs/COMBAT_ADVISOR.md); the overlay only starts it, reads its
/// stdout and draws it:
/// <code>
///  snapshot ──▶ UpdateCombatAdvice: single-player run in progress? ──▶ AdvisorProcessHost.Run(live --save … --log …)
///  helper stdout (worker) ──▶ CombatAdviceParser ──▶ _combatMessage ──▶ CombatAdvicePresenter ──▶ _vm.Combat
/// </code>
/// No helper found (setting <c>combatAdvisorCommand</c>, or <c>advisor\Sts2CombatAdvisor.exe</c> next to the exe): the
/// feature is off, silently.
/// </para>
/// </summary>
public sealed partial class AppController
{
    private AdvisorCommand? _advisorCommand;
    private AdvisorProcessHost? _advisor;
    private CombatTextProvider _combatText = CombatTextProvider.Empty;

    /// <summary>The helper's last usable line; null before the first one and after the helper stopped or exited.</summary>
    private CombatAdviceMessage? _combatMessage;

    /// <summary>Kinds of unusable lines already logged (each is logged once).</summary>
    private readonly HashSet<string> _combatProblems = [];

    private void InitCombatAdvice()
    {
        _advisorCommand = AdvisorCommand.Locate(_settings.CombatAdvisorCommand, AppContext.BaseDirectory, out var warning);
        if (warning is not null) AppLog.Warn("combat advisor: " + warning);
        AppLog.Info(_advisorCommand is { } c
            ? $"combat advisor: {c.FileName} ({c.Source}{(c.LeadingArgs.Count > 0 ? $", {c.LeadingArgs.Count} leading args" : "")}), enabled {_settings.CombatAdvisorEnabled}"
            : "combat advisor: no helper found (feature off)");
        if (_advisorCommand is not null) _combatText = CombatTextProvider.ForLocalData();
    }

    /// <summary>A helper was found: the tray shows the on/off item.</summary>
    public bool CombatAdvisorAvailable => _advisorCommand is not null;

    /// <summary>The user's on/off choice (settings.json <c>combatAdvisorEnabled</c>).</summary>
    public bool CombatAdvisorOn => _settings.CombatAdvisorEnabled;

    /// <summary>Tray: turns the in-fight play order on or off (persisted). Off also stops the helper.</summary>
    public void ToggleCombatAdvisor()
    {
        _settings.CombatAdvisorEnabled = !_settings.CombatAdvisorEnabled;
        SettingsStore.Save(AppPaths.SettingsFile, _settings);
        AppLog.Info($"combat advisor enabled = {_settings.CombatAdvisorEnabled}");
        ShowMessage(_settings.CombatAdvisorEnabled
            ? Loc.T("전투 추천 순서 켜짐: 전투 중 이번 턴에 낼 카드 순서를 표시합니다", "Combat play order on: shows the cards to play this turn during fights")
            : Loc.T("전투 추천 순서 꺼짐", "Combat play order off"));
        UpdateCombatAdvice(_snapshot);
        StateChanged?.Invoke();
    }

    /// <summary>Every snapshot: keeps the helper running exactly while a single-player run is in progress, then redraws.</summary>
    private void UpdateCombatAdvice(GameStateSnapshot snap)
    {
        if (DesiredAdvisorArgs() is { } args)
        {
            if (_advisor is null)
            {
                _advisor = new AdvisorProcessHost(_advisorCommand!, AppLog.Info);
                _advisor.LineReceived += OnAdvisorLine;
                _advisor.Exited += _ => _ui.BeginInvoke(() =>
                {
                    _combatMessage = null; // what it said last is about a fight it no longer follows
                    RenderCombatAdvice();
                });
            }
            _advisor.Run(args);
        }
        else if (_advisor?.Arguments is not null)
        {
            _advisor.Stop();
            _combatMessage = null;
        }
        RenderCombatAdvice();
    }

    /// <summary>
    /// The helper's arguments, or null when it should not run: feature off or not found, demo mode, no single-player run in
    /// progress (co-op runs use another save file and are not supported), or the save / log path is unknown.
    /// </summary>
    private IReadOnlyList<string>? DesiredAdvisorArgs()
    {
        if (_advisorCommand is null || !_settings.CombatAdvisorEnabled || Demo is not null) return null;
        if (_source is not GameStateAdapter adapter) return null;
        if (adapter.Tracked is not { Outcome: RunOutcome.InProgress } run || run.PlayerCount > 1 || run.State.IsMultiplayer) return null;
        if (adapter.SinglePlayerSavePath is not { } save || adapter.LogFile is not { } log) return null;
        return AdvisorCommand.LiveArgs(save, log);
    }

    /// <summary>Worker thread: one stdout line of the helper.</summary>
    private void OnAdvisorLine(string line)
    {
        var message = CombatAdviceParser.Parse(line, out var problem);
        if (message is null)
        {
            if (problem is not null)
            {
                bool first;
                lock (_combatProblems) first = _combatProblems.Count < 32 && _combatProblems.Add(problem);
                if (first) AppLog.Warn($"combat advisor: ignored a line ({problem}); further lines like it are not logged");
            }
            return;
        }
        _ui.BeginInvoke(() =>
        {
            _combatMessage = message;
            RenderCombatAdvice();
        });
    }

    /// <summary>
    /// Draws the section from the last line. Shown only while the game runs and the tracker says a fight is on, whatever
    /// the helper said last (see <see cref="CombatAdvicePresenter"/>).
    /// </summary>
    private void RenderCombatAdvice()
    {
        var inCombat = _advisor?.Arguments is not null && _tracker.Current is not null && _snapshot.Hint == ScreenHint.Combat;
        var section = CombatAdvicePresenter.Build(_combatMessage, inCombat, new CombatNames(_cards, _combatText));
        if (section != _vm.Combat) _vm.Combat = section;
    }

    /// <summary>data/local was regenerated or the language changed: new names.</summary>
    private void ReloadCombatAdvice()
    {
        if (_advisorCommand is not null) _combatText = CombatTextProvider.ForLocalData();
        RenderCombatAdvice();
    }

    private void DisposeCombatAdvice()
    {
        _advisor?.Dispose();
        _advisor = null;
    }
}

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Integration;
using Sts2Overlay.Resources;

namespace Sts2Overlay.App.Services;

/// <summary>
/// <c>Sts2Overlay.exe --prepare [--force] [--game-dir &lt;dir&gt;]</c>: generates data/local without opening any window,
/// prints progress to the console that started it, and exits with 0 (ok / already up to date), 2 (game not found) or
/// 1 (failed). Used by tools/refresh-local.ps1.
/// </summary>
public static class PrepareCommand
{
    public static int Run(AppOptions options, AppSettings settings)
    {
        AttachToParentConsole();
        var layout = new LocalLayout(AppPaths.LocalDataDir);
        try
        {
            // An explicit --game-dir must be right (no silent fallback); a stale settings.json value may fall back to Steam.
            if (options.GameDir is { } explicitDir && !new GameInstall(explicitDir).IsValid)
            {
                Say($"--game-dir {explicitDir}: SlayTheSpire2.pck / data_sts2_windows_x86_64\\sts2.dll 이 없습니다.");
                return 2;
            }
            var install = GameInstall.Locate(options.GameDir ?? settings.GameDir);
            if (install is null)
            {
                Say("Slay the Spire 2 설치 폴더를 찾지 못했습니다. --game-dir <폴더> 또는 settings.json의 gameDir을 지정하세요.");
                return 2;
            }

            var game = InstalledGame.Detect(install);
            Say($"게임: {game.VersionLabel} ({install.Directory})");
            Say($"  release_info.json ↔ sts2.dll 해시: {game.AssemblyHashMatches switch { true => "일치", false => "불일치 (수정된 sts2.dll?)", null => "확인 불가" }}");
            Say($"  지문: {game.Fingerprint}");
            Say($"출력: {layout.LocalDir}");

            var preparer = new ResourcePreparer(layout);
            var check = preparer.Check(game);
            if (check.Need == PrepareNeed.UpToDate && !options.Force)
            {
                Say($"이미 최신입니다 (manifest.json, {check.Manifest?.PreparedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} 준비). 다시 만들려면 --force");
                return 0;
            }
            Say($"준비 시작: {(options.Force ? "--force" : check.Reason)}");

            // Print a line when the step changes or every 10 %, not for each of the ~900 textures.
            (string Step, int Bucket) last = ("", -1);
            var manifest = preparer.Prepare(game, new InlineProgress(p =>
            {
                var step = new string(p.Text.TakeWhile(c => !char.IsAsciiDigit(c)).ToArray());
                var bucket = (int)(p.Fraction * 10);
                if ((step, bucket) == last) return;
                last = (step, bucket);
                Say($"  {p.Fraction * 100,3:0}%  {p.Text}");
            }));
            Say($"완료: 카드 {manifest.CatalogCards}장, 그림 {manifest.CardArtFiles}개, 현지화 {manifest.LocalizationFiles}개 → {manifest.DurationSeconds:0.0}초");

            // Build the recogniser's art cache too, so the first reward screen after start-up is instant.
            var sw = Stopwatch.StartNew();
            var ok = IntegrationFactory.CreateRealRecognizer().Preload();
            Say(ok ? $"인식용 그림 캐시 준비: {sw.Elapsed.TotalSeconds:0.0}초" : "인식용 그림 캐시를 만들지 못했습니다 (app.log 참고)");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLog.Error("--prepare failed", ex);
            Say($"실패: {ex.Message}");
            return 1;
        }
    }

    private static void Say(string line)
    {
        AppLog.Info("[prepare] " + line);
        try
        {
            Console.WriteLine(line);
        }
        catch (IOException)
        {
            // no console (started from Explorer): app.log has it
        }
    }

    /// <summary>Calls back on the reporting thread (here: the same thread), unlike <see cref="Progress{T}"/>.</summary>
    private sealed class InlineProgress(Action<PrepareProgress> report) : IProgress<PrepareProgress>
    {
        public void Report(PrepareProgress value) => report(value);
    }

    // The app is a WinExe (GUI subsystem), so Windows gives it no console. AttachConsole(-1) borrows the console of the
    // process that started us (cmd, PowerShell, dotnet run) so Console.WriteLine shows up there. If output is redirected
    // to a file/pipe, the inherited handles already work and the call simply fails harmlessly.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    private static void AttachToParentConsole()
    {
        const int AttachParentProcess = -1;
        AttachConsole(AttachParentProcess);
    }
}

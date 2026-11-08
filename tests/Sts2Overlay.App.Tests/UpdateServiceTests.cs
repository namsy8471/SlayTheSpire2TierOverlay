using System.IO;
using System.Net;
using System.Net.Http;
using Sts2Overlay.App.Infrastructure;
using Sts2Overlay.App.Services;

namespace Sts2Overlay.App.Tests;

/// <summary>
/// The tray "업데이트 확인" must always answer with a visible message, whatever happens (a fake client stands in for Velopack).
/// </summary>
public class UpdateServiceTests
{
    private sealed class FakeClient : IUpdateClient
    {
        public bool IsInstalled { get; init; } = true;
        public string? CurrentVersion { get; init; } = "0.1.0";
        public string? Available { get; set; }
        public Exception? CheckError { get; set; }
        public Exception? DownloadError { get; set; }
        public TaskCompletionSource? CheckGate { get; set; }
        public int Checks { get; private set; }
        public bool? AppliedWithRestart { get; private set; }

        public async Task<string?> CheckAsync()
        {
            Checks++;
            if (CheckGate is not null) await CheckGate.Task;
            if (CheckError is not null) throw CheckError;
            return Available;
        }

        public Task DownloadAsync() => DownloadError is null ? Task.CompletedTask : Task.FromException(DownloadError);

        public string? ReleaseNotes { get; set; }

        public void ApplyOnExit(bool restart) => AppliedWithRestart = restart;
    }

    private DateTime _now = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    private (UpdateService Service, List<string> Messages) Create(FakeClient client)
    {
        var service = new UpdateService(new AppSettings(), client, () => _now);
        var messages = new List<string>();
        service.Message += m => { lock (messages) messages.Add(m); };
        return (service, messages);
    }

    [Fact]
    public async Task Up_to_date_says_checking_then_the_current_version()
    {
        var (service, messages) = Create(new FakeClient());

        await service.CheckNowAsync();

        Assert.Equal(["업데이트 확인 중…", "최신 버전입니다 (v0.1.0)."], messages);
        Assert.False(service.IsChecking);
        Assert.Null(service.ReadyVersion);
    }

    [Fact]
    public async Task New_version_is_downloaded_announced_and_ready_to_apply()
    {
        var client = new FakeClient { Available = "0.1.1" };
        var (service, messages) = Create(client);

        await service.CheckNowAsync();

        Assert.Equal(3, messages.Count);
        Assert.Equal("업데이트 확인 중…", messages[0]);
        Assert.Equal("새 버전 v0.1.1 다운로드 중…", messages[1]);
        Assert.StartsWith("새 버전 v0.1.1 다운로드 완료 — 재시작하면 적용됩니다", messages[2]);
        Assert.Equal("0.1.1", service.ReadyVersion);

        Assert.True(service.RequestApplyAndRestart());
        service.ApplyOnExit();
        Assert.True(client.AppliedWithRestart);
    }

    [Fact]
    public async Task Clicking_again_when_an_update_is_ready_repeats_how_to_apply_without_checking()
    {
        var client = new FakeClient { Available = "0.1.1" };
        var (service, messages) = Create(client);
        await service.CheckNowAsync();
        messages.Clear();
        _now = _now.AddHours(1);

        await service.CheckNowAsync();

        Assert.Single(messages);
        Assert.StartsWith("새 버전 v0.1.1 다운로드 완료", messages[0]);
        Assert.Equal(1, client.Checks);
    }

    private const string Notes = """
        ## 새 기능
        - 빌드 탭 카드 툴팁에 **효과 문장** 표시 (#17)
        - 업데이트 확인 결과를 트레이 알림으로 (#13, #14)
        - 세 번째 줄은 안 나옴

        ---
        - English summary is never shown
        """;

    [Fact]
    public async Task Download_complete_message_carries_the_first_lines_of_the_release_notes()
    {
        var client = new FakeClient { Available = "0.1.2", ReleaseNotes = Notes };
        var (service, messages) = Create(client);

        await service.CheckNowAsync();

        Assert.Equal("빌드 탭 카드 툴팁에 효과 문장 표시 · 업데이트 확인 결과를 트레이 알림으로", service.ReadySummary);
        Assert.EndsWith("\n바뀐 점: 빌드 탭 카드 툴팁에 효과 문장 표시 · 업데이트 확인 결과를 트레이 알림으로", messages[^1]);

        // Clicking again repeats it.
        messages.Clear();
        _now = _now.AddHours(1);
        await service.CheckNowAsync();
        Assert.Contains("바뀐 점:", messages.Single());
    }

    [Fact]
    public async Task Download_complete_message_without_notes_has_no_summary_line()
    {
        var (service, messages) = Create(new FakeClient { Available = "0.1.2" });

        await service.CheckNowAsync();

        Assert.Null(service.ReadySummary);
        Assert.DoesNotContain('\n', messages[^1]);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("첫 공개 버전입니다.\n\n## 설치\n그냥 문단", null)]
    [InlineData("* [링크](https://example.com) 글자\n* `코드` _강조_", "링크 글자 · 코드 강조")]
    [InlineData("- 하나 (#9)\n- 둘", "하나 · 둘")]
    public void Summary_takes_list_items_only(string? markdown, string? expected)
        => Assert.Equal(expected, ReleaseNotesSummary.FromMarkdown(markdown));

    [Fact]
    public void Summary_is_cut_to_fit_a_balloon()
    {
        var summary = ReleaseNotesSummary.FromMarkdown("- " + new string('가', 300));
        Assert.NotNull(summary);
        Assert.Equal(ReleaseNotesSummary.DefaultMaxLength, summary.Length);
        Assert.EndsWith("…", summary);
        // The whole balloon stays under Windows' 255-character limit.
        Assert.True(UpdateService.ReadyText("10.20.30-beta.1", summary).Length <= 255);
    }

    [Fact]
    public async Task Exit_without_restart_applies_silently()
    {
        var client = new FakeClient { Available = "0.1.1" };
        var (service, _) = Create(client);
        await service.CheckNowAsync();

        service.ApplyOnExit();

        Assert.False(client.AppliedWithRestart);
    }

    [Fact]
    public async Task Check_failure_shows_the_reason()
    {
        var client = new FakeClient { CheckError = new HttpRequestException("No such host is known.") };
        var (service, messages) = Create(client);

        await service.CheckNowAsync();

        Assert.Equal(["업데이트 확인 중…", "업데이트 확인 실패: 인터넷에 연결할 수 없습니다."], messages);
        Assert.False(service.IsChecking);
    }

    [Fact]
    public async Task Download_failure_names_the_version()
    {
        var client = new FakeClient { Available = "0.1.1", DownloadError = new IOException("disk full") };
        var (service, messages) = Create(client);

        await service.CheckNowAsync();

        Assert.Equal("v0.1.1 다운로드 실패: 파일을 쓰지 못했습니다 (disk full).", messages[^1]);
        Assert.Null(service.ReadyVersion);
    }

    [Fact]
    public async Task Not_installed_explains_dev_builds_do_not_update()
    {
        var client = new FakeClient { IsInstalled = false, CurrentVersion = null };
        var (service, messages) = Create(client);

        await service.CheckNowAsync();

        Assert.Single(messages);
        Assert.StartsWith("개발용 실행(v", messages[0]);
        Assert.Contains("설치판(Setup.exe)에서만 업데이트됩니다", messages[0]);
        Assert.Equal(0, client.Checks);
    }

    [Fact]
    public async Task A_second_click_within_a_minute_says_so()
    {
        var client = new FakeClient();
        var (service, messages) = Create(client);
        await service.CheckNowAsync();
        messages.Clear();
        _now = _now.AddSeconds(20);

        await service.CheckNowAsync();

        Assert.Equal(["방금 확인했습니다 (현재 v0.1.0). 1분 뒤에 다시 눌러 주세요."], messages);
        Assert.Equal(1, client.Checks);
    }

    [Fact]
    public async Task While_checking_the_state_is_busy_and_a_second_click_is_answered()
    {
        var client = new FakeClient { CheckGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (service, messages) = Create(client);
        var changes = new List<bool>();
        service.StateChanged += () => { lock (changes) changes.Add(service.IsChecking); };

        var first = service.CheckNowAsync();
        Assert.True(service.IsChecking);
        await service.CheckNowAsync();
        Assert.Contains("이미 업데이트를 확인하는 중입니다…", messages);

        client.CheckGate.SetResult();
        await first;

        Assert.False(service.IsChecking);
        Assert.Equal([true, false], changes);
        Assert.Equal(1, client.Checks);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "GitHub 요청 한도를 넘었습니다. 1시간쯤 뒤에 다시 시도해 주세요.")]
    [InlineData(HttpStatusCode.TooManyRequests, "GitHub 요청 한도를 넘었습니다. 1시간쯤 뒤에 다시 시도해 주세요.")]
    [InlineData(HttpStatusCode.NotFound, "업데이트 주소에서 릴리스를 찾지 못했습니다 (설정의 updateUrl 확인).")]
    [InlineData(HttpStatusCode.BadGateway, "서버 응답 오류 (502).")]
    public void Http_errors_are_explained_even_when_wrapped(HttpStatusCode code, string expected)
    {
        var wrapped = new InvalidOperationException("velopack", new HttpRequestException("x", null, code));

        Assert.Equal(expected, UpdateService.DescribeError(wrapped));
    }

    [Fact]
    public void Unknown_errors_keep_a_short_message()
    {
        var text = UpdateService.DescribeError(new InvalidOperationException(new string('a', 200)));

        Assert.EndsWith("… (자세한 내용은 로그)", text);
        Assert.True(text.Length < 150);
    }
}

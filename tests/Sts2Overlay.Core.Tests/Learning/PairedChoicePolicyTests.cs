using System.Security.Cryptography;
using System.Text.Json;
using Sts2Overlay.Core.Events;
using Sts2Overlay.Core.Learning;
using Sts2Overlay.Core.Model;
using Sts2Overlay.Core.Scoring;

namespace Sts2Overlay.Core.Tests.Learning;

public class PairedChoicePolicyTests
{
    private static string PackDirectory => Path.Combine(TestPaths.RepoRoot, "data", "packs", "v0.111.0");
    private static string ModelDirectory => Path.Combine(PackDirectory, "model", "run-baseline");
    private static JsonDocument Fixtures() => JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "a0-choice-parity.json")));

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Malformed_optional_models_have_a_recoverable_load_error(string json) =>
        Assert.Throws<InvalidDataException>(() => PairedChoicePolicy.Load(json));
    private static RunState Run(JsonElement screen) => new()
    {
        Character = "CHARACTER.IRONCLAD", Ascension = 0, Act = screen.GetProperty("act").GetInt32(),
        Floor = screen.GetProperty("floor").GetInt32(), Hp = screen.GetProperty("hp").GetInt32(),
        MaxHp = screen.GetProperty("max_hp").GetInt32(), Gold = screen.GetProperty("gold").GetInt32(),
        ActBoss = screen.TryGetProperty("act_boss", out var boss) ? boss.GetString() : null,
        Deck = screen.GetProperty("deck").EnumerateArray().Select(c => new DeckCard(new(c[0].GetString()!, c[1].GetInt32()))).ToArray(),
        Relics = screen.GetProperty("relics").Deserialize<string[]>()!,
    };

    [Fact]
    public void All_synthetic_research_predictions_and_choices_match()
    {
        var models = new[] { "ancient", "event" }.ToDictionary(k => k, k => PairedChoicePolicy.Load(File.ReadAllText(Path.Combine(ModelDirectory, k + "-policy.json"))));
        using var fixtures = Fixtures();
        var count = 0; var changed = 0;
        var deployed = AdoptedEventChoices.LoadDirectory(PackDirectory)!;
        foreach (var item in fixtures.RootElement.GetProperty("cases").EnumerateArray())
        {
            var screen = item.GetProperty("screen"); var run = Run(screen);
            var model = models[item.GetProperty("model").GetString()!];
            var candidates = screen.GetProperty("candidates").EnumerateArray().Select(c => new VisibleEventCandidate(c.GetProperty("id").GetString()!,
                c.TryGetProperty("content", out var content) ? content.GetString()! : "")).ToArray();
            var rule = screen.GetProperty("chosen").GetInt32();
            var eventId = screen.GetProperty("event").GetString()!;
            foreach (var expected in item.GetProperty("recommendations").EnumerateArray())
            {
                var prediction = model.Predict(run, eventId, candidates, rule, expected.GetProperty("index").GetInt32());
                Assert.Equal(expected.GetProperty("gain").GetDouble(), prediction.Gain, 10);
                Assert.Equal(expected.GetProperty("uncertainty").GetDouble(), prediction.Uncertainty, 10);
                Assert.Equal(expected.GetProperty("supported").GetBoolean(), prediction.Supported);
            }
            var picked = model.Pick(run, eventId, candidates, rule, item.GetProperty("alternatives").Deserialize<int[]>()!);
            Assert.Equal(item.GetProperty("picked").GetInt32(), picked);
            var options = candidates.Select(c => new EventOptionDef
            {
                Key = "SYNTHETIC.INITIAL." + (model.Kind == "ancient" ? c.Content : c.Id),
                Effects = model.Kind == "ancient" ? [new EventEffect { Type = EventEffectTypes.Relic, Relic = "RELIC." + c.Content }] : [],
            }).ToArray();
            var evaluation = new EventEvaluation
            {
                Event = new() { Id = eventId, Kind = model.Kind, Pages = [new() { Page = "INITIAL", Options = options }] },
                Page = "INITIAL", OptionsExact = true,
                Options = options.Select((o, i) => new EventOptionEvaluation { Option = o, Score = new([]), Rank = i + 1, IsBest = i == rule }).ToArray(),
            };
            var runKey = "synthetic-" + count;
            deployed.Observe(runKey, run with { Act = 1 }, null);
            deployed.Observe(runKey, run, eventId);
            var actual = deployed.Apply(evaluation, run, true);
            Assert.True(actual.UsedAdoptedPolicy);
            Assert.Equal(options[picked].Key, actual.Best!.Option.Key);
            count++; if (picked != rule) changed++;
        }
        Assert.Equal(380, count); Assert.Equal(89, changed);
    }

    [Theory]
    [InlineData("ancient-policy.json", "ce4b928c923ae474cc3ae422a71eb4e392e11bcb60595f602833807585ae250e")]
    [InlineData("event-policy.json", "7a53c50c9c25c0520798dab5c762a90ed5ee340ddaa4c29ad9536be2cd5d3854")]
    public void Published_models_are_the_adopted_frozen_bytes(string file, string sha) =>
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(ModelDirectory, file)))));

    [Fact]
    public void Deployment_keeps_first_opportunity_and_abstains_after_missed_screens()
    {
        using var fixtures = Fixtures();
        var item = fixtures.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("model").GetString() == "event"
            && c.GetProperty("picked").GetInt32() != c.GetProperty("screen").GetProperty("chosen").GetInt32());
        var screen = item.GetProperty("screen"); var run = Run(screen);
        var eventId = screen.GetProperty("event").GetString()!;
        var options = screen.GetProperty("candidates").EnumerateArray().Select(c => new EventOptionDef { Key = "X.INITIAL." + c.GetProperty("id").GetString() }).ToArray();
        var evaluation = new EventEvaluation
        {
            Event = new() { Id = eventId, Pages = [new() { Page = "INITIAL", Options = options }] },
            Page = "INITIAL", OptionsExact = true,
            Options = options.Select((o, i) => new EventOptionEvaluation { Option = o, Score = new([]), Rank = i + 1, IsBest = i == screen.GetProperty("chosen").GetInt32() }).ToArray(),
        };
        var policy = AdoptedEventChoices.LoadDirectory(PackDirectory)!;
        policy.Observe("synthetic", run, eventId); // app started in act 2: first opportunity unknown
        Assert.False(policy.Apply(evaluation, run, true).UsedAdoptedPolicy);
        policy.Observe("fresh", run with { Act = 1 }, null);
        policy.Observe("fresh", run, eventId);
        var result = policy.Apply(evaluation, run, true);
        Assert.True(result.UsedAdoptedPolicy);
        Assert.Equal(options[item.GetProperty("picked").GetInt32()].Key, result.Best!.Option.Key);
        Assert.Equal(result.Best.Option.Key, policy.Apply(evaluation, run with { Hp = 1 }, true).Best!.Option.Key);
        Assert.False(policy.Apply(evaluation, run, true, choiceAlreadyMade: true).UsedAdoptedPolicy);
        var later = run with { Floor = run.Floor + 1 };
        policy.Observe("fresh", later, eventId);
        Assert.False(policy.Apply(evaluation, later, true).UsedAdoptedPolicy);
        policy.Observe("missed", run with { Act = 1 }, null);
        policy.Observe("missed", run, eventId); // no exact options observed before leaving
        policy.Observe("missed", later, eventId);
        Assert.False(policy.Apply(evaluation, later, true).UsedAdoptedPolicy);
    }
}

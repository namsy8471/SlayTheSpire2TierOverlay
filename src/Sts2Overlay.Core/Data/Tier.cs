namespace Sts2Overlay.Core.Data;

/// <summary>Single-letter tier shown on the badge (PLAN §6.2). Numeric values live in <see cref="Weights.TierValue"/>.</summary>
public enum Tier
{
    S,
    A,
    B,
    C,
    D,
}

public static class Roles
{
    public const string Frontload = "frontload";
    public const string Aoe = "aoe";
    public const string Block = "block";
    public const string Scaling = "scaling";
    public const string Draw = "draw";
    public const string Energy = "energy";
    public const string ExhaustFodder = "exhaust_fodder";

    public static readonly IReadOnlyList<string> All = [Frontload, Aoe, Block, Scaling, Draw, Energy, ExhaustFodder];
}

public static class CardTypes
{
    public const string Attack = "attack";
    public const string Skill = "skill";
    public const string Power = "power";
    public const string Status = "status";
    public const string Curse = "curse";
    public const string Quest = "quest";
}

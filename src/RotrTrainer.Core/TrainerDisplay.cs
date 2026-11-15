using System.Globalization;

namespace RotrTrainer.Core;

public sealed record FeatureDisplay(string Name, string Description, bool IsOn, bool WaitsForArming, bool IsAlwaysOn, bool HasFailed);

/// <summary>What the window shows for one engine status. Pure, so the wording is tested without a window.</summary>
public sealed record TrainerDisplay(
    string StateWord,
    string StateDetail,
    bool IsArmed,
    string ArmActionText,
    bool CanToggleArm,
    string Money,
    string GeneralPoints,
    string Rank,
    IReadOnlyList<FeatureDisplay> Features,
    string Footer)
{
    private const string Unreadable = "–";

    public static TrainerDisplay From(TrainerStatus status) => new(
        StateWord: status.IsArmed ? "ARMED" : "DISARMED",
        StateDetail: DetailFor(status),
        IsArmed: status.IsArmed,
        ArmActionText: status.IsArmed ? "Disarm" : "Arm",
        CanToggleArm: status.IsArmed || status.MayArm,
        Money: Format(status.Money),
        GeneralPoints: Format(status.SciencePurchasePoints),
        Rank: Format(status.RankLevel),
        Features: status.Features.Select(feature => new FeatureDisplay(
            feature.Name,
            feature.Failure is null ? feature.Description : $"Not applied: {feature.Failure}",
            IsOn: feature.Enabled,
            WaitsForArming: feature.Enabled && feature.NeedsArming && !status.IsArmed,
            IsAlwaysOn: !feature.NeedsArming,
            HasFailed: feature.Failure is not null)).ToList(),
        Footer: !status.IsAttached ? "Waiting for the game"
            : status.GenToolPresent ? "Game found · GenTool present"
            : "Game found");

    private static string DetailFor(TrainerStatus status)
    {
        if (!status.IsAttached)
            return "Start the game. The trainer attaches by itself.";
        if (status.BuildName is null)
            return $"This game build is not supported: {status.Refusal ?? status.GuardReason}.";
        if (status.IsArmed)
            return "Changes apply to you only. The AI plays by the normal rules.";
        return status.MayArm
            ? "Ready. Press F5 to arm for this match."
            : $"Cannot arm yet: {status.GuardReason}.";
    }

    private static string Format(uint? value) =>
        value is { } number ? number.ToString("N0", CultureInfo.InvariantCulture) : Unreadable;
}

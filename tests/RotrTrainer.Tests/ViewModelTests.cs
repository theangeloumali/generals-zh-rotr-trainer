using RotrTrainer.Core;

namespace RotrTrainer.Tests;

public static class ViewModelTests
{
    private static readonly FeatureStatus Money = new("Money", "Never below 1,000,000", Enabled: true, NeedsArming: true);
    private static readonly FeatureStatus Power = new("Unlimited power", "Base never runs short", Enabled: false, NeedsArming: true);

    private static TrainerStatus Status(
        bool attached = true, string? build = "modded.exe (Rise of the Reds, ZH 1.04)", string? refusal = null,
        bool armed = false, bool mayArm = false, bool genTool = false, string reason = "the game is at the main menu",
        uint? money = null) =>
        new(attached, build, refusal, armed, mayArm, genTool, reason, money, null, null, [Money, Power]);

    public static void TestArmedShowsTheArmedWordAndOffersDisarm()
    {
        var display = TrainerDisplay.From(Status(armed: true, mayArm: true));
        Check.Equal("ARMED", display.StateWord, "state");
        Check.Equal("Disarm", display.ArmActionText, "action");
        Check.True(display.IsArmed && display.CanToggleArm, "armed and can disarm");
    }

    public static void TestReadyToArmSaysSoAndEnablesTheButton()
    {
        var display = TrainerDisplay.From(Status(mayArm: true));
        Check.Equal("DISARMED", display.StateWord, "state");
        Check.Equal("Arm", display.ArmActionText, "action");
        Check.True(display.CanToggleArm, "button enabled");
        Check.True(display.StateDetail.Contains("F5"), display.StateDetail);
    }

    public static void TestCannotArmShowsTheReasonAndDisablesTheButton()
    {
        var display = TrainerDisplay.From(Status(mayArm: false, reason: "the game is at the main menu"));
        Check.True(!display.CanToggleArm, "button disabled");
        Check.True(display.StateDetail.Contains("the game is at the main menu"), display.StateDetail);
    }

    public static void TestNoGameAsksThePlayerToStartIt()
    {
        var display = TrainerDisplay.From(Status(attached: false, build: null, reason: "no game attached"));
        Check.True(!display.CanToggleArm, "button disabled");
        Check.True(display.StateDetail.Contains("Start the game"), display.StateDetail);
        Check.Equal("Waiting for the game", display.Footer, "footer");
    }

    public static void TestUnsupportedBuildIsNamedAndCannotArm()
    {
        var display = TrainerDisplay.From(Status(build: null, refusal: "Game.dat is not a supported build", reason: "Game.dat is not a supported build"));
        Check.True(!display.CanToggleArm, "button disabled");
        Check.True(display.StateDetail.Contains("not a supported build"), display.StateDetail);
    }

    public static void TestUnreadableValuesShowADash()
    {
        var display = TrainerDisplay.From(Status());
        Check.Equal("–", display.Money, "money");
        Check.Equal("–", display.GeneralPoints, "points");
        Check.Equal("–", display.Rank, "rank");
    }

    public static void TestMoneyIsGroupedByThousands()
    {
        var display = TrainerDisplay.From(Status(armed: true, mayArm: true, money: 1_000_000));
        Check.Equal("1,000,000", display.Money, "money");
    }

    public static void TestEnabledFeatureWaitsForArmingWhileDisarmed()
    {
        var display = TrainerDisplay.From(Status(mayArm: true));
        Check.True(display.Features[0].IsOn && display.Features[0].WaitsForArming, "money waits");
        Check.True(!display.Features[1].IsOn && !display.Features[1].WaitsForArming, "power is simply off");
    }

    public static void TestEnabledFeatureIsActiveWhileArmed()
    {
        var display = TrainerDisplay.From(Status(armed: true, mayArm: true));
        Check.True(display.Features[0].IsOn && !display.Features[0].WaitsForArming, "money active");
    }

    public static void TestAFailedFeatureShowsItsFailureInsteadOfItsDescription()
    {
        var failed = new FeatureStatus("Crash fix", "Fixes a known engine crash", Enabled: true, NeedsArming: false, Failure: "cannot read the site");
        var status = Status() with { Features = [failed] };
        var row = TrainerDisplay.From(status).Features[0];
        Check.Equal("Not applied: cannot read the site", row.Description, "failure shown");
        Check.True(row.HasFailed, "marked failed");
    }

    public static void TestFooterNamesGenToolWhenPresent()
    {
        Check.Equal("Game found · GenTool present", TrainerDisplay.From(Status(genTool: true)).Footer, "with GenTool");
        Check.Equal("Game found", TrainerDisplay.From(Status(genTool: false)).Footer, "without");
    }
}

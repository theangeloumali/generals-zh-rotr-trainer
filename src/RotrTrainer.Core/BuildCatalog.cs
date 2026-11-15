using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

[assembly: InternalsVisibleTo("RotrTrainer.Tests")]

namespace RotrTrainer.Core;

public sealed record CatalogResult
{
    private CatalogResult(GameAddresses? addresses, string? refusal)
    {
        Addresses = addresses;
        Refusal = refusal;
    }

    public GameAddresses? Addresses { get; }
    public string? Refusal { get; }

    internal static CatalogResult Resolved(GameAddresses addresses) => new(addresses, null);
    internal static CatalogResult Refused(string reason) => new(null, reason);
}

/// <summary>
/// Where an address comes from: match + Offset, an absolute 32-bit immediate at ImmediateAt, or the target of
/// the rel32 operand at Rel32At.
/// </summary>
internal sealed record SiteSignature(string Name, BytePattern Pattern, int? ImmediateAt = null, int Offset = 0, int? Rel32At = null);

/// <summary>Resolves addresses only for allow-listed builds, and only from signatures that match exactly once.</summary>
public static class BuildCatalog
{
    private static readonly Dictionary<string, string> AllowedBuilds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["4ea7e86baffbc94946f6e2f89f698812159b34b89876822de329af19bc28c887"] = "modded.exe (Rise of the Reds, ZH 1.04)",
        ["f37a4929f8d697104e99c2bcf46f8d833122c943afcd87fd077df641d344495b"] = "Game.dat (Zero Hour 1.04)",
    };

    private const string StartPowerRechargePrologue = "53 56 8B F1 57 8B 7E F4 8B 47 08 85 C0 74 ?? 8B 4E F8 85 C9 74 ?? E8";

    private static readonly BytePattern UpgradeCalcTimeToBuildPattern =
        BytePattern.Parse("D9 41 14 D8 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? C2 04 00");

    // SpecialPowerModule ctor: the inlined startPowerRecharge. +0Fh is mov ecx,[TheGameLogic]; the match ends at the join.
    private static readonly BytePattern CtorRechargePattern = BytePattern.Parse(
        "74 0D 8B 43 08 50 8B CD E8 ?? ?? ?? ?? EB 1F 8B 0D ?? ?? ?? ?? 8B 59 3C 8B 17 8B CF FF 52 14 8B C8 E8 ?? ?? ?? ?? 8B 40 18 03 C3 89 47 04");

    internal static readonly IReadOnlyList<SiteSignature> Sites =
    [
        // HUD money readout: mov eax,[ThePlayerList]; mov eax,[eax+0Ch]; ... mov ebx,[eax+38h]
        new(nameof(GameAddresses.PlayerListPointer),
            BytePattern.Parse("A1 ?? ?? ?? ?? 8B 40 0C 85 C0 74 ?? 8B 58 38 39 1D"), ImmediateAt: 1),
        // Energy::getProduction reads TheGameLogic->m_frame.
        new(nameof(GameAddresses.GameLogicPointer),
            BytePattern.Parse("A1 ?? ?? ?? ?? 8B 50 3C 3B 51 0C 73 03 33 C0 C3 8B 41 04 C3"), ImmediateAt: 1),
        new(nameof(GameAddresses.CalcTimeToBuild),
            BytePattern.Parse("83 EC 08 53 56 57 8B 7C 24 18 8B F1 8B 86 ?? ?? 00 00 56 6A 01")),
        new(nameof(GameAddresses.UpgradeCalcTimeToBuild), UpgradeCalcTimeToBuildPattern),
        new(nameof(GameAddresses.UpgradeBuildTimeConstant), UpgradeCalcTimeToBuildPattern, ImmediateAt: 5),
        new(nameof(GameAddresses.StartPowerRecharge), BytePattern.Parse(StartPowerRechargePrologue)),
        // startPowerRecharge+16h: call getControllingPlayer with ecx = the module's Object.
        new(nameof(GameAddresses.GetControllingPlayer),
            BytePattern.Parse(StartPowerRechargePrologue + " ?? ?? ?? ??"), Rel32At: 0x17),
        new(nameof(GameAddresses.SpecialPowerCtorRechargeSite), CtorRechargePattern, Offset: 0x0F),
        new(nameof(GameAddresses.SpecialPowerCtorRechargeSkip), CtorRechargePattern, Offset: 0x2E),
        // Player::update after call hasSufficientPower: neg al; sbb al,al; inc al (the inlined brown-out copy).
        new(nameof(GameAddresses.PlayerUpdateBrownOutSite),
            BytePattern.Parse("E8 ?? ?? ?? ?? F6 D8 1A C0 FE C0 88 44 24 13 8B CD 74 07"), Offset: 5),
        new(nameof(GameAddresses.GetEnergySupplyRatio), BytePattern.Parse("51 A1 ?? ?? ?? ?? 8B 50 3C 3B 51 0C 73 08")),
        // Whole function: radar toggle, then the per-object loop; rel32 calls and the callback push are wildcards.
        new(nameof(GameAddresses.OnPowerBrownOutChange),
            BytePattern.Parse(
                "8A 44 24 04 84 C0 56 57 8B F9 74 07 E8 ?? ?? ?? ?? EB 05 E8 ?? ?? ?? ?? 8B 87 A0 01 00 00 8B 30 3B F0 "
                + "74 1C 8B 4E 08 8D 44 24 0C 50 68 ?? ?? ?? ?? E8 ?? ?? ?? ?? 8B 36 3B B7 A0 01 00 00 75 E4 5F 5E C2 04 00")),
        // The site itself (mov edx,[esp+18h]), not the function start.
        new(nameof(GameAddresses.MoveAwayFromUnitNullSite),
            BytePattern.Parse("8B 54 24 18 8B 4A 64 74 65 83 78 08 17")),
        // privateMoveAwayFromUnit's first jne (+0Fh) goes to the shared early return: pop edi..ebx, ecx; ret 8.
        new(nameof(GameAddresses.MoveAwayFromUnitExit),
            BytePattern.Parse("51 53 55 56 8B F1 8A 86 F9 01 00 00 84 C0 57 0F 85 ?? ?? ?? ?? 8B 4E 0C E8 ?? ?? ?? ?? 84 C0 0F 84 "
                + "?? ?? ?? ?? 8B 06 8B CE FF 90 34 01 00 00 84 C0 0F 84 ?? ?? ?? ?? 8B 46 2C 8B 40 4C 85 C0 8B 54 24 18 8B 4A 64 74 ??"),
            Rel32At: 0x11),
    ];

    public static CatalogResult Resolve(string exePath)
    {
        byte[] file;
        try
        {
            file = File.ReadAllBytes(exePath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return CatalogResult.Refused($"cannot read {exePath}: {error.Message}");
        }

        string hash = Convert.ToHexString(SHA256.HashData(file));
        if (!AllowedBuilds.TryGetValue(hash, out string? buildName))
            return CatalogResult.Refused($"{Path.GetFileName(exePath)} (sha256 {hash.ToLowerInvariant()}) is not on the allow-list");
        if (!PeImage.TryParse(file, out var image))
            return CatalogResult.Refused($"{buildName} is not a valid PE32 image");
        return ResolveImage(image, buildName);
    }

    internal static CatalogResult ResolveImage(PeImage image, string buildName)
    {
        var resolved = new Dictionary<string, uint>();
        uint imageCheckAddress = 0;
        byte[] imageCheckBytes = [];
        var text = image.Text;
        foreach (var site in Sites)
        {
            int? offset = SignatureScanner.FindUnique(text, site.Pattern, out int count);
            if (offset is not int match)
                return CatalogResult.Refused($"{buildName}: {site.Name} signature has {count} matches, expected exactly 1");

            uint matchVa = image.TextVa + (uint)match;
            if (site.Name == nameof(GameAddresses.PlayerListPointer))
            {
                // The HUD money read: no feature patches it, so the live process must match the file here.
                imageCheckAddress = matchVa;
                imageCheckBytes = text.Slice(match, site.Pattern.Length).ToArray();
            }
            uint address = site switch
            {
                { ImmediateAt: int at } => BinaryPrimitives.ReadUInt32LittleEndian(text[(match + at)..]),
                { Rel32At: int at } => matchVa + (uint)at + 4 + (uint)BinaryPrimitives.ReadInt32LittleEndian(text[(match + at)..]),
                _ => matchVa + (uint)site.Offset,
            };
            if (!image.ContainsVa(address))
                return CatalogResult.Refused($"{buildName}: {site.Name} resolved to 0x{address:X8}, outside the image");
            resolved[site.Name] = address;
        }

        return CatalogResult.Resolved(new GameAddresses
        {
            BuildName = buildName,
            ImageCheckAddress = imageCheckAddress,
            ImageCheckBytes = imageCheckBytes,
            PlayerListPointer = resolved[nameof(GameAddresses.PlayerListPointer)],
            GameLogicPointer = resolved[nameof(GameAddresses.GameLogicPointer)],
            CalcTimeToBuild = resolved[nameof(GameAddresses.CalcTimeToBuild)],
            UpgradeCalcTimeToBuild = resolved[nameof(GameAddresses.UpgradeCalcTimeToBuild)],
            StartPowerRecharge = resolved[nameof(GameAddresses.StartPowerRecharge)],
            OnPowerBrownOutChange = resolved[nameof(GameAddresses.OnPowerBrownOutChange)],
            MoveAwayFromUnitNullSite = resolved[nameof(GameAddresses.MoveAwayFromUnitNullSite)],
            SpecialPowerCtorRechargeSite = resolved[nameof(GameAddresses.SpecialPowerCtorRechargeSite)],
            PlayerUpdateBrownOutSite = resolved[nameof(GameAddresses.PlayerUpdateBrownOutSite)],
            GetEnergySupplyRatio = resolved[nameof(GameAddresses.GetEnergySupplyRatio)],
            MoveAwayFromUnitExit = resolved[nameof(GameAddresses.MoveAwayFromUnitExit)],
            GetControllingPlayer = resolved[nameof(GameAddresses.GetControllingPlayer)],
            SpecialPowerCtorRechargeSkip = resolved[nameof(GameAddresses.SpecialPowerCtorRechargeSkip)],
            UpgradeBuildTimeConstant = resolved[nameof(GameAddresses.UpgradeBuildTimeConstant)],
        });
    }
}

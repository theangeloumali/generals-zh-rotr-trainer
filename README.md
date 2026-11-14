# Generals ZH RoTR Trainer

A single-player trainer for **Command & Conquer: Generals – Zero Hour** running the **Rise of the Reds** mod through GenLauncher.

It is a small Windows program that attaches to the running game and changes a few values for the local human player only. AI players are not affected.

## Status

Early and lightly tested. It has been used on one machine, with Rise of the Reds 1.87 Public Build 2.0 started from GenLauncher (which runs the game as `modded.exe`). The code has automated tests but has not had an independent review yet. Use it at your own risk.

## Features

| Hotkey | Feature | What it does |
| ------ | ------- | ------------ |
| F5 | Arm / disarm | Nothing is changed until the trainer is armed. It disarms itself when a match ends. |
| F6 | Money | Keeps your money at or above 1,000,000. |
| F7 | General's points | Keeps general's points available and promotes you on your next kill. |
| F8 | Fast build | Units and structures build in a fraction of the time. Upgrades keep their normal time. |
| F9 | Free build | Units and structures cost nothing. |
| F10 | Unlimited power | Your base never runs short of power, and a power sabotage ends within a moment. |

All five work by changing values in the game's memory. No game code and no game file is modified.

## Single-player only

- The trainer arms only in campaign and skirmish games, and only when the game has no network connection open.
- Close the trainer and restart the game before any LAN or online session.
- Replays of trained matches will not play back correctly. Do not share them.
- Any memory change desyncs a multiplayer game by design of the engine, so this cannot be used to cheat online.

## Requirements

- Windows 10 or 11, 64-bit
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- The game running as `modded.exe` (GenLauncher's default) or the retail 1.04 `game.dat`. Other builds are refused: the Steam 2025 build and the community `generalszh.exe` build are not supported.

## Using it

1. Unzip the release into a folder only administrators can write to, for example `C:\Program Files\RoTR-Trainer\`.
2. Start `RotrTrainer.exe` **before** the game. Windows asks for administrator rights: the game runs elevated under GenLauncher, and a non-elevated program cannot open it.
3. Start Rise of the Reds from GenLauncher and enter a skirmish. The trainer attaches by itself.
4. Press F5 to arm, then the feature hotkeys. Use the keyboard rather than clicking the trainer window while the game is running.

Starting the trainer while the game is already on screen, or keeping its window on top of the game, can leave the game's picture black. Start the trainer first.

## Known issues

- **Saving with fast build, free build or unlimited power on keeps the effect in the save**, even with the trainer closed. Turn them off before saving if you do not want that.
- **Unlimited power turned on while the base is already in low power** lifts at the next power change (build or sell any building).
- **Long matches with very large armies can crash the game.** The cause is not confirmed; the leading suspect is the 32-bit game running out of memory.
- **Experimental code patches** (`--code-patches`: instant upgrades, no cooldown, a fix for a known engine crash) black out the game's picture on the test machine and are off by default. The cause is not yet known.

## How it works

- The game binary is identified by its SHA-256 hash. Unknown builds are never written to.
- Addresses are found by byte signatures that must match exactly once. Nothing is hard-coded per machine.
- Every tick the trainer re-reads the local player from the game, checks its guards (game mode, no network endpoints, local player present, not an observer, not defeated) and only then applies the enabled features.
- The process is opened with the minimum access rights needed. There is no network code, no auto-update and no injected DLL.

Technical notes on the addresses and patch sites are in [`docs/anchors.md`](docs/anchors.md).

## Building

```
dotnet build RotrTrainer.sln -c Release
dotnet run --project tests/RotrTrainer.Tests
dotnet publish src/RotrTrainer.App -c Release -o publish
```

Requires the .NET 8 SDK. There are no NuGet dependencies. Some tests read the game's binaries from the default Steam install path and fail if the game is not installed there.

## Licence

No licence has been chosen yet. Until one is added, the code is published for reading only.

This project is not affiliated with Electronic Arts, the Rise of the Reds team, GenLauncher or GenTool.

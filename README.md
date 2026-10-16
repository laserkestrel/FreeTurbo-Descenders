# Descenders Cycling

A fitness mode for [Descenders](https://store.steampowered.com/app/681280/Descenders/) (Steam, PC). Ride a smart trainer and your watts move the bike: climbs get harder, descents are free speed, and an autopilot steers the course and turns the bike round at both ends so you can keep pedalling without holding a controller. ERG mode, structured workouts and training plans are built in, plus there is party mode to cheer you on.

> Status: An unofficial fan mod, not affiliated with or endorsed by the makers of Descenders.

If you enjoy it, you can [buy me a coffee](https://buymeacoffee.com/laserkestrel).

<!-- TODO (maintainer): how-to videos, social links. -->

## What you need

- **Descenders** on Steam (Windows) with **MelonLoader 0.5.7**.
- **A smart trainer**, connected with ANT+ or Bluetooth.
- Optional: a heart-rate strap, cadence sensor, power meter, or Xbox controller.

## The two parts

- **The mod** (`FreeTurbo-Descenders.dll`), a MelonLoader mod: it reads the course and the rider, drives the bike's speed from your power, steers, and draws the HUD. Optional Party mode for spectators at trackside. 
- **The bridge** (`FreeTurbo-Bridge.exe`): it talks to your trainer and sensors, sets the trainer's resistance from the road gradient, and runs ERG mode and workouts. Its window is where you choose devices, your weight and FTP, and the workout to ride.

## Building from source

- **.NET 8 SDK**.
- **Your own ANT+ network key** (free, by registering as an ANT+ adopter at [thisisant.com](https://www.thisisant.com/)). It is not in this repo. Put it in `bridge\config.json`, in the form of `bridge\config.example.json`, or in the `ANT_NETWORK_KEY` environment variable.
- If the game is not in `C:\Program Files (x86)\Steam\steamapps\common\Descenders`, copy `GamePath.props.example` to `GamePath.props` and change the path.

```
dotnet build -c Release
dotnet publish bridge\DescendersBridge\DescendersBridge.csproj -c Release -r win-x86 --self-contained true -o "<game folder>\DescendersBridge"
```

The first builds the mod and copies it into the game's `Mods` folder (close the game first). The second builds the bridge where the mod looks for it.

## Installing a release

1. Close Descenders.
2. Download the FreeTurbo-Descenders zip and unzip it anywhere.
3. Double-click `Install.cmd` and say Yes when Windows asks for administrator rights (the game lives under Program Files).

That's it, first install or update. The installer finds Descenders through Steam and checks each of these, only putting in what your PC is missing:

- **Microsoft Visual C++ runtimes**, x64 (needed by MelonLoader 0.5.7) and x86 (the ANT drivers are x86). Downloaded from Microsoft, and only run if Windows confirms Microsoft signed them.
- **MelonLoader 0.5.7**, the mod loader. Descenders needs this version, not the latest. Downloaded from the [MelonLoader release](https://github.com/LavaGang/MelonLoader/releases/tag/v0.5.7), checked against the known release file, and unblocked for you, so there's no right-click > Properties > Unblock. No need to start the game once first either.
- **FreeTurbo-Descenders** into `Descenders\Mods`, and **FreeTurbo-Bridge** into `Descenders\DescendersBridge`. Your settings are kept on an update.
- A **FreeTurbo-Bridge** shortcut on your desktop and in the Start menu.

Read the end of the window: it says `Install finished OK`, or in red what to do. If you already use a different MelonLoader version for other mods it stops and tells you, rather than replacing it.

No internet on the PC? Put [MelonLoader.x64.zip](https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip) next to `Install.ps1` in the unzipped folder, and install the [x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) and [x86](https://aka.ms/vs/17/release/vc_redist.x86.exe) Visual C++ runtimes yourself first.


## Instructions

Playing with, or without a turbo trainer, you can enable trackside spectators on any procedurally generated Descenders tracks (not bikeparks) by pressing F10 to enable or disable the "party mode". Party mode also turns night and day on generated courses (the hub and bike parks keep their own sky): a day lasts 12 minutes, starting again from each course's own time of day, with dusk and dawn fading in gradually and a head torch at night. Turning party mode off runs the sky back to the course's own time of day over a few seconds.
If you have a smart trainer, you can pair up your devices with my FreeTurbo-Bridge application.

**Pairing your trainer and sensors**

With your trainer powered on with bluetooth or ANT+ go to the "My Devices" tab of FreeTurbo-Bridge (the shortcut on your desktop or in the Start menu). Hit the "search for devices" button to begin. 
You should then find any nearby Trainers, HR monitors, Cadence and Power meters. 
Select the correct devices and hit save. 

The FreeTurbo-Bridge app assumes a default body weight and FTP (Functional Threshold Power). Update those and press save.
The Logs tab has various folders showing where you can keep workout and training plan files. 
Samples are within the repo: workouts in `workouts\` (copy them to your library's `Workouts` folder, keeping the `FreeTurbo_Starter` folder) and a two week sample training plan in `trainingplans\` (copy it to `TrainingPlans`). Search for "free zwo workouts" to find workout files that match how you want to train, or make your own zwo with an online editor (again search for free tools)
The resistance has some settings to personalise the Trainer feel and contains the settings I developed the mod on for my own trainer (Tacx Neo 2T).

With the workout folder configured, close and reopen FreeTurbo-Bridge, select a workout or training plan that you want, and start it: Descenders opens by itself. 
(Starting Descenders directly from Steam is also supported and will spawn the bridge silently with no gui)

When you finish a workout as part of a plan, your progress will be saved. 
A .fit file is also recorded to the activities folder for manual uploading to fitness tracking services.

## Credits

- **Descenders** by RageSquid, published by No More Robots.
- **[MelonLoader](https://github.com/LavaGang/MelonLoader)** by LavaGang, which bundles **[Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike.
- **[DescendersSandbox](https://github.com/NateHyden/DescendersSandboxModKit)**, whose techniques for finding the player and injecting steering this mod follows.
- **ANT+ / ANT Managed Library** by Garmin / Dynastream Innovations.

## Licence

Free for non-commercial use. The mod and the bridge are licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md): you may use, copy, change and share them for any non-commercial purpose. Commercial use needs separate permission.

Required Notice: Copyright 2026 Laserkestrel (https://github.com/laserkestrel)

That licence covers this project's own code only. The ANT libraries in `lib\ant` are redistributed under the ANT+ Shared Source Licence. The notice and the full licence text are in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). That licence forbids reverse engineering, decompiling or disassembling those DLLs.

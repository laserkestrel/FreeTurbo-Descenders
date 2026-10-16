# FreeTurbo-Descenders

A fitness mod for [Descenders](https://store.steampowered.com/app/681280/Descenders/) (Steam, PC).

Ride a Bluetooth or ANT+ smart trainer and your effort will move the virtual bike: climbs get harder on the hills, descents are free speed, and an autopilot steers the course and turns the bike round at the course ends so you can keep pedalling without holding a controller. ERG mode, basic structured workouts and training plans are included, plus there is party mode to cheer you on.

**No smart trainer?** You can still use my mod: party mode adds spectators and a changing time of day during a race, giving more atmosphere to career mode.

> Status: An unofficial fan mod, not affiliated with or endorsed by the makers of Descenders.

If you enjoy it, you can [buy me a coffee](https://buymeacoffee.com/laserkestrel).

<!-- TODO (maintainer): how-to videos, social links. -->

## Requirements

- A PC with ANT+ or Bluetooth connectivity.
- [MelonLoader 0.5.7](https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip), which itself needs the [Visual C++ 2015+ runtime (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- My own mod needs the [Visual C++ 2015+ runtime (x86)](https://aka.ms/vs/17/release/vc_redist.x86.exe).

My installer zip checks these prerequisites and downloads and installs any that are missing.

## Installation

1. Unzip the package.
2. Run `Install.cmd`.
3. Launch Descenders.

No internet on the PC? Put [MelonLoader.x64.zip](https://github.com/LavaGang/MelonLoader/releases/download/v0.5.7/MelonLoader.x64.zip) next to `Install.ps1` in the unzipped folder, and install the [x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) and [x86](https://aka.ms/vs/17/release/vc_redist.x86.exe) Visual C++ runtimes yourself first.

## Setup

### Your trainer and sensors

1. Run the **FreeTurbo-Bridge** application.
2. Go to the **My devices** tab.
3. Make sure your trainer is powered up, along with any sensor accessories (HR watch or strap, cadence sensor, power pedals).
4. Press **Search for devices**.
5. Pick your trainer, HR monitor, cadence sensor and power meter as required.
6. Hit **Save**.

### Rider and bike

Go to **Rider and bike** and enter your body weight, bike weight and bike type. There should be no need to change any other settings.

### Workouts

1. Go to the **Logs** tab and set where you want to keep your workout files. Search Google for `.zwo` workout files, which you can make or download from popular editor sites.
2. Head to the **Workouts** tab and select a workout. Clicking the workout graph or name will launch Descenders and begin the workout.

### Party mode

If you wish to play Descenders with the crowd enabled, simply press **F10** during a career mode event or workout and the crowd will spur you on.

> My mod currently starts Descenders immediately into a simple practice session to demonstrate the turbo trainer integration, rather than the lobby at Mount Palumbo.

## Main features

| Key | Feature |
|-----|---------|
| **F10** | **Party mode**: adds trackside spectators to cheer you on during career mode. |
| **F8** | **Autosteer**: automatically enabled during smart trainer sessions so you can focus on riding your bike, not the game. The in-game rider will perform a U-turn just before the finish and pedal back up the track, and repeat just before the start point. |

## Building from source

- **.NET 8 SDK**.
- **Your own ANT+ network key** (free, by registering as an ANT+ adopter at [thisisant.com](https://www.thisisant.com/)). It is not in this repo. Put it in `bridge\config.json`, in the form of `bridge\config.example.json`, or in the `ANT_NETWORK_KEY` environment variable.
- If the game is not in `C:\Program Files (x86)\Steam\steamapps\common\Descenders`, copy `GamePath.props.example` to `GamePath.props` and change the path.

```
dotnet build -c Release
dotnet publish bridge\DescendersBridge\DescendersBridge.csproj -c Release -r win-x86 --self-contained true -o "<game folder>\DescendersBridge"
```

The first builds the mod and copies it into the game's `Mods` folder (close the game first). The second builds the bridge where the mod looks for it.

## Credits

- **Descenders** by RageSquid, published by No More Robots.
- **[MelonLoader](https://github.com/LavaGang/MelonLoader)** by LavaGang, which bundles **[Harmony](https://github.com/pardeike/Harmony)** by Andreas Pardeike.
- **[DescendersSandbox](https://github.com/NateHyden/DescendersSandboxModKit)**, whose techniques for finding the player and injecting steering this mod follows.
- **ANT+ / ANT Managed Library** by Garmin / Dynastream Innovations.

## Licence

Free for non-commercial use. The mod and the bridge are licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE.md): you may use, copy, change and share them for any non-commercial purpose. Commercial use needs separate permission.

Required Notice: Copyright 2026 Laserkestrel (https://github.com/laserkestrel)

That licence covers this project's own code only. The ANT libraries in `lib\ant` are redistributed under the ANT+ Shared Source Licence. The notice and the full licence text are in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). That licence forbids reverse engineering, decompiling or disassembling those DLLs.

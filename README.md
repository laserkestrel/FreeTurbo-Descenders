# Descenders Cycling

A fitness mode for [Descenders](https://store.steampowered.com/app/681280/Descenders/) (Steam, PC). Ride a smart trainer and your watts move the bike: climbs get harder, descents are free speed, and an autopilot steers the course and turns the bike round at both ends so you can keep pedalling without holding a controller. ERG mode, structured workouts and training plans are built in.

> Status: proof of concept. An unofficial fan mod, not affiliated with or endorsed by the makers of Descenders.

<!-- TODO (maintainer): how-to videos, social links, donation links. -->

## What you need

- **Descenders** on Steam (Windows) with **MelonLoader 0.5.7**.
- **A smart trainer**, connected by ANT+ (with an ANT+ USB stick) or by Bluetooth.
- Optional: a heart-rate strap, a cadence sensor, a power meter, an Xbox-style controller.

## The two parts

- **The mod** (`DescendersCyclingPOC.dll`), a MelonLoader mod: it reads the course and the rider, drives the bike's speed from your power, steers, and draws the HUD.
- **The bridge** (`turbotrainer-bridge.exe`): it talks to your trainer and sensors, sets the trainer's resistance from the road gradient, and runs ERG mode and workouts. Its window is where you choose devices, your weight and FTP, and the workout to ride.

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

## Licences

The ANT libraries in `lib\ant` are redistributed under the ANT+ Shared Source Licence. The notice and the full licence text are in [`THIRD-PARTY-NOTICES.txt`](THIRD-PARTY-NOTICES.txt). That licence forbids reverse engineering, decompiling or disassembling those DLLs.

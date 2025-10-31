# Boombox Mod for 7 Days to Die

The Boombox mod adds a placeable stereo block that can stream custom audio inside 7 Days to Die worlds. It ships with networking support, simple play/stop controls, and server-side synchronization so every player hears the same track list.

## Features
- Placeable boombox block with interaction prompts for play, stop, and pickup.
- Custom audio playlist support driven by Unity asset bundles.
- Configurable sound definitions in `Config/sounds.xml` that align with the game's audio system.
- C# codebase (`Code/`) ready for Visual Studio or Rider with full IntelliSense.

## Repository Layout
- `Code/` - C# sources, project files, and build output.
- `Config/` - XML configuration patches for blocks and sound definitions.
- `Resources/` - Runtime assets such as the `Sounds.unity3d` bundle containing your music clips.

## How to Install
1. Copy the `Boombox` folder into your `7 Days To Die/Mods` directory (client and server).
2. Build or obtain a `.unity3d` audio bundle that contains your music clips, then place it in `Resources/` (replace or rename `Sounds.unity3d` as needed).
3. Update `Config/sounds.xml` so each `<sound_data>` entry references the clip names exported in your Unity bundle.
4. Restart the game/server with anticheat turned off. The new boombox block can be crafted or obtained in creative mode.

## Development Notes
- Open `Code/Boombox.csproj` in Visual Studio or Rider, or run `dotnet build` against it, to produce `Boombox.dll` in `Code/bin`.
- When iterating on audio, rebuild the Unity asset bundle and redeploy it to `Resources/` before testing in game.
- Backups (`*.bak`), temporary files, and build artifacts are excluded via `.gitignore` to keep the repository clean.

## License
This project inherits the licensing terms defined in `ModInfo.xml`. If you plan to redistribute, verify any additional requirements from the original mod author(s).

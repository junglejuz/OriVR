# Ori VR: Ori and the Blind Forest in VR

True stereo VR for Ori and the Blind Forest: Definitive Edition. Both eyes are drawn from the same game tick, so there is no alternate-eye shimmer and no warping. The game becomes a diorama floating in front of you: its real depth layers stay real, so backgrounds sit behind the stage and the foreground comes out toward you. You play with a gamepad; there are no motion controls.

**Download:** see [Releases](../../releases).

**Support:** Ori VR is free. If you enjoy it, you can [buy me a coffee](https://buymeacoffee.com/scubasteve2365).

## What you need

- The Steam version of Ori and the Blind Forest: Definitive Edition
- A PC VR headset running through SteamVR, version 2.17 or newer
- SteamVR set as your OpenXR runtime (SteamVR settings, OpenXR), because this game is 32-bit
- A gamepad

## Install

1. Unzip the release anywhere.
2. Double click `Install OriVR.bat` and type 1. It finds the game through Steam.
3. Start SteamVR, then start Ori from Steam. VR starts by itself.

To uninstall, run the same bat file and type 2. No game files are changed.

The mod is not signed, so Windows may warn you about the bat file. It is plain text, so you can read it first. Windows Smart App Control can block unsigned mod DLLs. If it is on and the game stays flat, that is the likely cause.

## Settings

Click both sticks at once to open the menu in the headset. The game pauses while it is open. Up and down choose a setting, left and right change it, A selects and B closes.

| Setting | What it does |
|---|---|
| Stage distance, Stage size | Where the stage sits and how big it is |
| Depth | How strong the 3D is. Higher makes the world feel smaller and deeper |
| Pop-out | Brings more of the scene toward you, and eases far backgrounds |
| Sharpen | Crisper edges. Too high looks harsh |
| Resolution | 100% matches your headset. Higher costs speed and adds little in this game |
| Recenter | Puts the stage in front of you again |

If far backgrounds strain your eyes, lower Depth or raise Pop-out.

## Good to know

- The game runs one tick per headset frame, so it follows your headset's refresh rate at normal game speed.
- Tested in a real headset on one setup: Pimax Dream Air, SteamVR, RTX 5090, at 72 Hz. Other headsets and graphics cards should work through OpenXR but have not been tried, and that includes Virtual Desktop's OpenXR runtime. Reports are welcome.
- There is no DLSS. The game is 32-bit, and DLSS needs a 64-bit game.
- The mod stops the game's telemetry.
- If something goes wrong, the log is `BepInEx\LogOutput.log` in the game folder. Please attach it to an issue.

## Support

Ori VR is free. If you enjoy it and want to support more VR mods, you can
[buy me a coffee](https://buymeacoffee.com/scubasteve2365). Thank you!

## Building from source

You need the game installed, Visual Studio's C++ tools with CMake, the .NET SDK, and the Khronos [OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK-Source/releases) for Windows. BepInEx 5 x86 must be installed in the game folder, because the build references it.

```
cmake -S native -B native/build -A Win32 -DOPENXR_DIR=<OpenXR loader folder>
cmake --build native/build --config Release
dotnet build OriVR.csproj -c Release -p:OriDir="<game folder>"
```

The output lands in `..\build`.

## Credits

Built on [BepInEx](https://github.com/BepInEx/BepInEx) (LGPL-2.1) and the Khronos [OpenXR loader](https://github.com/KhronosGroup/OpenXR-SDK-Source) (Apache-2.0). The sharpening pass follows the idea of AMD FidelityFX Contrast Adaptive Sharpening (MIT). Licences are in the release zip.

OriVR is MIT licensed, by scubasteve2365. This is a fan mod, not made or approved by Moon Studios or Xbox Game Studios. It contains no game files.

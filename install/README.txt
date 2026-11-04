OriVR
VR mod for Ori and the Blind Forest: Definitive Edition (Steam).
Native stereo, played with a gamepad, as a diorama floating in front of you.

You need
- The game on Steam.
- A PC VR headset that runs through SteamVR, with SteamVR 2.17 or newer.
- SteamVR set as your OpenXR runtime (SteamVR settings, OpenXR). The installer warns you if this looks missing.
- A gamepad.

Install
1. Unzip this folder anywhere.
2. Double click "Install OriVR.bat" and type 1.

Play
1. Start SteamVR.
2. Start Ori from Steam. VR starts by itself. The game may freeze for a few seconds while it connects.
3. Load a save. A stage floats in front of you.

Settings
Click both sticks at once to open the menu. The game pauses while it is open.
Up and down choose a setting. Left and right change it. A selects. B closes.
- Stage distance and Stage size: where the stage sits and how big it is.
- Depth: how strong the 3D is. Higher makes the world feel smaller and deeper. If your eyes strain on far backgrounds, lower it.
- Pop-out: moves more of the scene out toward you. It also eases far backgrounds.
- Sharpen: crisper edges. Too high looks harsh.
- Resolution: 100% matches your headset. Higher costs speed and adds little in this game.
- Recenter: puts the stage in front of you again.

Uninstall
Double click "Install OriVR.bat" and type 2. No game files are changed by this mod.

If something goes wrong
- The game stays flat: check SteamVR is set as your OpenXR runtime. Then look in BepInEx\LogOutput.log in the game folder.
- Windows warns about the bat file: this mod is not signed, so Windows may ask you to confirm. Read the bat file first if you like. It is plain text.
- Smart App Control: if it is on, Windows can block unsigned mod DLLs and the game runs flat. Windows does not make this easy to turn back on, so decide for yourself. There is no workaround in the mod.
- When SteamVR starts, its dashboard may appear in front of the stage. Close it as usual.
- Far backgrounds strain your eyes: lower Depth or raise Pop-out.

Tested on
Pimax Dream Air through SteamVR, RTX 5090, at 72 Hz. That is the only real headset tried so far. Others should work through OpenXR but have not been tried. AMD and Intel graphics have not been tried. Virtual Desktop's OpenXR runtime has not been tried with this 32-bit game.

Known limits
- Steam version only.
- No DLSS or other upscaler. The game is 32-bit.
- No motion controls. A gamepad is required.
- The game itself keeps many CPU threads busy, with or without this mod.

Notes
- The game runs one tick per headset frame, so it follows your headset's refresh rate. If your PC cannot keep up, SteamVR may halve the rate for a moment.
- The mod stops the game's telemetry.
- Settings are saved in BepInEx\config\com.scubasteve2365.orivr.cfg in the game folder.

Version 0.8.0. Credits and licences are in THIRD-PARTY-NOTICES.txt. OriVR is MIT licensed, by scubasteve2365.
This is a fan mod, not made or approved by Moon Studios or Xbox Game Studios.

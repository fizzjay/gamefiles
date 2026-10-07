# Orbus Flat Controls (IL2CPP / MelonLoader)

A client-side keyboard/mouse bridge intended to let the local player move the Orbus hand controllers and click/grab without physical VR controllers. It does not modify the local server or networking code.

## What I found in the supplied DLLs

The type metadata identifies the game scripts under `Il2CppMagicalActual`. The mod binds to the names present in the dump:

- `PlayerAvatar`: `localControl`, `leftController`, `rightController`, `grabberLeft`, and `grabberRight`
- `Grabber`: `isLocalGrabber`, `myAvatar`, and `myController`
- `OrbusTrackedController`: trigger/grip state and the `OnTriggerClicked` / `OnTriggerUnclicked` / `OnGripped` / `OnUngripped` handlers
- `PlayerRig.eyeCamera` for the local view, with `Camera.main` as a fallback

The uploaded assemblies are IL2CPP metadata/dummy DLLs, not a runnable copy of the game's generated proxy assemblies. The mod therefore finds objects by type/member name instead of compiling against those dump files. I can't launch the game or verify the runtime-specific reflection/event behavior in this repository.

## Build

Build against the target game's own MelonLoader-generated Unity assemblies, not the uploaded dummy DLLs. Install MelonLoader for the game and launch it once so `MelonLoader/Il2CppAssemblies` is generated. Then, from the repository root, run on a machine with the .NET 6 SDK:

```powershell
dotnet build .\OrbusFlatControls\OrbusFlatControls.csproj -c Release -p:GameDir="C:\Path\To\OrbusVR"
```

`GameDir` is the folder containing the game's executable and `MelonLoader` directory. If your MelonLoader files are in a non-standard location, pass `MelonLoaderRoot`, `MelonLoaderDll`, or `Il2CppAssembliesDir` as additional MSBuild properties.

Copy the resulting `OrbusFlatControls.dll` from `OrbusFlatControls/bin/Release/net6.0/` into the game's `Mods` folder, then start the game. Use a MelonLoader version compatible with the game's Unity/IL2CPP version and the matching .NET 6 runtime architecture.

## Controls

| Key / input | Action |
| --- | --- |
| `W A S D` | Move the right hand relative to the view |
| `Space` / `Ctrl` | Raise / lower the right hand |
| `I J K L` | Move the left hand relative to the view |
| `U` / `O` | Raise / lower the left hand |
| `E` / `T` | Right / left trigger |
| `Q` / `G` | Right / left grip |
| `Tab` | Select which hand receives mouse clicks |
| Left / right mouse button | Trigger / grip the selected hand; world-space UI clicks use the game's hand event path |
| `F6` | Capture/release the mouse; captured mouse movement looks around |
| `F1` | Open/close the small controls UI (mouse is released while it is open) |
| `F8` | Pause/resume this mod's hand controls |
| `F9` | Toggle Unity XR off/on for this session; off is the default for flatscreen |
| `R` / `Y` | Reset right / left hand position |
| `Backspace` | Reset both hand positions |
| `Shift` | Move hands more slowly for precise placement |
| `Esc` | Release the captured mouse |

Hand poses are clamped to a one-arm-length-ish area around the view. WASD is deliberately mapped to the right hand, not body locomotion; the mod does not rewrite player movement or server state. The normal keyboard remains available for text entry in game menus.

## Runtime notes

- The mod waits for a local `PlayerAvatar` and binds to controller references through that avatar (or its local `Grabber` components). The F1 panel shows whether binding succeeded.
- Mouse clicks call the controller's public click/grip handlers and mirror the controller state properties/fields when the generated proxy exposes them. If your game's proxy differs, check `MelonLoader/Latest.log` for binding/reflection warnings.
- F9 uses Unity's XR settings API by reflection. Some older XR providers only allow this setting at startup; if disabling XR blanks the game view, restore it with F9 or launch using the game's desktop/non-VR option.
- In-game world-space menu interaction depends on the game's own hand laser being active. The uploaded metadata cannot confirm the exact runtime UI event path, so that part needs an in-game test.

# MediaBridge Radio for Easy Delivery Co.

This BepInEx plugin routes MediaBridge's live audio through Easy Delivery Co.'s built-in custom radio channel. The game's radio source keeps its spatial audio, so the music moves with the truck and fades between inside and outside as it does for other stations.

When the custom station is selected (FM 99.9 by default), left/right skip to the previous/next MediaBridge track, down toggles play/pause and pauses the truck's radio audio source, and up is ignored while MediaBridge is selected so scan cannot tune away from it. The mod attaches its stream to the currently selected custom station (FM 99.9 by default), even if the game's custom-channel loader reports a different placeholder frequency. It uses the truck radio's AudioResource so the truck plays MediaBridge audio and the HUD displays `Title - Artist`. While selected, it bypasses the game's tower signal fade on both the main radio and truck radio player so streamed music does not fade in and out.

## Requirements

- Easy Delivery Co. on Steam, with BepInEx 5 Mono x64 installed.
- MediaBridge running on the same computer (default control/audio ports 48900 and 48901).
- A selectable custom radio station, FM 99.9 by default. The plugin keeps the game's normal radio tower unlock behavior.

## Install

1. Install BepInEx 5 Mono x64 in the Easy Delivery Co. game folder and run the game once.
2. Put `EasyDeliveryCoMediaBridge.dll` in `BepInEx/plugins/`.
3. In MediaBridge's control page, select the audio device receiving your music.
4. Start MediaBridge and launch the game. Tune to the custom station.
5. While tuned to FM 99.9, use left/right to skip tracks, down to toggle play/pause, and up is ignored while MediaBridge is selected.

If MediaBridge is not running, the plugin reconnects in the background. The game log records the connection state.

## Build

Build against the game's `EasyDeliveryCo_Data/Managed` assemblies and a BepInEx 5 Mono x64 install:

```powershell
dotnet build EasyDeliveryCo-MediaBridge.csproj -c Release `
  -p:GameDir="C:\path\to\Easy Delivery Co" `
  -p:BepInExDir="C:\path\to\Easy Delivery Co\BepInEx\core"
```

The output DLL is in `bin/Release/netstandard2.1/`.

## Notes

- `CustomStationFrequency`, `TrackButtonCooldown`, `ControlPort`, and `AudioPort` are configurable in `BepInEx/config/alexunknown37.easydeliveryco.mediabridge.cfg`. Ports are discovered from MediaBridge's `bridge.json` by default.
- The media audio path is the local float32 stream on port 48901; the plugin does not bundle MediaBridge.
- Linux/Steam Deck playability depends on the installed BepInEx setup and MediaBridge being reachable through Proton's localhost networking.

## Credits

- Easy Delivery Co. by Sam C. and Oro Interactive.
- BepInEx and HarmonyX.
- MediaBridge.

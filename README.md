# EX0Selector

A [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader) mod that
restructures Resonite's **ProtoFlux node browser** and the inspector's **component attacher**, gives both a fuzzy search bar and cascading columns.

## Install

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Put `EX0Selector.dll` into your `rml_mods` folder (next to `Resonite.exe`).
3. Start the game. The node browser and the component attacher use the new panel.

To remove it, delete the DLL.

## Config

Editable with [ResoniteModSettings](https://github.com/badhaloninja/ResoniteModSettings).

## Build

Requires .NET 10 SDK. The project auto-detects a Steam install at the default path and
deploys the DLL to its `rml_mods` after a successful build:

```
dotnet build -c Release
```

`-p:ResonitePath="D:/Games/Resonite/"`  Changes install path.
`-p:CopyToMods=false`                   Skips the deploy.

## License

MIT

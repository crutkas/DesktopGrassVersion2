# DesktopGrassVersion2

A proof of concept that rebuilds [DesktopGrass](https://github.com/crutkas/DesktopGrass) on the Windows
Composition visual layer (`Windows.UI.Composition`) instead of an immediate-mode Direct2D renderer.

The grass is a retained visual tree: every blade, flower and mushroom is a composition shape, and all
motion (ambient wind, mouse gusts, springy recovery, palette crossfades) is expressed as expression and
keyframe animations that DWM evaluates. The app itself is event-driven and mostly idle; it only does work
when you move the mouse over the grass, click, or change a setting.

> Status: playable grass milestone. Critters, sky (butterflies, fireflies, birds) and measurements against
> the original are next.

## Run it

Requires Windows 10 2004+ and the .NET 10 Desktop Runtime.

```powershell
dotnet build src\DesktopGrass.Composition -c Release -r win-arm64   # or win-x64
src\DesktopGrass.Composition\bin\Release\net10.0-windows10.0.19041.0\win-arm64\DesktopGrass.Composition.exe
```

## Play

- **Move the mouse** through the grass to push it around; it springs back.
- **Click** the grass to cut a patch (clippings fly; it regrows in a minute or so); click a **mushroom** to make it boing.
- **Tray icon** (right-click): Scene (Meadow, Desert, Winter, Autumn, Ocean), Motion (Full, or Calm which
  only reacts to you), Send a gust, Pause, Quit. Double-click the icon to pause.

The overlay is click-through, sits above the taskbar on every monitor, and hides itself for fullscreen
apps. Calm motion is used automatically when Windows animations are off, on battery saver, or while the
session is locked. Settings are saved to `%LOCALAPPDATA%\DesktopGrass.Composition\state.json`.

Useful flags: `--scene <name>`, `--motion full|calm`, `--log` (writes `log.txt` next to the settings),
`--demo` (scripted gust, cut and boing), `--no-hook`.

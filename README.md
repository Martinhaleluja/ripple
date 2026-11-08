<p align="center"><img src="Assets/logo-word.png" alt="Ripple" width="420"></p>

Live water on your Windows desktop. Move the cursor and the water parts around it, waves spread out and
bounce off the screen's edges, and clicking the desktop or an icon makes a splash. The wallpaper sits
underneath, bent by the water. Your icons stay on top and work normally.

## Using it

Ripple runs on 64-bit Windows and needs the free [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
Build it with the commands under **Building**, then run `publish\Ripple.exe`.

- **Tiny window:** choose a picture, go back to your Windows wallpaper, pause, or open settings.
- **Tray icon:** click to open, right-click for Pause and Quit. Closing the window only hides it.
- **Settings:** ripple strength, damping, cursor sensitivity, click splash, start with Windows, pause for
  full-screen apps, pause on battery, picture fit, water detail.

Choosing a picture here never changes your real Windows wallpaper. Quit Ripple and the desktop is back as it was.

Settings live in `%APPDATA%\Ripple\settings.json`, the log in `%LOCALAPPDATA%\Ripple\ripple.log`.

## How it works

- **Direct3D 11, not WPF, for the water.** The water has to draw inside Explorer's desktop window, behind
  the icons, where WPF can't render. On the graphics card the simulation is cheap and the CPU stays free.
  WPF only draws the small control window.
- **A real wave simulation** (`Shaders/Water.hlsl`): a height field on a grid, stepped 180 times a second with
  the wave equation. Solid-wall edges make the bounces. A wide stencil makes long waves outrun short ones, which
  is the dispersing look of real water, and viscosity kills fine ripples first. Drawing bends the wallpaper
  through the slopes, splits the colours slightly, and adds caustics and glints. Calm water adds nothing: it shows
  the prepared picture exactly.
- **Behind the icons** (`Core/DesktopHost.cs`): handles both the Windows 11 24H2 desktop layout and the older
  WorkerW one. The windows are disabled and hit-test transparent, so clicks can't land on them. They run on
  their own thread that only pumps messages, because a hang there could otherwise freeze Explorer.
- **Mouse** (`Core/MouseHook.cs`): a low-level hook that only notes positions and button presses. It stores
  nothing and blocks nothing.
- **Idle:** when the surface is flat the water thread sleeps and draws nothing. Moving the mouse over an app
  window doesn't wake the water.
- **Crashes:** the water windows die with the process, so the normal wallpaper shows again by itself.

## Tested so far

Run for real on Windows 11 (build 26200) with one 1920x1080 monitor at 125% scale. Not yet tried: more than one
monitor, Windows versions with the older desktop layout, recovery after Explorer restarts, and the automatic
pauses for full-screen apps and battery. Reports and fixes are welcome.

## Building

You need the .NET 8 SDK.

```bash
dotnet publish Ripple.csproj -c Release -o publish
```

`Ripple.exe --write-assets Assets` redraws the logo and icon from `UI/LogoArt.cs`.
`Ripple.exe --demo` drops splashes for 25 seconds without touching the mouse.
`Ripple.exe --quit` tells a running Ripple to quit cleanly. `--background` starts it without the window.

## Licences

Ripple's code is under the MIT licence: see [LICENSE](LICENSE).

- The bubble "Ripple" lettering is drawn from scratch in `UI/LogoArt.cs`; no font is involved.
- Inter (the interface font) is under the SIL Open Font License: `Assets/Fonts/Inter-OFL.txt`.
- Vortice.Windows (the Direct3D bindings) is under the MIT licence.

# Overtake Assistant

Experimental third-party ETS2LA plugin that coordinates overtaking by using the
existing indicator lane-change flow. It does not modify Pathfinding, Lane Assist,
or Adaptive Cruise Control.

## Current behavior

- Registers a `Toggle Overtake Assistant` control.
- Listens to game telemetry and traffic data.
- When enabled, looks for a slower vehicle 10-85 meters ahead in the current
  lane.
- If the truck is at least 5 km/h faster and the adjacent left lane appears
  clear, sends a short `lblinker` pulse through `GameOutput`.
- After passing the original target vehicle, sends a short `rblinker` pulse to
  request returning once the right lane is clear 70 meters ahead and 35 meters
  behind.
- After completion or abort, waits 10 seconds before checking for another
  overtake.
- Registers an ETS2LA overlay window with live status, target distance, speed
  delta, cooldown, and lane-clear checks.
- Registers optional AR markers that draw the current candidate or active target
  vehicle in the game overlay. During passing/returning it also draws the right
  lane return-clearance zone.
- Adjacent-lane safety checks scan the requested side only and include a
  vehicle-size buffer.

The actual lane-change execution remains owned by the existing Pathfinding,
Lane Assist, and ACC plugins.

The overlay window and AR markers can be toggled from the plugin settings page.

## Build

From the workspace root:

```powershell
$env:APPDATA = (Resolve-Path .).Path + '\.dotnet-appdata'
$env:DOTNET_CLI_HOME = (Resolve-Path .).Path + '\.dotnet-home'
$env:NUGET_PACKAGES = (Resolve-Path .).Path + '\.nuget-packages'
dotnet restore ThirdPartyPlugins\OvertakeAssistant\OvertakeAssistant.csproj --configfile ThirdPartyPlugins\OvertakeAssistant\NuGet.config
dotnet build ThirdPartyPlugins\OvertakeAssistant\OvertakeAssistant.csproj -c Release
```

## Install

Copy the built DLL to the portable release plugin directory. Manually installed
plugins must currently be placed directly under `Plugins/`; subfolders are only
used by manifest-installed plugins.

```powershell
Copy-Item ThirdPartyPlugins\OvertakeAssistant\bin\Release\net10.0\OvertakeAssistant.dll Release-ETS2LA-win-beta-Portable\current\Plugins\OvertakeAssistant.dll
```

Then restart ETS2LA, enable the plugin, bind `Toggle Overtake Assistant`, and
turn it on only after Pathfinding, Lane Assist, and ACC are already working.

## Safety limits

This first version is intentionally conservative and should be treated as a
closed-course prototype:

- It only requests one lane change via indicators.
- It cancels on paused assists, paused game, low speed, or manual brake/steer
  input during an overtake.
- It uses traffic positions and estimated truck heading only; it does not yet
  inspect road topology, lane legality, upcoming ramps, junctions, or road signs.

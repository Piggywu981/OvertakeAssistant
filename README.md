# Overtake Assistant (MOBIL)

Automatic overtaking for ETS2LA. This is the rewrite that replaced the legacy
blinker-pulse implementation (preserved on the [`legacy`](../../tree/legacy) branch).

## What changed vs. legacy

| | legacy (0.3.x) | this version (1.0+) |
|---|---|---|
| Decision | hand-rolled gap thresholds | **IDM/MOBIL** incentive + safety criteria (Kesting, Treiber & Helbing 2007) |
| Lane change | pulsed the game blinker and hoped Pathfinding reacted | sets `PlannedItem.targetLaneIndex` directly (the same mechanism user blinkers use) |
| Lane direction | assumed fixed 4.5 m lanes, never consulted the path | candidate lane world positions are resolved at runtime; **no road-side sign conventions are trusted** |
| Verification | lateral displacement only | PathLib commit signal (`laneIndex == requested && targetLaneIndex == 0`) **and** ≥ 1.5 m physical displacement in the requested direction |
| Abort | indicator un-pulse hack | resets `targetLaneIndex` (truck snaps back onto the planned lane, LaneAssist's cross-track PID smooths it) |
| Speed control | none | pass-feasibility pre-check: no pull-out unless the pass can complete before the target-lane leader |
| Metrics | none | one CSV row per attempt (completion/abort, gaps, follower deceleration, lateral accel) |

## Architecture

```
GameTelemetry (60Hz) ─┐
TrafficProvider      ─┼─► projection (signed Frenet + history-based speed along path)
Pathfinding.PlannedPathData ─┘
            │
            ▼
  Idle ──MOBIL incentive+safety──► RequestingLeft ──commit+displacement──► Passing
   ▲                                │ abort: gap closed / timeout /              │
   │                                │        oncoming / manual input             │ rear clear
   └──────────── Cooldown ◄─────────┴────── RequestingRight ◄────────────────────┘
```

- **Decision** (`MobilModel.cs`): pure IDM/MOBIL math. The incentive criterion
  `ã_c − a_c + p(ã_n − a_n + ã_o − a_o) ≥ a_th` plus safety `ã_n ≥ −b_safe`,
  gated by distance (only evaluate below `EvaluationGapGate`), hard time-headway
  gates, oncoming detection and a pass-feasibility check.
- **Execution**: `targetLaneIndex = laneIndex ± 1` (direction-aware, mirroring
  Pathfinding's blinker mapping). Pathfinding auto-indicates; PathLib generates
  the SmoothInOut transition over `5 × max(20, speedLimit)` meters; LaneAssist
  steers it. Completion = PathLib commit + physical displacement.
- **Direction resolution**: the two same-side neighbour candidates are tested by
  interpolating their lane centers into world space and checking which physical
  side of the truck they are on (`LaneCenterOnPhysicalSide`). This sidesteps the
  road-frame sign conventions entirely and also validates lane existence/bounds.
- **Speeds of traffic** are estimated from position history (60 Hz traffic
  stream), not from the raw quaternion, so the along-path speed sign is
  convention-free too — this is what powers oncoming detection.

## Metrics

Enable *Write metrics CSV* (on by default). Each attempt appends to
`Documents/ETS2LA/OvertakeAssistant/metrics.csv`:

`utc, outcome, reason, duration_s, original_lane, requested_lane, min_front_gap_m, min_rear_gap_m, min_rear_thw_s, follower_min_accel_ms2, max_lateral_accel_ms2`

Useful aggregates: completion rate vs abort rate (per `reason`), `follower_min_accel`
distribution (how hard cut-ins actually brake the AI — the realized `b_safe`),
`max_lateral_accel` for comfort.

## Tuning cheat sheet

| Symptom | Knob |
|---|---|
| Overtakes too rarely | lower `OvertakeBias`, raise `EvaluationGapGate`, lower `RearTimeHeadway` |
| Cuts people off | lower `SafeBrakingLimit`, raise `RearTimeHeadway`, raise `Politeness` |
| Sits in the outer lane too long | lower `ReturnBias` (more negative) / lower `ReturnClearTime` |
| Pulls out with no room to finish | raise `PassFeasibilityMargin` |
| Wrong direction on some roads | file a bug — direction is resolved geometrically, so this would be a real defect |

## Build

```powershell
# from ThirdPartyPlugins/
./build_all.ps1     # builds and copies the DLL to ETS2LA-win-release-Portable/current/Plugins
```

Requires the ETS2LA portable release at `../ETS2LA-win-release-Portable/current`
(compile-time references, `Private=false` — types resolve against the host at
runtime, PathLib via the default `AssemblyLoadContext` where libraries load).

## Runtime dependencies

- `tumppi066.pathlib` — lane change primitive (`PlannedItem.targetLaneIndex`)
- `tumppi066.pathfinding` — auto-indication + path publication
- `tumppi066.laneassist` — steering along the lane change spline
- ACC (recommended) — follows the new leader after the lane change commits
- Full self-driving driving mode + assist toggle must be on

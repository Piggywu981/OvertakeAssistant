using System.Numerics;
using ETS2LA.Backend.Events;
using ETS2LA.Controls;
using ETS2LA.Game.Output;
using ETS2LA.Game.SDK;
using ETS2LA.Game.Telemetry;
using ETS2LA.Logging;
using ETS2LA.Notifications;
using ETS2LA.Overlay;
using ETS2LA.Overlay.AR;
using ETS2LA.Shared;
using ETS2LA.State;
using Hexa.NET.ImGui;

namespace OvertakeAssistant;

public sealed class OvertakeAssistantPlugin : Plugin, IPluginUi
{
    private const string PluginId = "local.overtakeassistant";
    private const string ControlChannelId = "OvertakeAssistant.Indicators";
    private const string ToggleControlId = "OvertakeAssistant.Toggle";
    private const string OverlayWindowTitle = "Overtake Assistant";
    private const string ArCallbackName = "OvertakeAssistant.AR";

    private const float MinimumSpeed = 55f / 3.6f;
    private const float SlowVehicleLookahead = 85f;
    private const float SlowVehicleMinimumGap = 10f;
    private const float MinimumClosingSpeed = 5f / 3.6f;
    private const float AdjacentLaneWidth = 4.5f;
    private const float AdjacentLaneTolerance = 2.4f;
    private const float AdjacentFrontClearance = 70f;
    private const float AdjacentRearClearance = 45f;
    private const float ReturnFrontClearance = 38f;
    private const float ReturnRearClearance = 35f;
    private const float PassedVehicleBehindDistance = 26f;
    private const float IndicatorPulseSeconds = 0.35f;
    private const float LaneChangeSettleSeconds = 7.0f;
    private const float CooldownSeconds = 10f;
    private const float ManualInputBrakeThreshold = 0.08f;
    private const float ManualInputSteerThreshold = 0.18f;
    private const uint ArCandidateColor = 0x66CCFFFF;
    private const uint ArActiveColor = 0x77EE77FF;
    private const uint ArReturnColor = 0xFFCC66FF;

    private readonly ControlChannelDefinition _indicatorChannel = new()
    {
        Id = ControlChannelId,
        Timeout = 0.5f
    };

    private GameTelemetryData? _telemetry;
    private TrafficData? _traffic;
    private Vector3? _previousTruckPosition;
    private Vector3 _estimatedForward = Vector3.UnitZ;
    private OvertakePhase _phase = OvertakePhase.Idle;
    private DateTime _phaseStartedAt = DateTime.UtcNow;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private DateTime _indicatorUntil = DateTime.MinValue;
    private IndicatorDirection _activeIndicator = IndicatorDirection.None;
    private short? _targetVehicleId;
    private bool _armed;
    private bool _showOverlay = true;
    private bool _showAr = true;
    private bool _overlayWindowRegistered;
    private bool _arCallbackRegistered;
    private WindowDefinition _overlayWindowDefinition;
    private OverlaySnapshot _overlaySnapshot = OverlaySnapshot.Empty;

    public override PluginInformation Info => new()
    {
        Id = PluginId,
        Name = "Overtake Assistant",
        Description = "Experimental third-party overtaking assistant that reuses the existing indicator lane-change flow.",
        Version = "0.1.0",
        SupportedETS2LA = ">=3.3.6",
        AuthorName = "Local",
        Dependencies =
        [
            "tumppi066.pathlib",
            "tumppi066.pathfinding",
            "tumppi066.laneassist",
            "tumppi066.adaptivecruisecontrol"
        ],
        Tags = ["Driving", "Experimental"]
    };

    public override float TickRate => 10f;

    public override void Init()
    {
        ControlsBackend.Current.RegisterControl(new ControlDefinition
        {
            Id = ToggleControlId,
            Name = "Toggle Overtake Assistant",
            Description = "Enables or disables the experimental automatic overtaking assistant.",
            DefaultKeybind = "",
            Type = ControlType.Boolean
        });
    }

    public override void OnEnable()
    {
        Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        Events.Current.Subscribe<TrafficData>(TrafficProvider.Current.EventString, OnTraffic);
        ControlsBackend.Current.On(ToggleControlId, OnToggleControl);
        _armed = false;
        TransitionTo(OvertakePhase.Idle);
        RegisterOverlayWindow();
        TryRegisterArCallback();
        UpdateOverlaySnapshot("Ready");
        base.OnEnable();

        Notify("Ready", "Bind Toggle Overtake Assistant before enabling automatic overtakes.", NotificationLevel.Information, 6f);
    }

    public override void Tick()
    {
        TryRegisterArCallback();
        UpdateIndicatorPulse();

        if (!_armed)
        {
            ClearStateIfNeeded();
            UpdateOverlaySnapshot("Disabled");
            return;
        }

        if (!CanAssistRun(out string reason))
        {
            if (_phase is OvertakePhase.RequestingLeft or OvertakePhase.Passing or OvertakePhase.RequestingRight)
            {
                Abort(reason);
            }
            else
            {
                StopIndicatorPulse();
            }

            UpdateOverlaySnapshot(reason);
            return;
        }

        switch (_phase)
        {
            case OvertakePhase.Idle:
                TryStartOvertake();
                break;
            case OvertakePhase.RequestingLeft:
                if (SecondsInPhase > LaneChangeSettleSeconds)
                {
                    TransitionTo(OvertakePhase.Passing);
                }
                break;
            case OvertakePhase.Passing:
                TryReturnToOriginalLane();
                break;
            case OvertakePhase.RequestingRight:
                if (SecondsInPhase > LaneChangeSettleSeconds)
                {
                    CompleteOvertake();
                }
                break;
            case OvertakePhase.Cooldown:
                if (DateTime.UtcNow >= _cooldownUntil)
                {
                    TransitionTo(OvertakePhase.Idle);
                }
                break;
        }

        UpdateOverlaySnapshot(BuildPhaseStatus());
    }

    public override void OnDisable()
    {
        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        Events.Current.Unsubscribe<TrafficData>(TrafficProvider.Current.EventString, OnTraffic);
        ControlsBackend.Current.UnregisterListener(ToggleControlId, OnToggleControl);
        UnregisterArCallback();
        UnregisterOverlayWindow();
        StopIndicatorPulse();
        _armed = false;
        UpdateOverlaySnapshot("Disabled");
        base.OnDisable();
    }

    public override void Shutdown()
    {
        UnregisterArCallback();
        UnregisterOverlayWindow();
        StopIndicatorPulse();
        base.Shutdown();
    }

    private float SecondsInPhase => (float)(DateTime.UtcNow - _phaseStartedAt).TotalSeconds;

    private void OnTelemetry(GameTelemetryData data)
    {
        _telemetry = data;
        UpdateEstimatedForward(data.truckPlacement.coordinate.ToVector3());
    }

    private void OnTraffic(TrafficData data)
    {
        _traffic = data;
    }

    private void OnToggleControl(object? sender, ControlChangeEventArgs e)
    {
        if (e.NewValue is not bool pressed || pressed)
        {
            return;
        }

        _armed = !_armed;
        if (!_armed)
        {
            Abort("disabled by user");
        }

        Notify(_armed ? "Enabled" : "Disabled",
            _armed ? "Automatic overtake checks are active." : "Automatic overtake checks are paused.",
            _armed ? NotificationLevel.Success : NotificationLevel.Information,
            4f);
    }

    public IEnumerable<PluginPage> RenderPages()
    {
        OverlaySnapshot snapshot = _overlaySnapshot;
        yield return new PluginPage(
            "overtake-assistant",
            PluginPageLocation.Settings,
            "Overtake Assistant",
            "Automatic overtake requests with optional overlay and AR diagnostics.",
            new UiElement[]
            {
                new UiSwitch(
                    "Show overlay window",
                    "Shows the live overtake state in the ETS2LA overlay.",
                    _showOverlay,
                    "showOverlay"),
                new UiSwitch(
                    "Show AR markers",
                    "Draws the detected overtake target and lane-change hints in AR.",
                    _showAr,
                    "showAr"),
                new UiTable(
                    "Current state",
                    new[] { "Value", "Current" },
                    new[]
                    {
                        new[] { "Enabled", snapshot.Armed ? "Yes" : "No" },
                        new[] { "Phase", snapshot.Phase },
                        new[] { "Status", snapshot.Status },
                        new[] { "Target", snapshot.TargetText },
                        new[] { "Left lane", snapshot.LeftLaneClear ? "Clear" : "Blocked" },
                        new[] { "Right lane", snapshot.RightLaneClear ? "Clear" : "Blocked" }
                    })
            });
    }

    public void OnAction(string actionId, object? value)
    {
        switch (actionId)
        {
            case "showOverlay":
                _showOverlay = value is bool showOverlay && showOverlay;
                if (_showOverlay)
                {
                    RegisterOverlayWindow();
                    OverlayHandler.Current.OpenWindow(OverlayWindowTitle);
                }
                else
                {
                    OverlayHandler.Current.CloseWindow(OverlayWindowTitle);
                }
                break;
            case "showAr":
                _showAr = value is bool showAr && showAr;
                if (_showAr)
                {
                    TryRegisterArCallback();
                }
                else
                {
                    UnregisterArCallback();
                }
                break;
        }
    }

    private void UpdateEstimatedForward(Vector3 truckPosition)
    {
        if (_previousTruckPosition is { } previous)
        {
            Vector3 delta = truckPosition - previous;
            delta.Y = 0f;
            if (delta.Length() > 0.2f)
            {
                _estimatedForward = Vector3.Normalize(delta);
            }
        }

        _previousTruckPosition = truckPosition;
    }

    private void RegisterOverlayWindow()
    {
        if (_overlayWindowRegistered)
        {
            return;
        }

        _overlayWindowDefinition = new WindowDefinition
        {
            Title = OverlayWindowTitle,
            Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse,
            X = 24,
            Y = 180,
            Alpha = 0.82f
        };

        OverlayHandler.Current.RegisterWindow(_overlayWindowDefinition, RenderOverlayWindow);
        if (_showOverlay)
        {
            OverlayHandler.Current.OpenWindow(OverlayWindowTitle);
        }
        else
        {
            OverlayHandler.Current.CloseWindow(OverlayWindowTitle);
        }

        _overlayWindowRegistered = true;
    }

    private void UnregisterOverlayWindow()
    {
        if (!_overlayWindowRegistered)
        {
            return;
        }

        OverlayHandler.Current.UnregisterWindow(_overlayWindowDefinition);
        _overlayWindowRegistered = false;
    }

    private void TryRegisterArCallback()
    {
        if (_arCallbackRegistered || !_showAr)
        {
            return;
        }

        ARRenderer? ar = OverlayHandler.Current.AR;
        if (ar == null)
        {
            return;
        }

        ar.RegisterRenderCallback(new ARRenderCallback
        {
            Definition = new ARRendererDefinition
            {
                Name = ArCallbackName,
                Alpha = 1.0f
            },
            Render3D = RenderAr
        });
        _arCallbackRegistered = true;
    }

    private void UnregisterArCallback()
    {
        if (!_arCallbackRegistered)
        {
            return;
        }

        OverlayHandler.Current.AR?.UnregisterRenderCallback(ArCallbackName);
        _arCallbackRegistered = false;
    }

    private void RenderOverlayWindow()
    {
        if (!_showOverlay)
        {
            return;
        }

        OverlaySnapshot snapshot = _overlaySnapshot;
        ImGui.TextColored(snapshot.Armed ? new Vector4(0.45f, 1f, 0.45f, 1f) : new Vector4(1f, 0.55f, 0.55f, 1f),
            snapshot.Armed ? "Enabled" : "Disabled");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.72f, 0.72f, 0.72f, 1f), snapshot.Phase);
        ImGui.Separator();

        DrawOverlayRow("Status", snapshot.Status);
        DrawOverlayRow("Target", snapshot.TargetText);
        DrawOverlayRow("Left lane", snapshot.LeftLaneClear ? "Clear" : "Blocked");
        DrawOverlayRow("Right lane", snapshot.RightLaneClear ? "Clear" : "Blocked");
        DrawOverlayRow("Cooldown", snapshot.CooldownRemainingSeconds > 0 ? $"{snapshot.CooldownRemainingSeconds:0.0}s" : "Ready");

        ImGui.Separator();
        ImGui.TextColored(_showAr ? new Vector4(0.45f, 0.8f, 1f, 1f) : new Vector4(0.7f, 0.7f, 0.7f, 1f),
            _showAr ? "AR markers on" : "AR markers off");
    }

    private static void DrawOverlayRow(string label, string value)
    {
        ImGui.TextColored(new Vector4(0.68f, 0.68f, 0.68f, 1f), $"{label}:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.95f, 0.95f, 0.95f, 1f), value);
    }

    private void RenderAr()
    {
        if (!_showAr)
        {
            return;
        }

        OverlaySnapshot snapshot = _overlaySnapshot;
        if (!snapshot.Armed || snapshot.HighlightVehicle == null)
        {
            return;
        }

        ARRenderer? ar = OverlayHandler.Current.AR;
        if (ar == null)
        {
            return;
        }

        VehicleArInfo vehicle = snapshot.HighlightVehicle;
        uint color = snapshot.Phase switch
        {
            nameof(OvertakePhase.RequestingRight) => ArReturnColor,
            nameof(OvertakePhase.Passing) => ArActiveColor,
            nameof(OvertakePhase.RequestingLeft) => ArActiveColor,
            _ => ArCandidateColor
        };

        DrawVehicleBox(ar, vehicle, color);
        ARCoordinate labelPosition = new(vehicle.Position + new Vector3(0f, vehicle.Height + 1.2f, 0f));
        ar.Draw3DText(labelPosition, snapshot.ArText, color);

        if (_telemetry != null)
        {
            Vector3 truckPosition = _telemetry.truckPlacement.coordinate.ToVector3();
            ar.Draw3DLine(new ARCoordinate(truckPosition + Vector3.UnitY * 1.0f),
                new ARCoordinate(vehicle.Position + Vector3.UnitY * 1.0f),
                color,
                2.0f);
        }
    }

    private static void DrawVehicleBox(ARRenderer ar, VehicleArInfo vehicle, uint color)
    {
        Vector3 half = new(
            Math.Max(vehicle.Width, 1.8f) / 2f,
            Math.Max(vehicle.Height, 1.6f) / 2f,
            Math.Max(vehicle.Length, 4.0f) / 2f);
        Vector3 center = vehicle.Position + new Vector3(0f, half.Y, 0f);

        ARCoordinate topFrontLeft = center + new Vector3(-half.X, half.Y, half.Z);
        ARCoordinate topFrontRight = center + new Vector3(half.X, half.Y, half.Z);
        ARCoordinate topRearRight = center + new Vector3(half.X, half.Y, -half.Z);
        ARCoordinate topRearLeft = center + new Vector3(-half.X, half.Y, -half.Z);
        ARCoordinate bottomFrontLeft = center + new Vector3(-half.X, -half.Y, half.Z);
        ARCoordinate bottomFrontRight = center + new Vector3(half.X, -half.Y, half.Z);
        ARCoordinate bottomRearRight = center + new Vector3(half.X, -half.Y, -half.Z);
        ARCoordinate bottomRearLeft = center + new Vector3(-half.X, -half.Y, -half.Z);

        ar.Draw3DQuad(topFrontLeft, topFrontRight, topRearRight, topRearLeft, color, thickness: 2.0f);
        ar.Draw3DQuad(bottomFrontLeft, bottomFrontRight, bottomRearRight, bottomRearLeft, color, thickness: 2.0f);
        ar.Draw3DLine(topFrontLeft, bottomFrontLeft, color, 2.0f);
        ar.Draw3DLine(topFrontRight, bottomFrontRight, color, 2.0f);
        ar.Draw3DLine(topRearRight, bottomRearRight, color, 2.0f);
        ar.Draw3DLine(topRearLeft, bottomRearLeft, color, 2.0f);
    }

    private bool CanAssistRun(out string reason)
    {
        reason = "";
        ApplicationState state = ApplicationState.Current;
        if (_telemetry == null || _traffic == null)
        {
            reason = "waiting for telemetry";
            return false;
        }

        if (!_telemetry.sdkActive || _telemetry.paused)
        {
            reason = "game paused or SDK inactive";
            return false;
        }

        if (state.PauseSteeringAssist || state.PauseLongitudinalAssist)
        {
            reason = "assists paused";
            return false;
        }

        if (state.DesiredSteeringLevel == SteeringAssists.None || state.DesiredLongitudinalLevel == LongitudinalAssists.None)
        {
            reason = "required assists disabled";
            return false;
        }

        if (_telemetry.truckFloat.speed < MinimumSpeed)
        {
            reason = "below minimum speed";
            return false;
        }

        if (_phase != OvertakePhase.Idle &&
            (Math.Abs(_telemetry.truckFloat.userBrake) > ManualInputBrakeThreshold ||
             Math.Abs(_telemetry.truckFloat.userSteer) > ManualInputSteerThreshold))
        {
            reason = "manual input detected";
            return false;
        }

        if (_estimatedForward.LengthSquared() < 0.5f)
        {
            reason = "heading unavailable";
            return false;
        }

        return true;
    }

    private void UpdateOverlaySnapshot(string status)
    {
        GameTelemetryData? telemetry = _telemetry;
        TrafficData? traffic = _traffic;
        Units units = ApplicationState.Current.DisplayUnits;
        string speedUnit = UnitConversions.GetUnitAbbreviation(UnitType.Speed, units);
        float cooldownRemaining = Math.Max(0f, (float)(_cooldownUntil - DateTime.UtcNow).TotalSeconds);

        bool leftClear = false;
        bool rightClear = false;
        VehicleProjection? leading = null;
        VehicleProjection? target = null;

        if (telemetry != null && traffic != null && _estimatedForward.LengthSquared() >= 0.5f)
        {
            Vector3 truckPosition = telemetry.truckPlacement.coordinate.ToVector3();
            leftClear = IsAdjacentLaneClear(truckPosition, LaneSide.Left, AdjacentFrontClearance, AdjacentRearClearance);
            rightClear = IsAdjacentLaneClear(truckPosition, LaneSide.Right, ReturnFrontClearance, ReturnRearClearance);
            leading = FindLeadingSlowVehicle(truckPosition);
            TrafficVehicle? targetVehicle = FindTargetVehicle();
            if (targetVehicle != null)
            {
                target = ProjectVehicle(targetVehicle, truckPosition);
            }
        }

        VehicleProjection? highlight = target ?? leading;
        VehicleArInfo? vehicleInfo = highlight == null ? null : new VehicleArInfo(
            highlight.Vehicle.id,
            highlight.Vehicle.Position,
            Math.Abs(highlight.Vehicle.Size.X),
            Math.Abs(highlight.Vehicle.Size.Y),
            Math.Abs(highlight.Vehicle.Size.Z));

        string speedDelta = highlight == null
            ? ""
            : FormatSignedSpeed((telemetry?.truckFloat.speed ?? 0f) - highlight.Speed, units, speedUnit);

        string targetText = highlight == null
            ? "None"
            : $"{highlight.Longitudinal:0}m, {speedDelta}";

        string arText = highlight == null
            ? ""
            : $"{BuildPhaseStatus()} | {highlight.Longitudinal:0}m | {speedDelta}";

        _overlaySnapshot = new OverlaySnapshot(
            _armed,
            _phase.ToString(),
            status,
            targetText,
            arText,
            leftClear,
            rightClear,
            cooldownRemaining,
            vehicleInfo);
    }

    private string BuildPhaseStatus()
    {
        return _phase switch
        {
            OvertakePhase.Idle => DateTime.UtcNow < _cooldownUntil ? "Cooling down" : "Watching traffic",
            OvertakePhase.RequestingLeft => "Requesting left lane",
            OvertakePhase.Passing => "Passing target",
            OvertakePhase.RequestingRight => "Returning right",
            OvertakePhase.Cooldown => "Cooling down",
            _ => _phase.ToString()
        };
    }

    private static string FormatSpeed(float speed, Units units, string unit)
    {
        float display = UnitConversions.FromScientificUnits(UnitType.Speed, speed, units);
        return $"{display:0.0} {unit}";
    }

    private static string FormatSignedSpeed(float speed, Units units, string unit)
    {
        float display = UnitConversions.FromScientificUnits(UnitType.Speed, speed, units);
        return $"{display:+0.0;-0.0;0.0} {unit}";
    }

    private void TryStartOvertake()
    {
        if (DateTime.UtcNow < _cooldownUntil)
        {
            TransitionTo(OvertakePhase.Cooldown);
            return;
        }

        if (_telemetry == null || _traffic == null)
        {
            return;
        }

        Vector3 truckPosition = _telemetry.truckPlacement.coordinate.ToVector3();
        VehicleProjection? leading = FindLeadingSlowVehicle(truckPosition);
        if (leading == null)
        {
            return;
        }

        if (!IsAdjacentLaneClear(truckPosition, LaneSide.Left, AdjacentFrontClearance, AdjacentRearClearance))
        {
            return;
        }

        _targetVehicleId = leading.Vehicle.id;
        PulseIndicator(IndicatorDirection.Left);
        TransitionTo(OvertakePhase.RequestingLeft);
        Notify("Overtake requested", "Left indicator pulse sent to request a lane change.", NotificationLevel.Information, 4f);
    }

    private void TryReturnToOriginalLane()
    {
        if (_telemetry == null)
        {
            return;
        }

        Vector3 truckPosition = _telemetry.truckPlacement.coordinate.ToVector3();
        TrafficVehicle? targetVehicle = FindTargetVehicle();
        if (targetVehicle != null)
        {
            VehicleProjection target = ProjectVehicle(targetVehicle, truckPosition);
            if (target.Longitudinal > -PassedVehicleBehindDistance)
            {
                return;
            }
        }

        if (!IsAdjacentLaneClear(truckPosition, LaneSide.Right, ReturnFrontClearance, ReturnRearClearance))
        {
            return;
        }

        PulseIndicator(IndicatorDirection.Right);
        TransitionTo(OvertakePhase.RequestingRight);
        Notify("Return requested", "Right indicator pulse sent to return after passing.", NotificationLevel.Information, 4f);
    }

    private VehicleProjection? FindLeadingSlowVehicle(Vector3 truckPosition)
    {
        if (_traffic == null || _telemetry == null)
        {
            return null;
        }

        return _traffic.vehicles
            .Where(IsUsableVehicle)
            .Select(vehicle => ProjectVehicle(vehicle, truckPosition))
            .Where(vehicle => Math.Abs(vehicle.Lateral) < AdjacentLaneTolerance)
            .Where(vehicle => vehicle.Longitudinal >= SlowVehicleMinimumGap && vehicle.Longitudinal <= SlowVehicleLookahead)
            .Where(vehicle => _telemetry.truckFloat.speed - vehicle.Speed >= MinimumClosingSpeed)
            .OrderBy(vehicle => vehicle.Longitudinal)
            .FirstOrDefault();
    }

    private TrafficVehicle? FindTargetVehicle()
    {
        if (_traffic == null || _targetVehicleId == null)
        {
            return null;
        }

        return _traffic.vehicles.FirstOrDefault(vehicle => vehicle.id == _targetVehicleId.Value);
    }

    private bool IsAdjacentLaneClear(Vector3 truckPosition, LaneSide side, float frontClearance, float rearClearance)
    {
        if (_traffic == null)
        {
            return false;
        }

        float targetLateral = side == LaneSide.Left ? -AdjacentLaneWidth : AdjacentLaneWidth;
        foreach (TrafficVehicle vehicle in _traffic.vehicles.Where(IsUsableVehicle))
        {
            VehicleProjection projection = ProjectVehicle(vehicle, truckPosition);
            if (Math.Abs(projection.Lateral - targetLateral) > AdjacentLaneTolerance)
            {
                continue;
            }

            if (projection.Longitudinal >= 0f && projection.Longitudinal < frontClearance)
            {
                return false;
            }

            if (projection.Longitudinal < 0f && Math.Abs(projection.Longitudinal) < rearClearance)
            {
                return false;
            }
        }

        return true;
    }

    private VehicleProjection ProjectVehicle(TrafficVehicle vehicle, Vector3 truckPosition)
    {
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 delta = vehicle.Position - truckPosition;
        delta.Y = 0f;

        return new VehicleProjection(
            vehicle,
            Vector3.Dot(delta, forward),
            Vector3.Dot(delta, right),
            vehicle.speed);
    }

    private static bool IsUsableVehicle(TrafficVehicle vehicle)
    {
        return vehicle.id != 0 && vehicle.Size.LengthSquared() > 0.1f && vehicle.Position.LengthSquared() > 0.1f && !vehicle.isTrailer;
    }

    private void PulseIndicator(IndicatorDirection direction)
    {
        _activeIndicator = direction;
        _indicatorUntil = DateTime.UtcNow.AddSeconds(IndicatorPulseSeconds);
        PublishIndicator(direction);
    }

    private void UpdateIndicatorPulse()
    {
        if (_activeIndicator == IndicatorDirection.None)
        {
            return;
        }

        if (DateTime.UtcNow < _indicatorUntil)
        {
            PublishIndicator(_activeIndicator);
            return;
        }

        StopIndicatorPulse();
    }

    private void PublishIndicator(IndicatorDirection direction)
    {
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = _indicatorChannel,
            Properties = new ControlProperties
            {
                BooleanType = ControlBooleanType.Direct,
                Weight = 5.0f
            },
            Variables = new ControlVariables
            {
                lblinker = direction == IndicatorDirection.Left,
                rblinker = direction == IndicatorDirection.Right
            }
        });
    }

    private void StopIndicatorPulse()
    {
        if (_activeIndicator == IndicatorDirection.None)
        {
            return;
        }

        _activeIndicator = IndicatorDirection.None;
        Events.Current.Publish(GameOutput.Current.EventString, new ControlEvent
        {
            ChannelDefinition = _indicatorChannel,
            Properties = new ControlProperties(),
            Variables = new ControlVariables
            {
                lblinker = false,
                rblinker = false
            }
        });
    }

    private void CompleteOvertake()
    {
        _targetVehicleId = null;
        _cooldownUntil = DateTime.UtcNow.AddSeconds(CooldownSeconds);
        TransitionTo(OvertakePhase.Cooldown);
        Notify("Overtake complete", "Returning to cooldown.", NotificationLevel.Success, 4f);
    }

    private void Abort(string reason)
    {
        if (_phase == OvertakePhase.Idle && _targetVehicleId == null)
        {
            StopIndicatorPulse();
            return;
        }

        StopIndicatorPulse();
        _targetVehicleId = null;
        _cooldownUntil = DateTime.UtcNow.AddSeconds(CooldownSeconds);
        TransitionTo(OvertakePhase.Cooldown);
        Logger.Info($"Overtake Assistant aborted: {reason}");
    }

    private void ClearStateIfNeeded()
    {
        StopIndicatorPulse();
        if (_phase != OvertakePhase.Idle)
        {
            _targetVehicleId = null;
            TransitionTo(OvertakePhase.Idle);
        }
    }

    private void TransitionTo(OvertakePhase phase)
    {
        _phase = phase;
        _phaseStartedAt = DateTime.UtcNow;
    }

    private static void Notify(string title, string content, NotificationLevel level, float closeAfter)
    {
        NotificationHandler.Current.SendNotification(new Notification
        {
            Id = $"{PluginId}.{title.Replace(" ", "")}",
            Title = $"Overtake Assistant: {title}",
            Content = content,
            Level = level,
            CloseAfter = closeAfter
        });
    }

    private enum OvertakePhase
    {
        Idle,
        RequestingLeft,
        Passing,
        RequestingRight,
        Cooldown
    }

    private enum IndicatorDirection
    {
        None,
        Left,
        Right
    }

    private enum LaneSide
    {
        Left,
        Right
    }

    private sealed record OverlaySnapshot(
        bool Armed,
        string Phase,
        string Status,
        string TargetText,
        string ArText,
        bool LeftLaneClear,
        bool RightLaneClear,
        float CooldownRemainingSeconds,
        VehicleArInfo? HighlightVehicle)
    {
        public static OverlaySnapshot Empty { get; } = new(
            false,
            OvertakePhase.Idle.ToString(),
            "Waiting for telemetry",
            "None",
            "",
            false,
            false,
            0f,
            null);
    }

    private sealed record VehicleArInfo(short Id, Vector3 Position, float Width, float Height, float Length);

    private sealed record VehicleProjection(TrafficVehicle Vehicle, float Longitudinal, float Lateral, float Speed);
}

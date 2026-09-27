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

public sealed class OvertakeAssistantPlugin : Plugin
{
    public static OvertakeAssistantPlugin? Instance { get; private set; }

    public OvertakeAssistantPlugin()
    {
        Instance = this;
    }

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
    private const float ReturnFrontClearance = 70f;
    private const float ReturnRearClearance = 35f;
    private const float PassedVehicleBehindDistance = 26f;
    private const float VehicleLongitudinalSafetyBuffer = 2.0f;
    private const float IndicatorPulseSeconds = 0.35f;
    private const float CooldownSeconds = 10f;
    private const float MaxHeightDifference = 3f;
    private const float MinimumCurveRadius = 800f;
    private const float OvertakingMinimumSpeed = 45f / 3.6f;
    private const float RelativeSpeedHorizonSeconds = 4f;
    private const float AdjacentLaneExtendedScanRear = 60f;
    private const float AdjacentLaneFrontScanMargin = 10f;
    private const float LaneChangeConfirmedDistance = 3f;
    private const float LaneChangeSettleHoldSeconds = 1.5f;
    private const float LaneChangeVerificationTimeoutSeconds = 8f;
    private const float PassingTimeoutSeconds = 30f;
    private const float ManualInputBrakeThreshold = 0.08f;
    private const float ManualInputSteerThreshold = 0.18f;
    private const uint ArCandidateColor = 0x66CCFFFF;
    private const uint ArActiveColor = 0x77EE77FF;
    private const uint ArReturnColor = 0xFFCC66FF;
    private const uint ArReturnZoneColor = 0xFFCC66CC;

    private readonly ControlChannelDefinition _indicatorChannel = new()
    {
        Id = ControlChannelId,
        Timeout = 0.5f
    };

    private GameTelemetryData? _telemetry;
    private TrafficData? _traffic;
    private Vector3? _previousTruckPosition;
    private Vector3 _estimatedForward = Vector3.UnitZ;
    private float _curvature;
    private float? _previousHeading;
    private DateTime _previousHeadingAt = DateTime.UtcNow;
    private OvertakePhase _phase = OvertakePhase.Idle;
    private DateTime _phaseStartedAt = DateTime.UtcNow;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private DateTime _indicatorUntil = DateTime.MinValue;
    private IndicatorDirection _activeIndicator = IndicatorDirection.None;
    private short? _targetVehicleId;
    private Vector3? _phaseStartPosition;
    private Vector3 _phaseStartForward = Vector3.UnitZ;
    private DateTime? _laneChangeConfirmedAt;
    private bool _armed;
    private bool _showOverlay = true;
    private bool _showAr = true;
    private bool _overlayWindowRegistered;
    private bool _arCallbackRegistered;
    private WindowDefinition _overlayWindowDefinition;
    private OverlaySnapshot _overlaySnapshot = OverlaySnapshot.Empty;

    public bool ShowOverlay
    {
        get => _showOverlay;
        set
        {
            _showOverlay = value;
            if (_overlayWindowRegistered)
            {
                if (_showOverlay)
                {
                    OverlayHandler.Current.OpenWindow(OverlayWindowTitle);
                }
                else
                {
                    OverlayHandler.Current.CloseWindow(OverlayWindowTitle);
                }
            }
        }
    }

    public bool ShowAr
    {
        get => _showAr;
        set
        {
            _showAr = value;
            if (_showAr)
            {
                TryRegisterArCallback();
            }
            else
            {
                UnregisterArCallback();
            }
        }
    }

    public override PluginInformation Info => new()
    {
        Id = PluginId,
        Name = "Overtake Assistant",
        Description = "Experimental third-party overtaking assistant that reuses the existing indicator lane-change flow.",
        Version = "0.3.0",
        SupportedETS2LA = "*",
        AuthorName = "Local",
        Dependencies =
        [
            "tumppi066.pathlib",
            "tumppi066.pathfinding",
            "tumppi066.laneassist"
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
                HandleLaneChangePhase(movingLeft: true);
                break;
            case OvertakePhase.Passing:
                if (SecondsInPhase > PassingTimeoutSeconds)
                {
                    Abort("passing timed out");
                    break;
                }

                TryReturnToOriginalLane();
                break;
            case OvertakePhase.RequestingRight:
                HandleLaneChangePhase(movingLeft: false);
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
        UpdateCurvature(data);
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

    public OverlaySnapshot CurrentSnapshot => _overlaySnapshot;

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

    private void UpdateCurvature(GameTelemetryData data)
    {
        float heading = (float)data.truckPlacement.rotation.X;
        float speed = data.truckFloat.speed;
        DateTime now = DateTime.UtcNow;

        if (_previousHeading is { } previousHeading && speed > 1f)
        {
            float deltaTime = (float)(now - _previousHeadingAt).TotalSeconds;
            if (deltaTime > 0.01f && deltaTime < 1f)
            {
                // |dθ/dt| / v = 1/R. Only the absolute turn rate matters, so this works
                // regardless of the game's handedness or heading sign convention.
                float turnRate = MathF.Abs(WrapAngle(heading - previousHeading)) / deltaTime;
                float curvature = turnRate / speed;
                _curvature = _curvature * 0.8f + curvature * 0.2f;
            }
        }

        _previousHeading = heading;
        _previousHeadingAt = now;
    }

    private static float WrapAngle(float angle)
    {
        const float TwoPi = MathF.PI * 2f;
        angle %= TwoPi;
        if (angle > MathF.PI)
        {
            angle -= TwoPi;
        }
        else if (angle < -MathF.PI)
        {
            angle += TwoPi;
        }

        return angle;
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

            if (_phase is OvertakePhase.Idle or OvertakePhase.RequestingLeft)
            {
                DrawLaneClearanceZones(ar, truckPosition, LaneSide.Left, AdjacentFrontClearance, AdjacentRearClearance, ArCandidateColor, "Overtake scan");
            }

            if (_phase is OvertakePhase.Passing or OvertakePhase.RequestingRight)
            {
                DrawLaneClearanceZones(ar, truckPosition, LaneSide.Right, ReturnFrontClearance, ReturnRearClearance, ArReturnZoneColor, "Return scan");
            }
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

    private void DrawLaneClearanceZones(ARRenderer ar, Vector3 truckPosition, LaneSide side, float frontClearance, float rearClearance, uint color, string label)
    {
        foreach (float lateral in GetLaneScanLaterals(side))
        {
            DrawLaneClearanceZone(ar, truckPosition, lateral, frontClearance, rearClearance, color);
        }

        DrawLaneClearanceLabel(ar, truckPosition, GetPrimaryLaneLateral(side), frontClearance, rearClearance, color, label);
    }

    private void DrawLaneClearanceZone(ARRenderer ar, Vector3 truckPosition, float lateral, float frontClearance, float rearClearance, uint color)
    {
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        // Left-handed game world (+X = west): this cross product points to the truck's LEFT.
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 laneCenter = truckPosition + left * lateral + Vector3.UnitY * 0.12f;
        float halfWidth = AdjacentLaneTolerance;

        Vector3 frontCenter = laneCenter + forward * frontClearance;
        Vector3 rearCenter = laneCenter - forward * rearClearance;

        ARCoordinate frontLeft = frontCenter - left * halfWidth;
        ARCoordinate frontRight = frontCenter + left * halfWidth;
        ARCoordinate rearRight = rearCenter + left * halfWidth;
        ARCoordinate rearLeft = rearCenter - left * halfWidth;

        ar.Draw3DQuad(frontLeft, frontRight, rearRight, rearLeft, color, thickness: 2.0f);
        ar.Draw3DLine(new ARCoordinate(laneCenter - forward * rearClearance), new ARCoordinate(laneCenter + forward * frontClearance), color, 2.0f);
    }

    private void DrawLaneClearanceLabel(ARRenderer ar, Vector3 truckPosition, float lateral, float frontClearance, float rearClearance, uint color, string label)
    {
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        // Left-handed game world (+X = west): this cross product points to the truck's LEFT.
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 labelPosition = truckPosition + left * lateral + forward * Math.Min(frontClearance, 25f) + Vector3.UnitY * 2.2f;
        ar.Draw3DText(new ARCoordinate(labelPosition), $"{label} +{frontClearance:0}m / -{rearClearance:0}m", color);
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

        if (!state.EnableAssists)
        {
            reason = "assists disabled";
            return false;
        }

        if (state.DrivingMode != DrivingMode.FullSelfDriving)
        {
            reason = "full self-driving mode required";
            return false;
        }

        // Starting an overtake needs cruise speed; an active one only needs enough speed to
        // finish, so rolling grades do not abort the maneuver mid-way.
        float minimumSpeed = _phase == OvertakePhase.Idle ? MinimumSpeed : OvertakingMinimumSpeed;
        if (_telemetry.truckFloat.speed < minimumSpeed)
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

        if (_curvature >= 1f / MinimumCurveRadius)
        {
            // Lane classification from world positions is unreliable on curves; wait for a straight.
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
            .Where(vehicle => Math.Abs(vehicle.Position.Y - truckPosition.Y) <= MaxHeightDifference)
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
        if (_traffic == null || _telemetry == null)
        {
            return false;
        }

        float truckSpeed = _telemetry.truckFloat.speed;
        foreach (TrafficVehicle vehicle in _traffic.vehicles.Where(IsUsableVehicle))
        {
            // Bridges and parallel ramps sit above or below the carriageway; the flattened
            // projection would otherwise classify their traffic as adjacent-lane vehicles.
            if (Math.Abs(vehicle.Position.Y - truckPosition.Y) > MaxHeightDifference)
            {
                continue;
            }

            VehicleProjection projection = ProjectVehicle(vehicle, truckPosition);
            if (!IsInAnyScannedAdjacentLane(projection.Lateral, side))
            {
                continue;
            }

            if (projection.Longitudinal < -AdjacentLaneExtendedScanRear ||
                projection.Longitudinal > frontClearance + AdjacentLaneFrontScanMargin)
            {
                continue;
            }

            float longitudinalBuffer = GetLongitudinalVehicleBuffer(vehicle);
            float rearBlockBoundary = -(rearClearance + longitudinalBuffer);
            float frontBlockBoundary = frontClearance + longitudinalBuffer;

            // Static positions miss fast approachers: extrapolate the projection over the
            // prediction horizon and treat any overlap with the blocked range as unsafe.
            // The interval always contains the current position, so this also covers the
            // plain static case.
            float predictedEnd = projection.Longitudinal + (vehicle.speed - truckSpeed) * RelativeSpeedHorizonSeconds;

            if (Math.Min(projection.Longitudinal, predictedEnd) < frontBlockBoundary &&
                Math.Max(projection.Longitudinal, predictedEnd) > rearBlockBoundary)
            {
                return false;
            }
        }

        return true;
    }

    private static float[] GetLaneScanLaterals(LaneSide side)
    {
        return [GetPrimaryLaneLateral(side)];
    }

    // The game world is left-handed (+X = west), so Cross(UnitY, forward) yields the truck's
    // LEFT vector and positive lateral offsets are to the left. Hence Left => +AdjacentLaneWidth.
    private static float GetPrimaryLaneLateral(LaneSide side)
    {
        return side == LaneSide.Left ? AdjacentLaneWidth : -AdjacentLaneWidth;
    }

    private static bool IsInAnyScannedAdjacentLane(float lateral, LaneSide side)
    {
        foreach (float targetLateral in GetLaneScanLaterals(side))
        {
            if (Math.Abs(lateral - targetLateral) <= AdjacentLaneTolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static float GetLongitudinalVehicleBuffer(TrafficVehicle vehicle)
    {
        return Math.Max(Math.Abs(vehicle.Size.Z), Math.Abs(vehicle.Size.X)) / 2f + VehicleLongitudinalSafetyBuffer;
    }

    private VehicleProjection ProjectVehicle(TrafficVehicle vehicle, Vector3 truckPosition)
    {
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        // Left-handed game world (+X = west): this cross product points to the truck's LEFT,
        // so positive Lateral means the vehicle is on the truck's left side.
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 delta = vehicle.Position - truckPosition;
        delta.Y = 0f;

        return new VehicleProjection(
            vehicle,
            Vector3.Dot(delta, forward),
            Vector3.Dot(delta, left),
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

    private void Abort(string reason, bool stopIndicator = true)
    {
        if (_phase == OvertakePhase.Idle && _targetVehicleId == null)
        {
            if (stopIndicator)
            {
                StopIndicatorPulse();
            }

            return;
        }

        if (stopIndicator)
        {
            StopIndicatorPulse();
        }

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
        _phaseStartPosition = _telemetry?.truckPlacement.coordinate.ToVector3();
        _phaseStartForward = _estimatedForward;
        _laneChangeConfirmedAt = null;
    }

    private float LateralDisplacementSincePhaseStart()
    {
        if (_phaseStartPosition is not { } startPosition || _telemetry == null)
        {
            return 0f;
        }

        Vector3 forward = Vector3.Normalize(_phaseStartForward);
        // The game world is left-handed (+X = west): Cross(UnitY, forward) points to the
        // truck's LEFT, so a positive result means the truck moved left.
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 delta = _telemetry.truckPlacement.coordinate.ToVector3() - startPosition;
        delta.Y = 0f;
        return Vector3.Dot(delta, left);
    }

    private void HandleLaneChangePhase(bool movingLeft)
    {
        float displacement = LateralDisplacementSincePhaseStart();
        float signedDisplacement = movingLeft ? displacement : -displacement;

        if (signedDisplacement >= LaneChangeConfirmedDistance)
        {
            _laneChangeConfirmedAt ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _laneChangeConfirmedAt.Value).TotalSeconds >= LaneChangeSettleHoldSeconds)
            {
                if (movingLeft)
                {
                    TransitionTo(OvertakePhase.Passing);
                }
                else
                {
                    CompleteOvertake();
                }
            }

            return;
        }

        if (SecondsInPhase <= LaneChangeVerificationTimeoutSeconds)
        {
            return;
        }

        // Verified the lane change never happened, so the game indicator we pulsed is still on.
        // The game indicator is a toggle: a second press cancels it. Never do this after a
        // confirmed lane change (the game auto-cancels the indicator there, and the extra press
        // would switch it back on). Road curvature displacement (sagitta) can rarely mask a
        // failed change on curves; the Passing timeout backstops those cases.
        PulseIndicator(movingLeft ? IndicatorDirection.Left : IndicatorDirection.Right);
        Abort(movingLeft ? "lane change was not performed" : "return to original lane was not performed", stopIndicator: false);
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

    public sealed record OverlaySnapshot(
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

    public sealed record VehicleArInfo(short Id, Vector3 Position, float Width, float Height, float Length);

    private sealed record VehicleProjection(TrafficVehicle Vehicle, float Longitudinal, float Lateral, float Speed);
}

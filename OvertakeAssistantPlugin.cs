using System.Numerics;
using ETS2LA.Backend.Events;
using ETS2LA.Controls;
using ETS2LA.Game.Data;
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
using PathLib;
using TruckLib;
using TruckLib.ScsMap;

namespace OvertakeAssistant;

/// <summary>
/// MOBIL-based automatic overtaking.
///
/// Decision core: IDM/MOBIL incentive + safety criteria with distance gating and
/// hard time-headway gates. Execution: sets <c>PlannedItem.targetLaneIndex</c> so
/// Pathfinding auto-indicates and PathLib generates the lane change spline that
/// LaneAssist follows. The physical direction of a candidate lane is resolved at
/// runtime from its world position, so no road-side sign conventions are trusted.
/// </summary>
public sealed class OvertakeAssistantPlugin : Plugin
{
    public static OvertakeAssistantPlugin? Instance { get; private set; }

    public OvertakeAssistantPlugin()
    {
        Instance = this;
    }

    private const string PluginId = "local.overtakeassistant";
    private const string ToggleControlId = "OvertakeAssistant.Toggle";
    private const string OverlayWindowTitle = "Overtake Assistant";
    private const string ArCallbackName = "OvertakeAssistant.AR";
    private const string PlannedPathTopic = "Pathfinding.PlannedPathData";

    private const float OvertakingMinimumSpeed = 45f / 3.6f;
    private const float MaxHeightDifference = 3f;
    private const float EgoFrontOffset = 2.5f;
    private const float EgoRearOffset = 8.0f;
    private const float RearScanDistance = 60f;
    private const float CommittedDisplacement = 1.5f;
    private const float StartedDisplacement = 0.3f;
    private const float ProgressGraceSeconds = 4f;
    private const int MaxReasserts = 60;
    private const uint ArCandidateColor = 0x66CCFFFF;
    private const uint ArActiveColor = 0x77EE77FF;
    private const uint ArReturnColor = 0xFFCC66FF;
    private const uint ArReturnZoneColor = 0xFFCC66CC;

    private GameTelemetryData? _telemetry;
    private TrafficData? _traffic;
    private PlannedPathData? _plannedPath;

    private readonly Dictionary<short, (Vector3 Position, DateTime Time)> _vehicleTrack = new();
    private List<VehicleProjection> _projections = new();

    private Vector3? _previousTruckPosition;
    private Vector3 _estimatedForward = Vector3.UnitZ;
    private float _curvature;
    private float? _previousHeading;
    private DateTime _previousHeadingAt = DateTime.UtcNow;

    private OvertakePhase _phase = OvertakePhase.Idle;
    private DateTime _phaseStartedAt = DateTime.UtcNow;
    private DateTime _cooldownUntil = DateTime.MinValue;
    private bool _armed;

    private short? _targetVehicleId;
    private short? _followerWatchId;
    private int _originalLaneIndex;
    private int _requestedLaneIndex;
    private int _reassertCount;
    private float _maxDirectedDisplacement;
    private bool _overtakeOnLeft;

    // maneuver metrics
    private DateTime _maneuverStartedAt;
    private float _minFrontGap = float.MaxValue;
    private float _minRearGap = float.MaxValue;
    private float _minRearThw = float.MaxValue;
    private float _followerMinAccel = float.MaxValue;
    private float _maxLateralAccel;

    private bool _overlayWindowRegistered;
    private bool _arCallbackRegistered;
    private WindowDefinition _overlayWindowDefinition;
    private OverlaySnapshot _overlaySnapshot = OverlaySnapshot.Empty;

    public bool ShowOverlay
    {
        get => OvertakeSettings.Current.ShowOverlay;
        set
        {
            OvertakeSettings.Current.ShowOverlay = value;
            OvertakeSettings.Current.Save();
            if (_overlayWindowRegistered)
            {
                if (value) OverlayHandler.Current.OpenWindow(OverlayWindowTitle);
                else OverlayHandler.Current.CloseWindow(OverlayWindowTitle);
            }
        }
    }

    public bool ShowAr
    {
        get => OvertakeSettings.Current.ShowAr;
        set
        {
            OvertakeSettings.Current.ShowAr = value;
            OvertakeSettings.Current.Save();
            if (value) TryRegisterArCallback();
            else UnregisterArCallback();
        }
    }

    public override PluginInformation Info => new()
    {
        Id = PluginId,
        Name = "Overtake Assistant",
        Description = "MOBIL-based automatic overtaking. Requests lane changes through PathLib and verifies them physically.",
        Version = "1.0.0",
        AppVersionSupport = new PluginVersionSupport(),
        AuthorName = "Local",
        DependsOn =
        [
            new PluginDependency { Id = "tumppi066.pathlib" },
            new PluginDependency { Id = "tumppi066.pathfinding" },
            new PluginDependency { Id = "tumppi066.laneassist" }
        ],
        Tags = ["Driving", "Experimental"]
    };

    public override float TickRate => 20f;

    public override void Init()
    {
        ControlsBackend.Current.RegisterControl(new ControlDefinition
        {
            Id = ToggleControlId,
            Name = "Toggle Overtake Assistant",
            Description = "Enables or disables the MOBIL automatic overtaking assistant.",
            DefaultKeybind = "",
            Type = ControlType.Boolean
        });
    }

    public override void OnEnable()
    {
        Events.Current.Subscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        Events.Current.Subscribe<TrafficData>(TrafficProvider.Current.EventString, OnTraffic);
        Events.Current.Subscribe<PlannedPathData>(PlannedPathTopic, OnPlannedPath);
        ControlsBackend.Current.On(ToggleControlId, OnToggleControl);
        _armed = false;
        TransitionTo(OvertakePhase.Idle);
        RegisterOverlayWindow();
        TryRegisterArCallback();
        UpdateOverlaySnapshot("Ready");
        base.OnEnable();

        Notify("Ready", "Bind Toggle Overtake Assistant to enable automatic overtakes.", NotificationLevel.Information, 6f);
    }

    public override void OnDisable()
    {
        Events.Current.Unsubscribe<GameTelemetryData>(GameTelemetry.Current.EventString, OnTelemetry);
        Events.Current.Unsubscribe<TrafficData>(TrafficProvider.Current.EventString, OnTraffic);
        Events.Current.Unsubscribe<PlannedPathData>(PlannedPathTopic, OnPlannedPath);
        ControlsBackend.Current.UnregisterListener(ToggleControlId, OnToggleControl);
        UnregisterArCallback();
        UnregisterOverlayWindow();
        CancelLaneChangeRequest();
        _armed = false;
        UpdateOverlaySnapshot("Disabled");
        base.OnDisable();
    }

    public override void Shutdown()
    {
        UnregisterArCallback();
        UnregisterOverlayWindow();
        base.Shutdown();
    }

    // ------------------------------------------------------------------
    // Event handlers
    // ------------------------------------------------------------------

    private void OnTelemetry(GameTelemetryData data)
    {
        _telemetry = data;
        UpdateEstimatedForward(data.truckPlacement.coordinate.ToVector3());
        UpdateCurvature(data);
    }

    private void OnTraffic(TrafficData data) => _traffic = data;

    private void OnPlannedPath(PlannedPathData data) => _plannedPath = data;

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
            _armed ? "MOBIL overtake checks are active." : "Automatic overtakes are paused.",
            _armed ? NotificationLevel.Success : NotificationLevel.Information,
            4f);
    }

    // ------------------------------------------------------------------
    // Main loop
    // ------------------------------------------------------------------

    public override void Tick()
    {
        TryRegisterArCallback();

        if (!_armed)
        {
            CancelLaneChangeRequest();
            if (_phase != OvertakePhase.Idle) TransitionTo(OvertakePhase.Idle);
            UpdateOverlaySnapshot("Disabled");
            return;
        }

        if (!CanAssistRun(out string reason))
        {
            if (_phase is OvertakePhase.RequestingLeft or OvertakePhase.Passing or OvertakePhase.RequestingRight)
            {
                Abort(reason);
            }
            UpdateOverlaySnapshot(reason);
            return;
        }

        RebuildProjections();
        SampleManeuverMetrics();

        switch (_phase)
        {
            case OvertakePhase.Idle:
                EvaluateOvertake();
                break;
            case OvertakePhase.RequestingLeft:
                HandleLaneChangePhase(goingLeft: true, OvertakePhase.Passing);
                break;
            case OvertakePhase.Passing:
                HandlePassing();
                break;
            case OvertakePhase.RequestingRight:
                HandleLaneChangePhase(goingLeft: false, OvertakePhase.Cooldown);
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

        // Starting an overtake needs cruise speed; an active one only needs enough
        // speed to finish, so rolling grades do not abort the maneuver mid-way.
        float minimumSpeed = _phase == OvertakePhase.Idle
            ? OvertakeSettings.Current.MinimumSpeed
            : OvertakingMinimumSpeed;
        if (_telemetry.truckFloat.speed < minimumSpeed)
        {
            reason = "below minimum speed";
            return false;
        }

        if (_phase != OvertakePhase.Idle &&
            (Math.Abs(_telemetry.truckFloat.userBrake) > 0.08f ||
             Math.Abs(_telemetry.truckFloat.userSteer) > 0.18f))
        {
            reason = "manual input detected";
            return false;
        }

        if (_estimatedForward.LengthSquared() < 0.5f)
        {
            reason = "heading unavailable";
            return false;
        }

        if (_phase != OvertakePhase.Idle)
        {
            if (_plannedPath == null || _plannedPath.HasError || _plannedPath.PlannedPath.Count == 0)
            {
                reason = "planned path lost";
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Idle: MOBIL evaluation
    // ------------------------------------------------------------------

    private void EvaluateOvertake()
    {
        OvertakeSettings settings = OvertakeSettings.Current;

        if (DateTime.UtcNow < _cooldownUntil)
        {
            TransitionTo(OvertakePhase.Cooldown);
            return;
        }

        if (_curvature > settings.MaximumCurvature)
        {
            return; // lane classification is unreliable on curves; wait for a straight
        }

        if (_telemetry!.truckBool.blinkerLeftActive || _telemetry.truckBool.blinkerRightActive)
        {
            return; // the driver is doing something manual; stay out of the way
        }

        PlannedItem? item = CurrentItem();
        if (item == null || item.GetLane() == null || !item.allowedToLaneChange)
        {
            return; // prefabs and disallowed sections cannot host a lane change
        }

        _overtakeOnLeft = settings.OvertakeOnLeft;

        VehicleProjection? leader = FindLeadingSlowVehicle();
        if (leader == null)
        {
            return;
        }

        float egoSpeed = _telemetry!.truckFloat.speed;
        float gapLeader = FrontBumperGap(leader);
        if (gapLeader > settings.EvaluationGapGate)
        {
            return; // distance gate: nothing to gain yet, and huge gaps make MOBIL stall
        }

        bool targetLeft = _overtakeOnLeft;
        if (TargetLaneHasOncoming(targetLeft))
        {
            return; // that side is oncoming traffic, not an overtaking lane
        }

        (VehicleProjection? tgtLeader, VehicleProjection? tgtFollower) = LaneNeighbors(targetLeft, exclude: null);
        (VehicleProjection? oldFollower, _) = SameLaneNeighbors(exclude: leader);

        // hard time-headway gates, independent of the model
        if (tgtFollower != null)
        {
            float rearGap = RearBumperGap(tgtFollower);
            float thw = rearGap / MathF.Max(egoSpeed, 1f);
            if (thw < settings.RearTimeHeadway)
            {
                return;
            }
        }

        if (tgtLeader != null)
        {
            float frontGap = FrontBumperGap(tgtLeader);
            float thw = frontGap / MathF.Max(egoSpeed, 1f);
            if (thw < settings.FrontTimeHeadway)
            {
                return;
            }
        }

        if (!PassIsFeasible(leader, tgtLeader, gapLeader))
        {
            return;
        }

        // ---- MOBIL incentive + safety ----
        float desired = DesiredSpeed();
        MobilParameters p = settings.ToMobilParameters(settings.OvertakeBias);

        float gapTgtLeader = tgtLeader != null ? FrontBumperGap(tgtLeader) : 500f;
        float aEgo = MobilModel.IdmAcceleration(egoSpeed, gapLeader, leader.SpeedAlong, desired, p);
        float aEgoNew = MobilModel.IdmAcceleration(egoSpeed, gapTgtLeader, tgtLeader?.SpeedAlong ?? egoSpeed, desired, p);

        float aNew = 0f, aNewAfter = 0f;
        if (tgtFollower != null)
        {
            float vF = MathF.Max(tgtFollower.SpeedAlong, 0f);
            float gapFollowerToLeader = tgtLeader != null
                ? BetweenGap(tgtFollower, tgtLeader)
                : settings.LeadingLookahead;
            float gapFollowerToEgo = RearBumperGap(tgtFollower);
            aNew = MobilModel.IdmAcceleration(vF, gapFollowerToLeader, tgtLeader?.SpeedAlong ?? vF, desired, p);
            aNewAfter = MobilModel.IdmAcceleration(vF, gapFollowerToEgo, egoSpeed, desired, p);
        }

        float aOld = 0f, aOldAfter = 0f;
        if (oldFollower != null)
        {
            float vO = MathF.Max(oldFollower.SpeedAlong, 0f);
            aOld = MobilModel.IdmAcceleration(vO, RearBumperGap(oldFollower), egoSpeed, desired, p);
            // after ego leaves, the old follower follows our old leader
            aOldAfter = MobilModel.IdmAcceleration(vO, BetweenGap(oldFollower, leader), leader.SpeedAlong, desired, p);
        }

        MobilVerdict verdict = MobilModel.Evaluate(
            new MobilSituations(aEgo, aEgoNew, aNew, aNewAfter, aOld, aOldAfter), p);

        if (!verdict.ShouldChange)
        {
            return;
        }

        if (!TryRequestLaneChange(targetLeft, out string error))
        {
            Logger.Info($"Overtake Assistant: request rejected ({error}).");
            return;
        }

        _targetVehicleId = leader.Vehicle.id;
        _followerWatchId = tgtFollower?.Vehicle.id;
        StartManeuverMetrics();
        TransitionTo(OvertakePhase.RequestingLeft);
        Logger.Info($"Overtake Assistant: overtaking vehicle {leader.Vehicle.id} " +
                    $"(gap {gapLeader:0}m, closing {(egoSpeed - leader.SpeedAlong) * 3.6f:0.0}km/h, " +
                    $"incentive {verdict.Incentive:0.00}m/s²).");
        Notify("Overtake requested",
            $"MOBIL incentive {verdict.Incentive:0.00} m/s² passed the threshold.",
            NotificationLevel.Information, 4f);
    }

    private float DesiredSpeed()
    {
        float desired = ApplicationState.Current.DesiredSpeed;
        if (desired < 1f) desired = _telemetry?.truckFloat.speedLimit ?? 0f;
        if (desired < 1f) desired = (_telemetry?.truckFloat.speed ?? 0f) + 5f;
        return desired;
    }

    private bool PassIsFeasible(VehicleProjection leader, VehicleProjection? tgtLeader, float gapLeader)
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        float egoSpeed = _telemetry!.truckFloat.speed;
        float closing = egoSpeed - leader.SpeedAlong;
        if (closing < 0.3f)
        {
            return false;
        }

        float tPass = (gapLeader + HalfLen(leader) * 2f) / closing;
        if (tgtLeader != null)
        {
            float frontGap = FrontBumperGap(tgtLeader);
            float relativeUse = (egoSpeed - tgtLeader.SpeedAlong) * tPass;
            return relativeUse <= frontGap - settings.PassFeasibilityMargin;
        }

        // no target-lane leader in scan: the pass must at least fit inside a generous horizon
        return egoSpeed * tPass <= settings.LeadingLookahead + 40f;
    }

    // ------------------------------------------------------------------
    // Lane change request handling
    // ------------------------------------------------------------------

    private PlannedItem? CurrentItem()
    {
        if (_plannedPath == null || _plannedPath.HasError || _plannedPath.PlannedPath.Count == 0)
        {
            return null;
        }

        return _plannedPath.PlannedPath[0];
    }

    /// <summary>
    /// Requests a lane change by setting PlannedItem.targetLaneIndex, exactly like the
    /// user blinker flow in Pathfinding. The candidate neighbour's physical side is
    /// resolved from its world position, so no road-side sign conventions are trusted.
    /// </summary>
    private bool TryRequestLaneChange(bool physicalLeft, out string error)
    {
        error = "";
        PlannedItem? item = CurrentItem();
        if (item == null)
        {
            error = "no planned path item";
            return false;
        }

        if (item.GetLane() == null)
        {
            error = "current item has no lane";
            return false;
        }

        if (!item.allowedToLaneChange)
        {
            error = "lane change not allowed on this section";
            return false;
        }

        if (item.laneIndex == 0)
        {
            error = "current lane index is zero";
            return false;
        }

        int step = item.direction == Direction.Backward ? -1 : 1;
        int? target = null;
        foreach (int candidate in new[] { item.laneIndex + step, item.laneIndex - step })
        {
            if (!IsSameSideNeighbor(item, candidate))
            {
                continue;
            }

            if (LaneCenterOnPhysicalSide(item, candidate, physicalLeft))
            {
                target = candidate;
                break;
            }
        }

        if (target == null)
        {
            error = "no usable lane on the requested physical side";
            return false;
        }

        _originalLaneIndex = item.laneIndex;
        _requestedLaneIndex = target.Value;
        _reassertCount = 0;
        item.targetLaneIndex = target.Value;
        return true;
    }

    private static bool IsSameSideNeighbor(PlannedItem item, int candidate)
    {
        if (candidate == 0 || item.laneIndex == 0)
        {
            return false;
        }

        // Crossing sides would mean crossing the median into oncoming traffic.
        if (Math.Sign(candidate) != Math.Sign(item.laneIndex))
        {
            return false;
        }

        return Math.Abs(candidate - item.laneIndex) == 1;
    }

    private bool LaneCenterOnPhysicalSide(PlannedItem item, int candidate, bool physicalLeft)
    {
        Tuple<Side, int>? lane = item.GetLane(candidate);
        if (lane == null || _telemetry == null)
        {
            return false;
        }

        try
        {
            Vector3 truckPosition = _telemetry.truckPlacement.coordinate.ToVector3();
            float factor = item.GetFactorFor(truckPosition);
            OrientedPoint? point = item.item switch
            {
                ParsedRoad road => road.InterpolateLane(factor, lane.Item1, lane.Item2),
                ParsedRoadList roadList => roadList.InterpolateLane(factor, lane.Item1, lane.Item2),
                _ => null
            };
            if (point == null)
            {
                return false;
            }

            Vector3 delta = point.Value.Position - truckPosition;
            delta.Y = 0f;
            // Left-handed game world (+X = west): this cross product points to the truck's LEFT.
            Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, Vector3.Normalize(_estimatedForward)));
            return (Vector3.Dot(delta, left) > 0f) == physicalLeft;
        }
        catch
        {
            return false;
        }
    }

    private void HandleLaneChangePhase(bool goingLeft, OvertakePhase nextPhase)
    {
        if (!MaintainLaneChangeRequest(goingLeft, out string? failure, out bool committed))
        {
            Abort(failure ?? "lane change failed");
            return;
        }

        if (!committed)
        {
            // pre-commit safety re-checks (target side of the ACTIVE request)
            if (TargetLaneHasOncoming(goingLeft))
            {
                Abort("oncoming traffic appeared in the target lane");
                return;
            }

            (_, VehicleProjection? follower) = LaneNeighbors(goingLeft, exclude: null);
            if (follower != null)
            {
                float thw = RearBumperGap(follower) / MathF.Max(_telemetry!.truckFloat.speed, 1f);
                // hysteresis: only abort when clearly worse than the entry gate
                if (thw < OvertakeSettings.Current.RearTimeHeadway * 0.8f)
                {
                    Abort("target lane rear gap closed");
                    return;
                }
            }

            if (SecondsInPhase > OvertakeSettings.Current.LaneChangeTimeoutSeconds)
            {
                Abort("lane change timed out");
                return;
            }
        }

        if (committed)
        {
            if (nextPhase == OvertakePhase.Passing)
            {
                TransitionTo(OvertakePhase.Passing);
            }
            else
            {
                CompleteOvertake();
            }
        }
    }

    /// <summary>
    /// Keeps the lane change request alive across path rebuilds and detects completion.
    /// Completion = PathLib committed (laneIndex == requested, targetLaneIndex back to 0)
    /// AND the truck physically moved ≥ 1.5 m towards the requested side.
    /// </summary>
    private bool MaintainLaneChangeRequest(bool goingLeft, out string? failure, out bool committed)
    {
        failure = null;
        committed = false;
        PlannedItem? item = CurrentItem();
        if (item == null)
        {
            failure = "planned path lost";
            return false;
        }

        float displacement = SignedLateralDisplacementSincePhaseStart();
        float directed = goingLeft ? displacement : -displacement;
        if (directed > _maxDirectedDisplacement)
        {
            _maxDirectedDisplacement = directed;
        }

        if (item.laneIndex == _requestedLaneIndex && item.targetLaneIndex == 0)
        {
            if (_maxDirectedDisplacement >= CommittedDisplacement)
            {
                committed = true;
                return true;
            }
        }
        else if (item.targetLaneIndex != _requestedLaneIndex)
        {
            // the path was rebuilt or the index was reset (e.g. out-of-bounds); re-assert
            if (item.GetLane() != null && item.allowedToLaneChange)
            {
                item.targetLaneIndex = _requestedLaneIndex;
                _reassertCount++;
                if (_reassertCount > MaxReasserts)
                {
                    failure = "target lane unavailable on this road";
                    return false;
                }
            }
        }

        if (SecondsInPhase > ProgressGraceSeconds && _maxDirectedDisplacement < StartedDisplacement)
        {
            failure = "lane change did not start";
            return false;
        }

        return true;
    }

    private void CancelLaneChangeRequest()
    {
        if (_requestedLaneIndex == 0)
        {
            return;
        }

        PlannedItem? item = CurrentItem();
        if (item != null && item.targetLaneIndex == _requestedLaneIndex)
        {
            // snap back to the planned lane; LaneAssist's cross-track PID pulls the truck over smoothly
            item.targetLaneIndex = 0;
        }

        _requestedLaneIndex = 0;
    }

    // ------------------------------------------------------------------
    // Passing
    // ------------------------------------------------------------------

    private void HandlePassing()
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        if (SecondsInPhase > settings.PassingTimeoutSeconds)
        {
            Abort("passing timed out");
            return;
        }

        bool rearClear;
        TrafficVehicle? target = FindVehicleById(_targetVehicleId);
        if (target != null)
        {
            VehicleProjection projection = ProjectVehicle(target);
            // the passed vehicle's FRONT must be ReturnClearTime * v behind our rear
            float required = EgoRearOffset + settings.ReturnClearTime * MathF.Max(_telemetry!.truckFloat.speed, 1f);
            rearClear = -projection.Longitudinal - HalfLen(projection) >= required;
        }
        else
        {
            // target left the traffic list (despawned / out of range); fall back to elapsed time
            rearClear = SecondsInPhase >= 4f;
        }

        if (!rearClear)
        {
            return;
        }

        if (_curvature > settings.MaximumCurvature)
        {
            return;
        }

        bool returnLeft = !_overtakeOnLeft;
        if (TargetLaneHasOncoming(returnLeft))
        {
            return;
        }

        (VehicleProjection? returnLeader, _) = LaneNeighbors(returnLeft, exclude: null);
        if (returnLeader != null)
        {
            float thw = FrontBumperGap(returnLeader) / MathF.Max(_telemetry!.truckFloat.speed, 1f);
            if (thw < settings.ReturnFrontTimeHeadway)
            {
                return;
            }
        }

        if (!TryRequestLaneChange(returnLeft, out string error))
        {
            Logger.Info($"Overtake Assistant: return rejected ({error}); will retry.");
            return;
        }

        TransitionTo(OvertakePhase.RequestingRight);
        Logger.Info("Overtake Assistant: passing complete, returning to the original lane.");
    }

    private void CompleteOvertake()
    {
        float duration = (float)(DateTime.UtcNow - _maneuverStartedAt).TotalSeconds;
        WriteMetrics("completed", "", duration);
        _targetVehicleId = null;
        _followerWatchId = null;
        _cooldownUntil = DateTime.UtcNow.AddSeconds(OvertakeSettings.Current.CooldownSeconds);
        TransitionTo(OvertakePhase.Cooldown);
        Notify("Overtake complete", $"Duration {duration:0.0}s. Cooling down.", NotificationLevel.Success, 4f);
    }

    private void Abort(string reason)
    {
        CancelLaneChangeRequest();
        if (_phase != OvertakePhase.Idle && _phase != OvertakePhase.Cooldown)
        {
            float duration = (float)(DateTime.UtcNow - _maneuverStartedAt).TotalSeconds;
            WriteMetrics("aborted", reason, duration);
        }

        _targetVehicleId = null;
        _followerWatchId = null;
        _cooldownUntil = DateTime.UtcNow.AddSeconds(OvertakeSettings.Current.CooldownSeconds);
        TransitionTo(OvertakePhase.Cooldown);
        Logger.Info($"Overtake Assistant aborted: {reason}");
    }

    // ------------------------------------------------------------------
    // Traffic perception
    // ------------------------------------------------------------------

    private sealed record VehicleProjection(TrafficVehicle Vehicle, float Longitudinal, float Lateral, float SpeedAlong);

    /// <summary>
    /// Projects every usable vehicle into the truck's local frame (forward / left)
    /// and estimates its speed ALONG our path from position history. Velocity from
    /// position history is convention-free, unlike interpreting the raw quaternion.
    /// </summary>
    private void RebuildProjections()
    {
        _projections.Clear();
        if (_telemetry == null || _traffic == null || _estimatedForward.LengthSquared() < 0.5f)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        Vector3 truckPosition = _telemetry.truckPlacement.coordinate.ToVector3();

        foreach (TrafficVehicle vehicle in _traffic.vehicles)
        {
            if (!IsUsableVehicle(vehicle, truckPosition))
            {
                continue;
            }

            float along;
            if (_vehicleTrack.TryGetValue(vehicle.id, out (Vector3 Position, DateTime Time) previous))
            {
                float dt = (float)(now - previous.Time).TotalSeconds;
                Vector3 delta = vehicle.Position - previous.Position;
                delta.Y = 0f;
                if (dt > 0.03f && dt < 1.5f && delta.LengthSquared() > 0.0004f)
                {
                    along = Vector3.Dot(delta, Vector3.Normalize(_estimatedForward)) / dt;
                }
                else
                {
                    along = vehicle.speed; // fallback: game scalar speed, direction unknown
                }
            }
            else
            {
                along = vehicle.speed;
            }

            _vehicleTrack[vehicle.id] = (vehicle.Position, now);

            Vector3 forward = Vector3.Normalize(_estimatedForward);
            // Left-handed game world (+X = west): this cross product points to the truck's LEFT,
            // so positive Lateral means the vehicle is on the truck's left side.
            Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
            Vector3 offset = vehicle.Position - truckPosition;
            offset.Y = 0f;
            _projections.Add(new VehicleProjection(vehicle, Vector3.Dot(offset, forward), Vector3.Dot(offset, left), along));
        }

        if (_vehicleTrack.Count > 80)
        {
            List<short> stale = _vehicleTrack
                .Where(kvp => (now - kvp.Value.Time).TotalSeconds > 2)
                .Select(kvp => kvp.Key)
                .ToList();
            foreach (short id in stale)
            {
                _vehicleTrack.Remove(id);
            }
        }
    }

    /// <summary>Re-projects a single vehicle (e.g. one tracked by id between rebuilds).</summary>
    private VehicleProjection ProjectVehicle(TrafficVehicle vehicle)
    {
        Vector3 truckPosition = _telemetry!.truckPlacement.coordinate.ToVector3();
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 delta = vehicle.Position - truckPosition;
        delta.Y = 0f;

        float along = vehicle.speed;
        if (_vehicleTrack.TryGetValue(vehicle.id, out (Vector3 Position, DateTime Time) previous))
        {
            float dt = (float)(DateTime.UtcNow - previous.Time).TotalSeconds;
            Vector3 move = vehicle.Position - previous.Position;
            move.Y = 0f;
            if (dt > 0.03f && dt < 1.5f && move.LengthSquared() > 0.0004f)
            {
                along = Vector3.Dot(move, forward) / dt;
            }
        }

        return new VehicleProjection(vehicle, Vector3.Dot(delta, forward), Vector3.Dot(delta, left), along);
    }

    private static bool IsUsableVehicle(TrafficVehicle vehicle, Vector3 truckPosition)
    {
        return vehicle.id != 0
            && vehicle.Size.LengthSquared() > 0.1f
            && vehicle.Position.LengthSquared() > 0.1f
            && !vehicle.isTrailer
            && Math.Abs(vehicle.Position.Y - truckPosition.Y) <= MaxHeightDifference;
    }

    private VehicleProjection? FindLeadingSlowVehicle()
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        float egoSpeed = _telemetry!.truckFloat.speed;

        return _projections
            .Where(v => Math.Abs(v.Lateral) <= settings.SameLaneTolerance)
            .Where(v => v.Longitudinal >= settings.LeadingMinimumGap && v.Longitudinal <= settings.LeadingLookahead)
            .Where(v => egoSpeed - v.SpeedAlong >= settings.MinimumClosingSpeed)
            .OrderBy(v => v.Longitudinal)
            .FirstOrDefault();
    }

    /// <summary>Nearest vehicles ahead/behind in the adjacent lane on the given physical side.</summary>
    private (VehicleProjection? Leader, VehicleProjection? Follower) LaneNeighbors(bool physicalLeft, VehicleProjection? exclude)
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        float center = physicalLeft ? settings.AdjacentLaneCenter : -settings.AdjacentLaneCenter;
        float egoSpeed = _telemetry!.truckFloat.speed;

        VehicleProjection? leader = null;
        VehicleProjection? follower = null;
        foreach (VehicleProjection v in _projections)
        {
            if (exclude != null && v.Vehicle.id == exclude.Vehicle.id)
            {
                continue;
            }

            if (Math.Abs(v.Lateral - center) > settings.AdjacentLaneTolerance)
            {
                continue;
            }

            bool ahead = v.Longitudinal - HalfLen(v) > EgoFrontOffset - 1f;
            if (ahead && v.Longitudinal <= settings.LeadingLookahead + 20f)
            {
                if (leader == null || v.Longitudinal < leader.Longitudinal)
                {
                    leader = v;
                }
            }
            else if (!ahead && -v.Longitudinal <= RearScanDistance)
            {
                if (follower == null || v.Longitudinal > follower.Longitudinal)
                {
                    follower = v;
                }
            }
        }

        return (leader, follower);
    }

    /// <summary>Nearest vehicle behind us in our own lane (the MOBIL "old follower").</summary>
    private (VehicleProjection? Follower, VehicleProjection? LeaderAhead) SameLaneNeighbors(VehicleProjection? exclude)
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        VehicleProjection? follower = null;
        foreach (VehicleProjection v in _projections)
        {
            if (exclude != null && v.Vehicle.id == exclude.Vehicle.id)
            {
                continue;
            }

            if (Math.Abs(v.Lateral) > settings.SameLaneTolerance)
            {
                continue;
            }

            bool behind = v.Longitudinal + HalfLen(v) < -(EgoRearOffset - 1f);
            if (behind && -v.Longitudinal <= RearScanDistance)
            {
                if (follower == null || v.Longitudinal > follower.Longitudinal)
                {
                    follower = v;
                }
            }
        }

        return (follower, null);
    }

    private bool TargetLaneHasOncoming(bool physicalLeft)
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        float center = physicalLeft ? settings.AdjacentLaneCenter : -settings.AdjacentLaneCenter;
        foreach (VehicleProjection v in _projections)
        {
            if (Math.Abs(v.Lateral - center) <= settings.AdjacentLaneTolerance && v.SpeedAlong < -0.5f)
            {
                return true;
            }
        }

        return false;
    }

    private TrafficVehicle? FindVehicleById(short? id)
    {
        if (id == null || _traffic == null)
        {
            return null;
        }

        return _traffic.vehicles.FirstOrDefault(v => v.id == id.Value);
    }

    // ------------------------------------------------------------------
    // Geometry helpers
    // ------------------------------------------------------------------

    private static float HalfLen(VehicleProjection v) =>
        MathF.Max(MathF.Abs(v.Vehicle.Size.Z), MathF.Abs(v.Vehicle.Size.X)) / 2f;

    /// <summary>Bumper gap from our front to the vehicle ahead.</summary>
    private float FrontBumperGap(VehicleProjection ahead) =>
        ahead.Longitudinal - EgoFrontOffset - HalfLen(ahead);

    /// <summary>Bumper gap from our rear to the vehicle behind.</summary>
    private float RearBumperGap(VehicleProjection behind) =>
        -behind.Longitudinal - EgoRearOffset - HalfLen(behind);

    /// <summary>Bumper gap between two vehicles, rear then front.</summary>
    private static float BetweenGap(VehicleProjection rear, VehicleProjection front) =>
        front.Longitudinal - rear.Longitudinal - HalfLen(rear) - HalfLen(front);

    private float SecondsInPhase => (float)(DateTime.UtcNow - _phaseStartedAt).TotalSeconds;

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
                // |dθ/dt| / v = 1/R; works regardless of the game's heading sign convention.
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

    private float SignedLateralDisplacementSincePhaseStart()
    {
        // uses the phase-start forward vector; positive = moved to the truck's left
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        Vector3 current = _telemetry?.truckPlacement.coordinate.ToVector3() ?? Vector3.Zero;
        Vector3 delta = current - CurrentPhaseStartPosition;
        delta.Y = 0f;
        return Vector3.Dot(delta, left);
    }

    private Vector3 CurrentPhaseStartPosition =>
        _phaseStartPosition ?? _telemetry?.truckPlacement.coordinate.ToVector3() ?? Vector3.Zero;

    private Vector3? _phaseStartPosition;

    private void TransitionTo(OvertakePhase phase)
    {
        _phase = phase;
        _phaseStartedAt = DateTime.UtcNow;
        _phaseStartPosition = _telemetry?.truckPlacement.coordinate.ToVector3();
        _maxDirectedDisplacement = 0f;
        _reassertCount = 0;
    }

    // ------------------------------------------------------------------
    // Metrics
    // ------------------------------------------------------------------

    private void StartManeuverMetrics()
    {
        _maneuverStartedAt = DateTime.UtcNow;
        _minFrontGap = float.MaxValue;
        _minRearGap = float.MaxValue;
        _minRearThw = float.MaxValue;
        _followerMinAccel = float.MaxValue;
        _maxLateralAccel = 0f;
    }

    private void SampleManeuverMetrics()
    {
        if (_phase is not (OvertakePhase.RequestingLeft or OvertakePhase.Passing or OvertakePhase.RequestingRight))
        {
            return;
        }

        float egoSpeed = _telemetry?.truckFloat.speed ?? 0f;
        bool targetLeft = _phase == OvertakePhase.RequestingRight ? !_overtakeOnLeft : _overtakeOnLeft;
        (VehicleProjection? leader, VehicleProjection? follower) = LaneNeighbors(targetLeft, exclude: null);

        if (leader != null)
        {
            float gap = FrontBumperGap(leader);
            if (gap < _minFrontGap) _minFrontGap = gap;
        }

        if (follower != null)
        {
            float gap = RearBumperGap(follower);
            if (gap < _minRearGap) _minRearGap = gap;
            float thw = gap / MathF.Max(egoSpeed, 1f);
            if (thw < _minRearThw) _minRearThw = thw;
        }

        if (_followerWatchId != null)
        {
            TrafficVehicle? watched = FindVehicleById(_followerWatchId);
            if (watched != null && watched.acceleration < _followerMinAccel)
            {
                _followerMinAccel = watched.acceleration;
            }
        }

        float lateralAccel = _curvature * egoSpeed * egoSpeed;
        if (lateralAccel > _maxLateralAccel) _maxLateralAccel = lateralAccel;
    }

    private void WriteMetrics(string outcome, string reason, float duration)
    {
        if (!OvertakeSettings.Current.LogMetrics)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "ETS2LA", "OvertakeAssistant");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "metrics.csv");
            if (!File.Exists(file))
            {
                File.WriteAllText(file,
                    "utc,outcome,reason,duration_s,original_lane,requested_lane,min_front_gap_m,min_rear_gap_m,min_rear_thw_s,follower_min_accel_ms2,max_lateral_accel_ms2\n");
            }

            string line = string.Join(',',
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                outcome,
                reason.Replace(',', ';'),
                duration.ToString("0.0"),
                _originalLaneIndex,
                _requestedLaneIndex,
                _minFrontGap == float.MaxValue ? "" : _minFrontGap.ToString("0"),
                _minRearGap == float.MaxValue ? "" : _minRearGap.ToString("0"),
                _minRearThw == float.MaxValue ? "" : _minRearThw.ToString("0.00"),
                _followerMinAccel == float.MaxValue ? "" : _followerMinAccel.ToString("0.00"),
                _maxLateralAccel.ToString("0.00"));
            File.AppendAllText(file, line + "\n");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Overtake Assistant: failed to write metrics: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Overlay / AR
    // ------------------------------------------------------------------

    public OverlaySnapshot CurrentSnapshot => _overlaySnapshot;

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
        if (ShowOverlay)
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
        if (_arCallbackRegistered || !ShowAr)
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
        if (!ShowOverlay)
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
        DrawOverlayRow("Overtake lane", snapshot.OvertakeLaneClear ? "Clear" : "Blocked");
        DrawOverlayRow("Return lane", snapshot.ReturnLaneClear ? "Clear" : "Blocked");
        DrawOverlayRow("Cooldown", snapshot.CooldownRemainingSeconds > 0 ? $"{snapshot.CooldownRemainingSeconds:0.0}s" : "Ready");
    }

    private static void DrawOverlayRow(string label, string value)
    {
        ImGui.TextColored(new Vector4(0.68f, 0.68f, 0.68f, 1f), $"{label}:");
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.95f, 0.95f, 0.95f, 1f), value);
    }

    private void RenderAr()
    {
        if (!ShowAr)
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

        if (_telemetry != null && _estimatedForward.LengthSquared() >= 0.5f)
        {
            Vector3 truckPosition = _telemetry.truckPlacement.coordinate.ToVector3();
            ar.Draw3DLine(new ARCoordinate(truckPosition + Vector3.UnitY * 1.0f),
                new ARCoordinate(vehicle.Position + Vector3.UnitY * 1.0f),
                color,
                2.0f);

            OvertakeSettings settings = OvertakeSettings.Current;
            if (_phase is OvertakePhase.Idle or OvertakePhase.RequestingLeft)
            {
                DrawLaneClearanceZone(ar, truckPosition, settings.OvertakeOnLeft, settings.LeadingLookahead, RearScanDistance, ArCandidateColor, "Overtake scan");
            }

            if (_phase is OvertakePhase.Passing or OvertakePhase.RequestingRight)
            {
                DrawLaneClearanceZone(ar, truckPosition, !settings.OvertakeOnLeft, settings.LeadingLookahead, RearScanDistance, ArReturnZoneColor, "Return scan");
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

    private void DrawLaneClearanceZone(ARRenderer ar, Vector3 truckPosition, bool physicalLeft, float frontClearance, float rearClearance, uint color, string label)
    {
        Vector3 forward = Vector3.Normalize(_estimatedForward);
        // Left-handed game world (+X = west): this cross product points to the truck's LEFT.
        Vector3 left = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));
        float lateral = physicalLeft ? OvertakeSettings.Current.AdjacentLaneCenter : -OvertakeSettings.Current.AdjacentLaneCenter;
        Vector3 laneCenter = truckPosition + left * lateral + Vector3.UnitY * 0.12f;
        float halfWidth = OvertakeSettings.Current.AdjacentLaneTolerance;

        Vector3 frontCenter = laneCenter + forward * frontClearance;
        Vector3 rearCenter = laneCenter - forward * rearClearance;

        ARCoordinate frontLeft = frontCenter - left * halfWidth;
        ARCoordinate frontRight = frontCenter + left * halfWidth;
        ARCoordinate rearRight = rearCenter + left * halfWidth;
        ARCoordinate rearLeft = rearCenter - left * halfWidth;

        ar.Draw3DQuad(frontLeft, frontRight, rearRight, rearLeft, color, thickness: 2.0f);
        ar.Draw3DLine(new ARCoordinate(laneCenter - forward * rearClearance), new ARCoordinate(laneCenter + forward * frontClearance), color, 2.0f);

        Vector3 labelPosition = truckPosition + left * lateral + forward * Math.Min(frontClearance, 25f) + Vector3.UnitY * 2.2f;
        ar.Draw3DText(new ARCoordinate(labelPosition), $"{label} +{frontClearance:0}m / -{rearClearance:0}m", color);
    }

    private void UpdateOverlaySnapshot(string status)
    {
        GameTelemetryData? telemetry = _telemetry;
        OvertakeSettings settings = OvertakeSettings.Current;
        Units units = ApplicationState.Current.DisplayUnits;
        string speedUnit = UnitConversions.GetUnitAbbreviation(UnitType.Speed, units);
        float cooldownRemaining = Math.Max(0f, (float)(_cooldownUntil - DateTime.UtcNow).TotalSeconds);

        bool overtakeClear = false;
        bool returnClear = false;
        VehicleProjection? highlight = null;

        if (telemetry != null && _estimatedForward.LengthSquared() >= 0.5f && _projections.Count > 0)
        {
            overtakeClear = IsLaneClear(settings.OvertakeOnLeft);
            returnClear = IsLaneClear(!settings.OvertakeOnLeft);

            TrafficVehicle? target = FindVehicleById(_targetVehicleId);
            if (target != null)
            {
                highlight = _projections.FirstOrDefault(p => p.Vehicle.id == target.id);
            }
            else
            {
                highlight = FindLeadingSlowVehicle();
            }
        }

        VehicleArInfo? vehicleInfo = highlight == null
            ? null
            : new VehicleArInfo(
                highlight.Vehicle.id,
                highlight.Vehicle.Position,
                Math.Abs(highlight.Vehicle.Size.X),
                Math.Abs(highlight.Vehicle.Size.Y),
                Math.Abs(highlight.Vehicle.Size.Z));

        string speedDelta = highlight == null || telemetry == null
            ? ""
            : FormatSignedSpeed(telemetry.truckFloat.speed - highlight.SpeedAlong, units, speedUnit);

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
            overtakeClear,
            returnClear,
            cooldownRemaining,
            vehicleInfo);
    }

    private bool IsLaneClear(bool physicalLeft)
    {
        OvertakeSettings settings = OvertakeSettings.Current;
        if (TargetLaneHasOncoming(physicalLeft))
        {
            return false;
        }

        float egoSpeed = _telemetry?.truckFloat.speed ?? 0f;
        (VehicleProjection? leader, VehicleProjection? follower) = LaneNeighbors(physicalLeft, exclude: null);
        if (follower != null)
        {
            float thw = RearBumperGap(follower) / MathF.Max(egoSpeed, 1f);
            if (thw < settings.RearTimeHeadway)
            {
                return false;
            }
        }

        if (leader != null)
        {
            float thw = FrontBumperGap(leader) / MathF.Max(egoSpeed, 1f);
            if (thw < settings.FrontTimeHeadway)
            {
                return false;
            }
        }

        return true;
    }

    private string BuildPhaseStatus()
    {
        return _phase switch
        {
            OvertakePhase.Idle => DateTime.UtcNow < _cooldownUntil ? "Cooling down" : "Watching traffic",
            OvertakePhase.RequestingLeft => "Requesting overtake lane",
            OvertakePhase.Passing => "Passing target",
            OvertakePhase.RequestingRight => "Returning to lane",
            OvertakePhase.Cooldown => "Cooling down",
            _ => _phase.ToString()
        };
    }

    private static string FormatSignedSpeed(float speed, Units units, string unit)
    {
        float display = UnitConversions.FromScientificUnits(UnitType.Speed, speed, units);
        return $"{display:+0.0;-0.0;0.0} {unit}";
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

    public sealed record OverlaySnapshot(
        bool Armed,
        string Phase,
        string Status,
        string TargetText,
        string ArText,
        bool OvertakeLaneClear,
        bool ReturnLaneClear,
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
}

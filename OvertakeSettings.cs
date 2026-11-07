using System;
using ETS2LA.Settings;

namespace OvertakeAssistant;

[Serializable]
public class OvertakeSettings
{
    [NonSerialized]
    private static readonly Lazy<OvertakeSettings> _instance = new(() => new OvertakeSettings(loadSettings: true));

    // ---- behaviour ----

    /// <summary>Physical side used to overtake: true = pass on the truck's left (right-hand traffic), false = pass right.</summary>
    public bool OvertakeOnLeft = true;

    public float MinimumSpeed = 55f / 3.6f;

    /// <summary>No overtake decisions on curves sharper than 1 / 800 m (classification gets unreliable).</summary>
    public float MaximumCurvature = 1f / 800f;

    public float LeadingLookahead = 85f;
    public float LeadingMinimumGap = 10f;

    /// <summary>Minimum approach speed towards the slow leader in m/s (default 5 km/h).</summary>
    public float MinimumClosingSpeed = 5f / 3.6f;

    public float CooldownSeconds = 10f;

    // ---- lane geometry ----

    public float SameLaneTolerance = 2.4f;
    public float AdjacentLaneCenter = 4.5f;
    public float AdjacentLaneTolerance = 2.4f;

    // ---- decision gates ----

    /// <summary>Only evaluate overtakes when the slow leader is closer than this (meters). Prevents "stuck driver" evaluations at huge gaps.</summary>
    public float EvaluationGapGate = 70f;

    /// <summary>Minimum time headway (s) to the target lane follower before cutting in.</summary>
    public float RearTimeHeadway = 2.0f;

    /// <summary>Minimum time headway (s) to the target lane leader before cutting in.</summary>
    public float FrontTimeHeadway = 3.0f;

    /// <summary>Seconds of clear trailing distance behind the passed vehicle before returning.</summary>
    public float ReturnClearTime = 1.5f;

    /// <summary>Minimum time headway (s) to the next vehicle ahead in the lane we return to.</summary>
    public float ReturnFrontTimeHeadway = 2.0f;

    /// <summary>Extra distance margin (m) required to guarantee the pass can complete before the target lane leader.</summary>
    public float PassFeasibilityMargin = 15f;

    // ---- maneuver timeouts ----

    public float LaneChangeTimeoutSeconds = 8f;
    public float PassingTimeoutSeconds = 30f;

    // ---- MOBIL / IDM ----

    public float Politeness = 0.3f;
    public float OvertakeBias = 0.25f;
    public float ReturnBias = -0.15f;
    public float SafeBrakingLimit = 1.2f;
    public float IdmDesiredAccel = 0.6f;
    public float IdmComfortDecel = 1.5f;
    public float IdmMinimumGap = 4f;
    public float IdmTimeHeadway = 2.0f;
    public float IdmAccelExponent = 1.0f;

    // ---- UI / logging ----

    public bool ShowOverlay = true;
    public bool ShowAr = true;
    public bool LogMetrics = true;

    [NonSerialized]
    private SettingsHandler? _settingsHandler;

    public static OvertakeSettings Current => _instance.Value;

    public OvertakeSettings()
    {
    }

    public OvertakeSettings(bool loadSettings)
    {
        if (loadSettings)
        {
            _settingsHandler = new SettingsHandler();
            OvertakeSettings? loaded = _settingsHandler.Load<OvertakeSettings>("local.overtakeassistant.json");
            if (loaded != null)
            {
                OvertakeOnLeft = loaded.OvertakeOnLeft;
                MinimumSpeed = loaded.MinimumSpeed;
                MaximumCurvature = loaded.MaximumCurvature;
                LeadingLookahead = loaded.LeadingLookahead;
                LeadingMinimumGap = loaded.LeadingMinimumGap;
                MinimumClosingSpeed = loaded.MinimumClosingSpeed;
                CooldownSeconds = loaded.CooldownSeconds;
                SameLaneTolerance = loaded.SameLaneTolerance;
                AdjacentLaneCenter = loaded.AdjacentLaneCenter;
                AdjacentLaneTolerance = loaded.AdjacentLaneTolerance;
                EvaluationGapGate = loaded.EvaluationGapGate;
                RearTimeHeadway = loaded.RearTimeHeadway;
                FrontTimeHeadway = loaded.FrontTimeHeadway;
                ReturnClearTime = loaded.ReturnClearTime;
                ReturnFrontTimeHeadway = loaded.ReturnFrontTimeHeadway;
                PassFeasibilityMargin = loaded.PassFeasibilityMargin;
                LaneChangeTimeoutSeconds = loaded.LaneChangeTimeoutSeconds;
                PassingTimeoutSeconds = loaded.PassingTimeoutSeconds;
                Politeness = loaded.Politeness;
                OvertakeBias = loaded.OvertakeBias;
                ReturnBias = loaded.ReturnBias;
                SafeBrakingLimit = loaded.SafeBrakingLimit;
                IdmDesiredAccel = loaded.IdmDesiredAccel;
                IdmComfortDecel = loaded.IdmComfortDecel;
                IdmMinimumGap = loaded.IdmMinimumGap;
                IdmTimeHeadway = loaded.IdmTimeHeadway;
                IdmAccelExponent = loaded.IdmAccelExponent;
                ShowOverlay = loaded.ShowOverlay;
                ShowAr = loaded.ShowAr;
                LogMetrics = loaded.LogMetrics;
            }
        }
    }

    public MobilParameters ToMobilParameters(float biasOverride)
    {
        return new MobilParameters
        {
            Politeness = Politeness,
            BiasThreshold = biasOverride,
            SafeBrakingLimit = SafeBrakingLimit,
            DesiredAccel = IdmDesiredAccel,
            ComfortDecel = IdmComfortDecel,
            MinimumGap = IdmMinimumGap,
            TimeHeadway = IdmTimeHeadway,
            AccelExponent = IdmAccelExponent
        };
    }

    public void Save()
    {
        _settingsHandler?.Save("local.overtakeassistant.json", this);
    }
}

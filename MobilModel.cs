namespace OvertakeAssistant;

/// <summary>
/// Tunable parameters of the IDM-based MOBIL lane change model
/// (Kesting, Treiber &amp; Helbing 2007). Defaults are tuned for a
/// fully loaded ETS2 truck, not a passenger car.
/// </summary>
public sealed class MobilParameters
{
    /// <summary>Weight given to the acceleration changes imposed on other drivers (0..1).</summary>
    public float Politeness = 0.3f;

    /// <summary>Switching bias in m/s². Positive values make changes harder (keep-lane bias), negative ones easier.</summary>
    public float BiasThreshold = 0.25f;

    /// <summary>Maximum deceleration (m/s²) the new follower may be forced into. The safety criterion.</summary>
    public float SafeBrakingLimit = 1.2f;

    /// <summary>IDM maximum acceleration a in m/s² (loaded truck: ~0.5).</summary>
    public float DesiredAccel = 0.6f;

    /// <summary>IDM comfortable deceleration b in m/s².</summary>
    public float ComfortDecel = 1.5f;

    /// <summary>IDM minimum standstill gap s0 in meters.</summary>
    public float MinimumGap = 4f;

    /// <summary>IDM desired time headway T in seconds.</summary>
    public float TimeHeadway = 2.0f;

    /// <summary>IDM acceleration exponent delta (1 for trucks, 4 for cars).</summary>
    public float AccelExponent = 1.0f;
}

/// <summary>
/// The six IDM accelerations that feed the MOBIL incentive criterion.
/// "Current" values use the present traffic situation, "AfterChange"
/// values use the hypothetical situation after ego moved to the target lane.
/// </summary>
public readonly record struct MobilSituations(
    float EgoCurrent,
    float EgoAfterChange,
    float NewFollowerCurrent,
    float NewFollowerAfterChange,
    float OldFollowerCurrent,
    float OldFollowerAfterChange);

public static class MobilModel
{
    /// <summary>
    /// Intelligent Driver Model acceleration in m/s² for one car-following pair.
    /// </summary>
    /// <param name="speed">Ego speed in m/s.</param>
    /// <param name="gap">Bumper-to-tail gap to the leader in meters.</param>
    /// <param name="leaderSpeed">Leader speed in m/s.</param>
    /// <param name="desiredSpeed">IDM desired speed v0 in m/s.</param>
    public static float IdmAcceleration(float speed, float gap, float leaderSpeed, float desiredSpeed, MobilParameters p)
    {
        float effectiveGap = MathF.Max(gap, 0.01f);
        float dv = speed - leaderSpeed;
        float sStar = p.MinimumGap
            + MathF.Max(0f, speed * p.TimeHeadway + speed * dv / (2f * MathF.Sqrt(p.DesiredAccel * p.ComfortDecel)));
        float v0 = MathF.Max(desiredSpeed, 1f);
        float freeTerm = 1f - MathF.Pow(MathF.Max(speed, 0f) / v0, p.AccelExponent);
        float interactionTerm = sStar / effectiveGap;
        return p.DesiredAccel * (freeTerm - interactionTerm * interactionTerm);
    }

    /// <summary>
    /// MOBIL incentive + safety criteria.
    /// Incentive: (ã_c − a_c) + p·[(ã_n − a_n) + (ã_o − a_o)] ≥ a_th
    /// Safety: ã_n ≥ −b_safe
    /// </summary>
    public static MobilVerdict Evaluate(in MobilSituations s, in MobilParameters p)
    {
        float incentive = (s.EgoAfterChange - s.EgoCurrent)
            + p.Politeness * ((s.NewFollowerAfterChange - s.NewFollowerCurrent)
                            + (s.OldFollowerAfterChange - s.OldFollowerCurrent));

        bool incentivePassed = incentive >= p.BiasThreshold;
        bool safetyPassed = s.NewFollowerAfterChange >= -p.SafeBrakingLimit;

        return new MobilVerdict(incentivePassed && safetyPassed, incentive, s.NewFollowerAfterChange, incentivePassed, safetyPassed);
    }
}

public readonly record struct MobilVerdict(
    bool ShouldChange,
    float Incentive,
    float NewFollowerAccelAfterChange,
    bool IncentivePassed,
    bool SafetyPassed);

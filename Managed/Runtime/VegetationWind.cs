using ArisenEngine.Resources.Serialization;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;
using System.Numerics;

namespace ArisenEngine.Vegetation;

/// <summary>
/// Frame-global wind inputs. Every value is bounded so that culling and shadow fitting can stay
/// conservative without re-reading the wind every frame: a species with stiffness <c>s</c> leans
/// by at most <see cref="MaximumBendFraction"/>, so the horizontal travel of a vertex is capped by
/// its own height above the instance origin no matter how the gust phases advance.
/// </summary>
public readonly record struct VegetationWindSettings
{
    public const float MinimumStrength = 0.0f;
    public const float MaximumStrength = 12.0f;
    public const float MinimumGustAmplitude = 0.0f;
    public const float MaximumGustAmplitude = 1.0f;
    public const float MinimumGustFrequencyHz = 0.01f;
    public const float MaximumGustFrequencyHz = 2.0f;
    public const float MaximumTimeSeconds = 86_400.0f;

    /// <summary>
    /// Ratio between the secondary and primary gust frequencies. The value is irrational
    /// enough that the two waves never repeat a joint phase inside one session.
    /// </summary>
    public const float SecondaryGustFrequencyRatio = 1.618f;

    /// <summary>
    /// Spatial frequency of the gust wave along the wind direction, in radians per metre. The
    /// vegetation shaders hold the same value as their <c>VEGETATION_GUST_SPATIAL_FREQUENCY</c>
    /// literal; the CPU mirrors it because it anchors the wave to the frame the shader receives
    /// positions in instead of to the absolute world position of each vertex.
    /// </summary>
    public const float GustSpatialFrequency = 0.35f;

    /// <summary>
    /// Spatial scale of the secondary gust wave relative to the primary one. Mirrors the shader
    /// literal <c>VEGETATION_GUST_SPATIAL_SECONDARY_SCALE</c>.
    /// </summary>
    public const float SecondaryGustSpatialScale = 0.5f;

    /// <summary>
    /// Lean, as a fraction of a right angle, per (m/s of wind x unit species stiffness). The
    /// vegetation shaders read the resulting <see cref="BendStrength"/> straight out of the
    /// frame constants, so this constant stays the single source of truth shared by the CPU
    /// conservative bounds and the GPU displacement.
    /// </summary>
    public const float BendScale = 0.12f;

    private static readonly Vector3 s_DefaultDirection =
        Vector3.Normalize(new Vector3(0.82f, 0.0f, 0.57f));

    private VegetationWindSettings(
        Vector3 direction,
        float strength,
        float gustAmplitude,
        float gustFrequencyHz,
        float timeSeconds)
    {
        Direction = direction;
        Strength = strength;
        GustAmplitude = gustAmplitude;
        GustFrequencyHz = gustFrequencyHz;
        TimeSeconds = timeSeconds;
    }

    public Vector3 Direction { get; }

    public float Strength { get; }

    public float GustAmplitude { get; }

    public float GustFrequencyHz { get; }

    public float TimeSeconds { get; }

    /// <summary>
    /// Strength scaled by <see cref="BendScale"/>: the shader turns this into a bounded lean by
    /// multiplying it with the species stiffness and the current gust envelope.
    /// </summary>
    public float BendStrength => Strength * BendScale;

    public bool IsValid =>
        IsFiniteDirection(Direction) &&
        float.IsFinite(Strength) &&
        Strength >= MinimumStrength &&
        Strength <= MaximumStrength &&
        IsFiniteInRange(GustAmplitude, MinimumGustAmplitude, MaximumGustAmplitude) &&
        IsFiniteInRange(GustFrequencyHz, MinimumGustFrequencyHz, MaximumGustFrequencyHz) &&
        float.IsFinite(TimeSeconds) &&
        TimeSeconds >= 0.0f;

    public static VegetationWindSettings Default { get; } = Create(
        s_DefaultDirection,
        strength: 3.5f,
        gustAmplitude: 0.35f,
        gustFrequencyHz: 0.21f,
        timeSeconds: 0.0f);

    public static VegetationWindSettings Create(
        Vector3 direction,
        float strength,
        float gustAmplitude,
        float gustFrequencyHz,
        float timeSeconds)
    {
        return new VegetationWindSettings(
            NormalizeDirection(direction),
            ClampFinite(strength, MinimumStrength, MaximumStrength),
            ClampFinite(gustAmplitude, MinimumGustAmplitude, MaximumGustAmplitude),
            ClampFinite(gustFrequencyHz, MinimumGustFrequencyHz, MaximumGustFrequencyHz),
            float.IsFinite(timeSeconds)
                ? MathF.Max(0.0f, MathF.Min(timeSeconds, MaximumTimeSeconds))
                : 0.0f);
    }

    public VegetationWindSettings WithTime(float timeSeconds) => Create(
        Direction,
        Strength,
        GustAmplitude,
        GustFrequencyHz,
        timeSeconds);

    public VegetationWindSettings WithParameters(
        Vector3 direction,
        float strength,
        float gustAmplitude,
        float gustFrequencyHz) => Create(
        direction,
        strength,
        gustAmplitude,
        gustFrequencyHz,
        TimeSeconds);

    /// <summary>
    /// Gust angles are wrapped on the CPU so the shader never multiplies a large time value
    /// by a frequency. The wrapped values stay exact for arbitrarily long sessions.
    /// </summary>
    public (float Primary, float Secondary) ResolveGustPhases()
    {
        double primary = TimeSeconds * GustFrequencyHz;
        double secondary = primary * SecondaryGustFrequencyRatio;
        return (WrapToAngle(primary), WrapToAngle(secondary));
    }

    /// <summary>
    /// Gust angles for one frame anchor: the world position that the vertex positions a pass uploads
    /// are expressed relative to. A gust is a wave over the world, so its spatial term has to be
    /// sampled from the absolute world position of a vertex while the shader only has that vertex's
    /// frame-relative position. The dot product splits without loss:
    /// <c>dot(direction, local + anchor) = dot(direction, local) + dot(direction, anchor)</c>, so
    /// the CPU folds the anchor half into the wrapped angles and leaves the shader untouched. The
    /// vegetation passes anchor on the render camera, which makes the wave a property of the world
    /// alone: neither a world-origin rebase nor a camera-relative pass frame can move or re-phase
    /// it, and the shader keeps evaluating its half on small frame-relative numbers instead of
    /// losing precision to a large absolute coordinate.
    /// </summary>
    public (float Primary, float Secondary) ResolveGustPhases(WorldPosition frameAnchor)
    {
        if (!frameAnchor.IsFinite)
        {
            throw new ArgumentException(
                "[Vegetation] Gust phases require a finite frame anchor.",
                nameof(frameAnchor));
        }

        (float primary, float secondary) = ResolveGustPhases();
        double spatial =
            ((double)Direction.X * frameAnchor.X + (double)Direction.Z * frameAnchor.Z) *
            GustSpatialFrequency;
        return (
            WrapRadians(primary + spatial),
            WrapRadians(secondary + spatial * SecondaryGustSpatialScale));
    }

    /// <summary>
    /// Worst-case lean for a species, as a fraction of a right angle. The gust envelope is
    /// bounded, the stiffness is clamped, and the lean saturates at one, so the shader can never
    /// displace a vertex further than its own distance above the instance origin.
    /// </summary>
    public static float MaximumBendFraction(float windResponse)
    {
        float stiffness = float.IsFinite(windResponse)
            ? Math.Clamp(windResponse, 0.0f, 1.0f)
            : 0.0f;
        return MathF.Min(
            1.0f,
            stiffness * MaximumStrength * (1.0f + MaximumGustAmplitude) * BendScale);
    }

    /// <summary>
    /// Conservative horizontal travel of the tallest point of an instance, in metres. Bakes the
    /// bounded lean into a single build-time bound: the shader displaces a vertex by at most
    /// <c>lean x localHeight</c>, so inflating by this value keeps culling and shadow fitting
    /// valid for every reachable wind configuration.
    /// </summary>
    public static float MaximumHorizontalDisplacement(
        float windResponse,
        float scaledHeight)
    {
        float height = float.IsFinite(scaledHeight)
            ? MathF.Max(0.0f, scaledHeight)
            : 0.0f;
        return MaximumBendFraction(windResponse) * height;
    }

    private static float WrapToAngle(double turns)
    {
        double angle = turns - Math.Floor(turns);
        return (float)(angle * Math.Tau);
    }

    private static float WrapRadians(double radians)
    {
        double wrapped = radians - (Math.Tau * Math.Floor(radians / Math.Tau));
        return (float)wrapped;
    }

    private static Vector3 NormalizeDirection(Vector3 direction)
    {
        if (!IsFiniteDirection(direction))
        {
            return s_DefaultDirection;
        }

        var planar = new Vector3(direction.X, 0.0f, direction.Z);
        return planar.LengthSquared() > 1.0e-6f
            ? Vector3.Normalize(planar)
            : s_DefaultDirection;
    }

    private static bool IsFiniteDirection(Vector3 direction) =>
        float.IsFinite(direction.X) &&
        float.IsFinite(direction.Y) &&
        float.IsFinite(direction.Z);

    private static bool IsFiniteInRange(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static float ClampFinite(float value, float minimum, float maximum) =>
        float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : minimum;
}

/// <summary>
/// Frame-global wind provider consumed by the vegetation render feature.
/// </summary>
public interface IVegetationWindSource
{
    VegetationWindSettings Current { get; }
}

/// <summary>
/// Deterministic control surface for the wind clock. Rendering only reads
/// <see cref="IVegetationWindSource"/>; pinning is reserved for harnesses that have to compare
/// independently launched processes against each other and for tests. The wind is the only
/// time-driven input of the renderer, so a shared pinned time is what makes a cross-process
/// vegetation comparison well defined.
/// </summary>
public interface IVegetationWindClockControl
{
    /// <summary>
    /// True while the wind time is held at an explicit value instead of advancing with the
    /// kernel clock.
    /// </summary>
    bool HasTimeOverride { get; }

    /// <summary>
    /// Holds the wind at an explicit time. Every process that pins the same value renders the
    /// exact same vegetation pose for the same camera, which is what the comparison relies on.
    /// </summary>
    void SetTimeSeconds(float seconds);

    /// <summary>
    /// Returns the wind to the kernel clock. Idempotent.
    /// </summary>
    void ClearTimeOverride();
}

/// <summary>
/// Default wind source. Direction, strength, gust envelope and height response are
/// explicitly configured; the gust time advances from the kernel clock unless a
/// deterministic override is supplied by a validator or test.
/// </summary>
public sealed class VegetationWindService : IVegetationWindSource, IVegetationWindClockControl
{
    private const float NoTimeOverride = -1.0f;

    private readonly object m_Gate = new();
    private VegetationWindSettings m_Configured = VegetationWindSettings.Default;
    private float m_TimeOverrideSeconds = NoTimeOverride;

    public VegetationWindSettings Current
    {
        get
        {
            float time;
            lock (m_Gate)
            {
                time = m_TimeOverrideSeconds >= 0.0f
                    ? m_TimeOverrideSeconds
                    : (float)Time.elapsedTime;
                return m_Configured.WithTime(time);
            }
        }
    }

    public bool HasTimeOverride
    {
        get
        {
            lock (m_Gate)
            {
                return m_TimeOverrideSeconds >= 0.0f;
            }
        }
    }

    public void Configure(
        Vector3 direction,
        float strength,
        float gustAmplitude,
        float gustFrequencyHz)
    {
        lock (m_Gate)
        {
            m_Configured = m_Configured.WithParameters(
                direction,
                strength,
                gustAmplitude,
                gustFrequencyHz);
            KernelLog.Info(
                "[Vegetation] Wind configured | Direction: " +
                $"{m_Configured.Direction.X:F3},{m_Configured.Direction.Z:F3} | " +
                $"Strength: {m_Configured.Strength:F2} | Gust: {m_Configured.GustAmplitude:F2} " +
                $"at {m_Configured.GustFrequencyHz:F2}Hz");
        }
    }

    public void SetTimeSeconds(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds < 0.0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seconds),
                "[Vegetation] Wind time override must be finite and non-negative.");
        }

        lock (m_Gate)
        {
            m_TimeOverrideSeconds = MathF.Min(seconds, VegetationWindSettings.MaximumTimeSeconds);
        }
    }

    public void ClearTimeOverride()
    {
        lock (m_Gate)
        {
            m_TimeOverrideSeconds = NoTimeOverride;
        }
    }

    public void Clear()
    {
        lock (m_Gate)
        {
            m_Configured = VegetationWindSettings.Default;
            m_TimeOverrideSeconds = NoTimeOverride;
        }
    }
}

namespace ArisenEngine.Vegetation;

public enum VegetationAuthoringPreviewState
{
    None = 0,
    Queued = 1,
    Ready = 2,
    Failed = 3
}

public sealed class VegetationAuthoringPreviewSnapshot
{
    public static VegetationAuthoringPreviewSnapshot Empty { get; } = new(
        Guid.Empty,
        0,
        VegetationAuthoringPreviewState.None,
        0,
        0,
        string.Empty);

    public VegetationAuthoringPreviewSnapshot(
        Guid biomeGuid,
        ulong revision,
        VegetationAuthoringPreviewState state,
        int clusterCount,
        int instanceCount,
        string diagnostic)
    {
        if (clusterCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterCount));
        }

        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }

        if (state == VegetationAuthoringPreviewState.None)
        {
            if (biomeGuid != Guid.Empty || revision != 0 || clusterCount != 0 || instanceCount != 0)
            {
                throw new ArgumentException(
                    "An empty vegetation preview cannot carry identity or generated content.",
                    nameof(state));
            }
        }
        else if (biomeGuid == Guid.Empty || revision == 0)
        {
            throw new ArgumentException(
                "A vegetation preview requires a biome identity and non-zero revision.",
                nameof(biomeGuid));
        }

        BiomeGuid = biomeGuid;
        Revision = revision;
        State = state;
        ClusterCount = clusterCount;
        InstanceCount = instanceCount;
        Diagnostic = diagnostic?.Trim()
            ?? throw new ArgumentNullException(nameof(diagnostic));
    }

    public Guid BiomeGuid { get; }
    public ulong Revision { get; }
    public VegetationAuthoringPreviewState State { get; }
    public int ClusterCount { get; }
    public int InstanceCount { get; }
    public string Diagnostic { get; }
    public bool HasPreview => State != VegetationAuthoringPreviewState.None;
}

public interface IVegetationAuthoringPreviewService
{
    VegetationAuthoringPreviewSnapshot GetSnapshot();

    void Publish(VegetationAuthoringPreviewSnapshot snapshot);

    void Clear();
}

internal sealed class VegetationAuthoringPreviewService :
    IVegetationAuthoringPreviewService
{
    private VegetationAuthoringPreviewSnapshot m_Snapshot =
        VegetationAuthoringPreviewSnapshot.Empty;

    public VegetationAuthoringPreviewSnapshot GetSnapshot() =>
        Volatile.Read(ref m_Snapshot);

    public void Publish(VegetationAuthoringPreviewSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref m_Snapshot, snapshot);
    }

    public void Clear() =>
        Interlocked.Exchange(ref m_Snapshot, VegetationAuthoringPreviewSnapshot.Empty);
}

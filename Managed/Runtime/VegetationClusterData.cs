namespace ArisenEngine.Vegetation;

public sealed class VegetationClusterDataSnapshot
{
    public static VegetationClusterDataSnapshot Empty { get; } = new(0, 0, 0);

    public VegetationClusterDataSnapshot(
        ulong revision,
        int clusterCount,
        int instanceCount)
    {
        if (clusterCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(clusterCount));
        }

        if (instanceCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(instanceCount));
        }

        if (revision == 0 && (clusterCount != 0 || instanceCount != 0))
        {
            throw new ArgumentException(
                "A non-empty vegetation snapshot must have a non-zero revision.",
                nameof(revision));
        }

        Revision = revision;
        ClusterCount = clusterCount;
        InstanceCount = instanceCount;
    }

    public ulong Revision { get; }

    public int ClusterCount { get; }

    public int InstanceCount { get; }

    public bool IsEmpty => ClusterCount == 0 && InstanceCount == 0;
}

public interface IVegetationClusterDataSource
{
    VegetationClusterDataSnapshot GetSnapshot();
}

internal sealed class VegetationRuntimeDataStore : IVegetationClusterDataSource
{
    private VegetationClusterDataSnapshot m_Snapshot = VegetationClusterDataSnapshot.Empty;

    public VegetationClusterDataSnapshot GetSnapshot() => Volatile.Read(ref m_Snapshot);

    internal void Publish(VegetationClusterDataSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref m_Snapshot, snapshot);
    }

    internal void Clear() =>
        Interlocked.Exchange(ref m_Snapshot, VegetationClusterDataSnapshot.Empty);
}

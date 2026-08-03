namespace ArisenEngine.Vegetation;

public sealed class VegetationDiagnosticsSnapshot
{
    public static VegetationDiagnosticsSnapshot Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0);

    public VegetationDiagnosticsSnapshot(
        uint frameIndex,
        ulong runtimeRevision,
        int residentClusterCount,
        int residentInstanceCount,
        int visibleClusterCount,
        int visibleInstanceCount,
        int drawBatchCount,
        long cpuBytes,
        long preparedBytes)
    {
        if (residentClusterCount < 0 ||
            residentInstanceCount < 0 ||
            visibleClusterCount < 0 ||
            visibleInstanceCount < 0 ||
            drawBatchCount < 0 ||
            cpuBytes < 0 ||
            preparedBytes < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(residentClusterCount),
                "Vegetation diagnostic counters cannot be negative.");
        }

        if (visibleClusterCount > residentClusterCount ||
            visibleInstanceCount > residentInstanceCount)
        {
            throw new ArgumentException(
                "Visible vegetation counts cannot exceed resident counts.");
        }

        FrameIndex = frameIndex;
        RuntimeRevision = runtimeRevision;
        ResidentClusterCount = residentClusterCount;
        ResidentInstanceCount = residentInstanceCount;
        VisibleClusterCount = visibleClusterCount;
        VisibleInstanceCount = visibleInstanceCount;
        DrawBatchCount = drawBatchCount;
        CpuBytes = cpuBytes;
        PreparedBytes = preparedBytes;
    }

    public uint FrameIndex { get; }
    public ulong RuntimeRevision { get; }
    public int ResidentClusterCount { get; }
    public int ResidentInstanceCount { get; }
    public int VisibleClusterCount { get; }
    public int VisibleInstanceCount { get; }
    public int DrawBatchCount { get; }
    public long CpuBytes { get; }
    public long PreparedBytes { get; }
}

public interface IVegetationDiagnostics
{
    VegetationDiagnosticsSnapshot GetSnapshot();
}

public interface IVegetationDiagnosticsPublisher
{
    void Publish(VegetationDiagnosticsSnapshot snapshot);

    void Clear();
}

internal sealed class VegetationDiagnosticsService :
    IVegetationDiagnostics,
    IVegetationDiagnosticsPublisher
{
    private VegetationDiagnosticsSnapshot m_Snapshot = VegetationDiagnosticsSnapshot.Empty;

    public VegetationDiagnosticsSnapshot GetSnapshot() => Volatile.Read(ref m_Snapshot);

    public void Publish(VegetationDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref m_Snapshot, snapshot);
    }

    public void Clear() =>
        Interlocked.Exchange(ref m_Snapshot, VegetationDiagnosticsSnapshot.Empty);
}

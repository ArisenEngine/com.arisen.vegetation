using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Vegetation;

public enum VegetationQueryStatus
{
    InvalidRequest = 0,
    OutsideCoverage = 1,
    Unavailable = 2,
    Available = 3
}

public readonly record struct VegetationQueryRequest(
    WorldPosition Center,
    double Radius,
    int MaximumResults)
{
    public bool IsValid =>
        Center.IsFinite &&
        double.IsFinite(Radius) &&
        Radius >= 0.0 &&
        MaximumResults >= 0;
}

public readonly record struct VegetationInstanceQueryResult(
    Guid InstanceGuid,
    Guid ClusterGuid,
    Guid SpeciesGuid,
    ulong ClusterGeneration,
    WorldPosition Position,
    double DistanceSquared);

public interface IVegetationQueryService
{
    VegetationQueryStatus QueryNearby(
        in VegetationQueryRequest request,
        Span<VegetationInstanceQueryResult> results,
        out int resultCount);
}

internal sealed class VegetationQueryService : IVegetationQueryService
{
    private readonly VegetationRuntimeDataStore m_RuntimeData;

    public VegetationQueryService(VegetationRuntimeDataStore runtimeData)
    {
        m_RuntimeData = runtimeData ?? throw new ArgumentNullException(nameof(runtimeData));
    }

    public VegetationQueryStatus QueryNearby(
        in VegetationQueryRequest request,
        Span<VegetationInstanceQueryResult> results,
        out int resultCount)
    {
        resultCount = 0;
        if (!request.IsValid || request.MaximumResults > results.Length)
        {
            return VegetationQueryStatus.InvalidRequest;
        }

        return m_RuntimeData.GetSnapshot().IsEmpty
            ? VegetationQueryStatus.Unavailable
            : VegetationQueryStatus.OutsideCoverage;
    }
}

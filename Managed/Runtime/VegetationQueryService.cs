using ArisenEngine.Core.ECS;
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
    private readonly Func<EntityManager?> m_EntityManagerProvider;

    public VegetationQueryService(VegetationRuntimeDataStore runtimeData)
        : this(runtimeData, static () => null)
    {
    }

    internal VegetationQueryService(
        VegetationRuntimeDataStore runtimeData,
        Func<EntityManager?> entityManagerProvider)
    {
        m_RuntimeData = runtimeData ?? throw new ArgumentNullException(nameof(runtimeData));
        m_EntityManagerProvider = entityManagerProvider
            ?? throw new ArgumentNullException(nameof(entityManagerProvider));
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

        VegetationClusterDataSnapshot snapshot = m_RuntimeData.GetSnapshot();
        ReadOnlySpan<VegetationResidentClusterData> clusters = snapshot.Clusters;
        if (clusters.IsEmpty)
        {
            return VegetationQueryStatus.Unavailable;
        }

        EntityManager? entityManager = m_EntityManagerProvider();
        if (entityManager == null || !entityManager.HasPool<VegetationClusterComponent>())
        {
            return VegetationQueryStatus.Unavailable;
        }

        ComponentPool<VegetationClusterComponent> pool =
            entityManager.GetPool<VegetationClusterComponent>();
        VegetationClusterComponent[] components = pool.GetRawComponentArray();
        bool hasActiveCluster = false;
        bool intersectsCoverage = false;
        double radiusSquared = request.Radius * request.Radius;
        for (int componentIndex = 0; componentIndex < pool.Count; componentIndex++)
        {
            ref readonly VegetationClusterComponent component = ref components[componentIndex];
            int clusterIndex = FindCluster(clusters, component.ClusterGuid);
            if (clusterIndex < 0)
            {
                continue;
            }

            VegetationResidentClusterData cluster = clusters[clusterIndex];
            if (!MatchesActiveComponent(component, cluster))
            {
                continue;
            }

            hasActiveCluster = true;
            if (!IntersectsSphere(cluster.Bounds, request.Center, request.Radius, radiusSquared))
            {
                continue;
            }

            intersectsCoverage = true;
            if (request.MaximumResults == 0)
            {
                return VegetationQueryStatus.Available;
            }

            ReadOnlySpan<VegetationResidentPageData> pages = cluster.Pages;
            for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
            {
                VegetationResidentPageData page = pages[pageIndex];
                if (!IntersectsSphere(page.Bounds, request.Center, request.Radius, radiusSquared))
                {
                    continue;
                }

                ReadOnlySpan<VegetationResidentInstance> instances = page.Instances;
                for (int instanceIndex = 0; instanceIndex < instances.Length; instanceIndex++)
                {
                    ref readonly VegetationResidentInstance instance = ref instances[instanceIndex];
                    var position = new WorldPosition(
                        page.Origin.X + instance.LocalPosition.X,
                        page.Origin.Y + instance.LocalPosition.Y,
                        page.Origin.Z + instance.LocalPosition.Z);
                    double deltaX = position.X - request.Center.X;
                    double deltaY = position.Y - request.Center.Y;
                    double deltaZ = position.Z - request.Center.Z;
                    if (Math.Abs(deltaX) > request.Radius ||
                        Math.Abs(deltaY) > request.Radius ||
                        Math.Abs(deltaZ) > request.Radius)
                    {
                        continue;
                    }

                    double distanceSquared =
                        (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ);
                    if (distanceSquared > radiusSquared)
                    {
                        continue;
                    }

                    InsertNearest(
                        new VegetationInstanceQueryResult(
                            instance.InstanceGuid,
                            cluster.Guid,
                            instance.SpeciesGuid,
                            cluster.Generation,
                            position,
                            distanceSquared),
                        results,
                        request.MaximumResults,
                        ref resultCount);
                }
            }
        }

        if (!hasActiveCluster)
        {
            return VegetationQueryStatus.Unavailable;
        }

        return intersectsCoverage
            ? VegetationQueryStatus.Available
            : VegetationQueryStatus.OutsideCoverage;
    }

    private static bool MatchesActiveComponent(
        in VegetationClusterComponent component,
        VegetationResidentClusterData cluster)
    {
        return (component.Flags & VegetationClusterFlags.Visible) != 0 &&
            component.ClusterGuid == cluster.Guid &&
            component.BiomeGuid == cluster.BiomeGuid &&
            component.WorldGuid != Guid.Empty &&
            component.OwningCellGuid != Guid.Empty &&
            cluster.ContainsSpecies(component.SpeciesGuid) &&
            component.OriginX == cluster.Origin.X &&
            component.OriginY == cluster.Origin.Y &&
            component.OriginZ == cluster.Origin.Z &&
            component.BoundsMinX == cluster.Bounds.Min.X &&
            component.BoundsMinY == cluster.Bounds.Min.Y &&
            component.BoundsMinZ == cluster.Bounds.Min.Z &&
            component.BoundsMaxX == cluster.Bounds.Max.X &&
            component.BoundsMaxY == cluster.Bounds.Max.Y &&
            component.BoundsMaxZ == cluster.Bounds.Max.Z &&
            component.PageCount == cluster.PageCount &&
            component.InstanceCount == cluster.InstanceCount;
    }

    private static int FindCluster(
        ReadOnlySpan<VegetationResidentClusterData> clusters,
        Guid clusterGuid)
    {
        int lower = 0;
        int upper = clusters.Length - 1;
        while (lower <= upper)
        {
            int middle = lower + ((upper - lower) >> 1);
            int comparison = clusters[middle].Guid.CompareTo(clusterGuid);
            if (comparison == 0)
            {
                return middle;
            }

            if (comparison < 0)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }

        return -1;
    }

    private static bool IntersectsSphere(
        in WorldBounds bounds,
        in WorldPosition center,
        double radius,
        double radiusSquared)
    {
        double deltaX = AxisDistance(center.X, bounds.Min.X, bounds.Max.X);
        double deltaY = AxisDistance(center.Y, bounds.Min.Y, bounds.Max.Y);
        double deltaZ = AxisDistance(center.Z, bounds.Min.Z, bounds.Max.Z);
        if (deltaX > radius || deltaY > radius || deltaZ > radius)
        {
            return false;
        }

        return (deltaX * deltaX) + (deltaY * deltaY) + (deltaZ * deltaZ) <=
            radiusSquared;
    }

    private static double AxisDistance(double value, double minimum, double maximum) =>
        value < minimum
            ? minimum - value
            : value > maximum
                ? value - maximum
                : 0.0;

    private static void InsertNearest(
        in VegetationInstanceQueryResult candidate,
        Span<VegetationInstanceQueryResult> results,
        int maximumResults,
        ref int resultCount)
    {
        if (maximumResults == 0)
        {
            return;
        }

        int insertionIndex;
        if (resultCount < maximumResults)
        {
            insertionIndex = resultCount;
            resultCount++;
        }
        else
        {
            if (Compare(candidate, results[maximumResults - 1]) >= 0)
            {
                return;
            }

            insertionIndex = maximumResults - 1;
        }

        while (insertionIndex > 0 && Compare(candidate, results[insertionIndex - 1]) < 0)
        {
            results[insertionIndex] = results[insertionIndex - 1];
            insertionIndex--;
        }

        results[insertionIndex] = candidate;
    }

    private static int Compare(
        in VegetationInstanceQueryResult left,
        in VegetationInstanceQueryResult right)
    {
        int result = left.DistanceSquared.CompareTo(right.DistanceSquared);
        if (result != 0)
        {
            return result;
        }

        result = left.InstanceGuid.CompareTo(right.InstanceGuid);
        if (result != 0)
        {
            return result;
        }

        result = left.ClusterGuid.CompareTo(right.ClusterGuid);
        if (result != 0)
        {
            return result;
        }

        return left.SpeciesGuid.CompareTo(right.SpeciesGuid);
    }
}

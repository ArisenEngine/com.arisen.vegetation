using ArisenEngine.Core.ECS;

namespace ArisenEngine.Vegetation;

public interface IVegetationClusterRenderSource
{
    ReadOnlySpan<VegetationClusterComponent> ExtractVisibleClusters();
}

public sealed class VegetationClusterRenderSource : IVegetationClusterRenderSource
{
    private readonly Func<EntityManager?> m_EntityManagerProvider;
    private VegetationClusterComponent[] m_VisibleClusters =
        Array.Empty<VegetationClusterComponent>();
    private int m_VisibleClusterCount;

    public VegetationClusterRenderSource(Func<EntityManager?> entityManagerProvider)
    {
        m_EntityManagerProvider = entityManagerProvider
            ?? throw new ArgumentNullException(nameof(entityManagerProvider));
    }

    public ReadOnlySpan<VegetationClusterComponent> ExtractVisibleClusters()
    {
        EntityManager? entityManager = m_EntityManagerProvider();
        if (entityManager == null || !entityManager.HasPool<VegetationClusterComponent>())
        {
            m_VisibleClusterCount = 0;
            return ReadOnlySpan<VegetationClusterComponent>.Empty;
        }

        ComponentPool<VegetationClusterComponent> pool =
            entityManager.GetPool<VegetationClusterComponent>();
        EnsureCapacity(pool.Count);

        VegetationClusterComponent[] components = pool.GetRawComponentArray();
        int visibleCount = 0;
        for (int index = 0; index < pool.Count; index++)
        {
            VegetationClusterComponent component = components[index];
            if ((component.Flags & VegetationClusterFlags.Visible) == 0)
            {
                continue;
            }

            m_VisibleClusters[visibleCount++] = component;
        }

        if (visibleCount > 1)
        {
            SortVisibleClusters(visibleCount);
        }

        m_VisibleClusterCount = visibleCount;
        return new ReadOnlySpan<VegetationClusterComponent>(
            m_VisibleClusters,
            0,
            m_VisibleClusterCount);
    }

    private void EnsureCapacity(int required)
    {
        if (required <= m_VisibleClusters.Length)
        {
            return;
        }

        int capacity = Math.Max(4, m_VisibleClusters.Length);
        while (capacity < required)
        {
            capacity = checked(capacity * 2);
        }

        Array.Resize(ref m_VisibleClusters, capacity);
    }

    private void SortVisibleClusters(int count)
    {
        for (int root = (count >> 1) - 1; root >= 0; root--)
        {
            SiftDown(root, count);
        }

        for (int end = count - 1; end > 0; end--)
        {
            (m_VisibleClusters[0], m_VisibleClusters[end]) =
                (m_VisibleClusters[end], m_VisibleClusters[0]);
            SiftDown(0, end);
        }
    }

    private void SiftDown(int root, int count)
    {
        while (true)
        {
            int child = checked((root << 1) + 1);
            if (child >= count)
            {
                return;
            }

            int right = child + 1;
            if (right < count &&
                CompareRenderOrder(
                    in m_VisibleClusters[child],
                    in m_VisibleClusters[right]) < 0)
            {
                child = right;
            }

            if (CompareRenderOrder(
                    in m_VisibleClusters[root],
                    in m_VisibleClusters[child]) >= 0)
            {
                return;
            }

            (m_VisibleClusters[root], m_VisibleClusters[child]) =
                (m_VisibleClusters[child], m_VisibleClusters[root]);
            root = child;
        }
    }

    private static int CompareRenderOrder(
        in VegetationClusterComponent left,
        in VegetationClusterComponent right)
    {
        int result = left.BiomeGuid.CompareTo(right.BiomeGuid);
        if (result != 0)
        {
            return result;
        }

        result = left.WorldGuid.CompareTo(right.WorldGuid);
        if (result != 0)
        {
            return result;
        }

        result = left.CellX.CompareTo(right.CellX);
        if (result != 0)
        {
            return result;
        }

        result = left.CellY.CompareTo(right.CellY);
        if (result != 0)
        {
            return result;
        }

        result = left.CellZ.CompareTo(right.CellZ);
        if (result != 0)
        {
            return result;
        }

        result = left.OwningCellGuid.CompareTo(right.OwningCellGuid);
        if (result != 0)
        {
            return result;
        }

        result = left.SpeciesGuid.CompareTo(right.SpeciesGuid);
        return result != 0
            ? result
            : left.ClusterGuid.CompareTo(right.ClusterGuid);
    }
}

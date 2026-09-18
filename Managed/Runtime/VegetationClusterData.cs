using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Vegetation.Assets;

namespace ArisenEngine.Vegetation;

public enum VegetationResidentResourceKind
{
    Cluster = 1,
    Page = 2
}

public readonly record struct VegetationResidentResourceHandle(
    VegetationResidentResourceKind Kind,
    Guid Guid,
    ulong Generation)
{
    public bool IsValid => Guid != Guid.Empty && Generation != 0;
}

public readonly record struct VegetationRuntimeDataMetrics(
    int ResidentClusterCount,
    int ResidentPageCount,
    int CompleteClusterCount,
    int CompletePageCount,
    int InstanceCount);

public sealed class VegetationClusterDataSnapshot
{
    private readonly VegetationResidentClusterData[] m_Clusters;

    public static VegetationClusterDataSnapshot Empty { get; } = new(
        revision: 0,
        Array.Empty<VegetationResidentClusterData>());

    private VegetationClusterDataSnapshot(
        ulong revision,
        VegetationResidentClusterData[] clusters)
    {
        if (revision == 0 && clusters.Length != 0)
        {
            throw new ArgumentException(
                "A non-empty vegetation snapshot must have a non-zero revision.",
                nameof(revision));
        }

        int pageCount = 0;
        int instanceCount = 0;
        for (int index = 0; index < clusters.Length; index++)
        {
            pageCount = checked(pageCount + clusters[index].PageCount);
            instanceCount = checked(instanceCount + clusters[index].InstanceCount);
        }

        Revision = revision;
        ClusterCount = clusters.Length;
        PageCount = pageCount;
        InstanceCount = instanceCount;
        m_Clusters = clusters;
    }

    public ulong Revision { get; }

    public int ClusterCount { get; }

    public int PageCount { get; }

    public int InstanceCount { get; }

    public bool IsEmpty => m_Clusters.Length == 0;

    public ReadOnlySpan<VegetationResidentClusterData> Clusters => m_Clusters;

    internal static VegetationClusterDataSnapshot Create(
        ulong revision,
        VegetationResidentClusterData[] clusters) =>
        new(revision, clusters);
}

public sealed class VegetationPreparedClusterData
{
    internal VegetationPreparedClusterData(VegetationResidentClusterRecord cluster)
    {
        Cluster = cluster;
    }

    internal VegetationResidentClusterRecord Cluster { get; }
}

public sealed class VegetationPreparedPageData
{
    internal VegetationPreparedPageData(VegetationResidentPageData page)
    {
        Page = page;
    }

    internal VegetationResidentPageData Page { get; }
}

public interface IVegetationClusterDataSource
{
    VegetationClusterDataSnapshot GetSnapshot();
}

public interface IVegetationRuntimeDataStore : IVegetationClusterDataSource
{
    VegetationPreparedClusterData PrepareCluster(CookedVegetationCluster cluster);

    VegetationPreparedPageData PreparePage(
        CookedVegetationInstancePage page,
        VegetationInstancePagePayloadIdentity payloadIdentity);

    VegetationResidentResourceHandle PublishCluster(VegetationPreparedClusterData cluster);

    VegetationResidentResourceHandle PublishPage(VegetationPreparedPageData page);

    bool Remove(VegetationResidentResourceHandle handle);

    VegetationRuntimeDataMetrics GetMetrics();
}

internal sealed class VegetationRuntimeDataStore : IVegetationRuntimeDataStore
{
    private readonly object m_Gate = new();
    private Dictionary<Guid, VegetationResidentClusterRecord> m_Clusters = new();
    private Dictionary<Guid, VegetationResidentPageData> m_Pages = new();
    private VegetationClusterDataSnapshot m_Snapshot = VegetationClusterDataSnapshot.Empty;
    private ulong m_NextGeneration;
    private ulong m_NextRevision;

    public VegetationClusterDataSnapshot GetSnapshot() => Volatile.Read(ref m_Snapshot);

    public VegetationPreparedClusterData PrepareCluster(CookedVegetationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return new VegetationPreparedClusterData(
            VegetationResidentClusterRecord.Create(cluster));
    }

    public VegetationPreparedPageData PreparePage(
        CookedVegetationInstancePage page,
        VegetationInstancePagePayloadIdentity payloadIdentity)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new VegetationPreparedPageData(
            VegetationResidentPageData.Create(page, payloadIdentity));
    }

    internal VegetationResidentResourceHandle PublishCluster(CookedVegetationCluster cluster) =>
        PublishCluster(PrepareCluster(cluster));

    public VegetationResidentResourceHandle PublishCluster(
        VegetationPreparedClusterData cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        lock (m_Gate)
        {
            ulong generation = NextGeneration();
            VegetationResidentClusterRecord prepared = cluster.Cluster.WithGeneration(generation);
            var candidateClusters = new Dictionary<Guid, VegetationResidentClusterRecord>(m_Clusters)
            {
                [prepared.Guid] = prepared
            };
            VegetationClusterDataSnapshot candidate = BuildSnapshot(
                candidateClusters,
                m_Pages,
                NextRevision());

            m_Clusters = candidateClusters;
            Volatile.Write(ref m_Snapshot, candidate);
            return new VegetationResidentResourceHandle(
                VegetationResidentResourceKind.Cluster,
                prepared.Guid,
                prepared.Generation);
        }
    }

    internal VegetationResidentResourceHandle PublishPage(CookedVegetationInstancePage page)
    {
        byte[] payload = VegetationInstancePageAssetCooker.WritePayload(page);
        var payloadIdentity = new VegetationInstancePagePayloadIdentity(
            page.Guid,
            payload.LongLength,
            SHA256.HashData(payload));
        return PublishPage(PreparePage(page, payloadIdentity));
    }

    public VegetationResidentResourceHandle PublishPage(VegetationPreparedPageData page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (m_Gate)
        {
            ulong generation = NextGeneration();
            VegetationResidentPageData prepared = page.Page.WithGeneration(generation);
            var candidatePages = new Dictionary<Guid, VegetationResidentPageData>(m_Pages)
            {
                [prepared.Guid] = prepared
            };
            VegetationClusterDataSnapshot candidate = BuildSnapshot(
                m_Clusters,
                candidatePages,
                NextRevision());

            m_Pages = candidatePages;
            Volatile.Write(ref m_Snapshot, candidate);
            return new VegetationResidentResourceHandle(
                VegetationResidentResourceKind.Page,
                prepared.Guid,
                prepared.Generation);
        }
    }

    public bool Remove(VegetationResidentResourceHandle handle)
    {
        if (!handle.IsValid)
        {
            return false;
        }

        lock (m_Gate)
        {
            switch (handle.Kind)
            {
                case VegetationResidentResourceKind.Cluster:
                    if (!m_Clusters.TryGetValue(
                            handle.Guid,
                            out VegetationResidentClusterRecord? cluster) ||
                        cluster.Generation != handle.Generation)
                    {
                        return false;
                    }

                    var candidateClusters =
                        new Dictionary<Guid, VegetationResidentClusterRecord>(m_Clusters);
                    candidateClusters.Remove(handle.Guid);
                    VegetationClusterDataSnapshot clusterSnapshot = BuildSnapshot(
                        candidateClusters,
                        m_Pages,
                        NextRevision());
                    m_Clusters = candidateClusters;
                    Volatile.Write(ref m_Snapshot, clusterSnapshot);
                    return true;

                case VegetationResidentResourceKind.Page:
                    if (!m_Pages.TryGetValue(handle.Guid, out VegetationResidentPageData? page) ||
                        page.Generation != handle.Generation)
                    {
                        return false;
                    }

                    var candidatePages = new Dictionary<Guid, VegetationResidentPageData>(m_Pages);
                    candidatePages.Remove(handle.Guid);
                    VegetationClusterDataSnapshot pageSnapshot = BuildSnapshot(
                        m_Clusters,
                        candidatePages,
                        NextRevision());
                    m_Pages = candidatePages;
                    Volatile.Write(ref m_Snapshot, pageSnapshot);
                    return true;

                default:
                    return false;
            }
        }
    }

    public VegetationRuntimeDataMetrics GetMetrics()
    {
        lock (m_Gate)
        {
            VegetationClusterDataSnapshot snapshot = m_Snapshot;
            return new VegetationRuntimeDataMetrics(
                m_Clusters.Count,
                m_Pages.Count,
                snapshot.ClusterCount,
                snapshot.PageCount,
                snapshot.InstanceCount);
        }
    }

    internal void Clear()
    {
        lock (m_Gate)
        {
            m_Clusters.Clear();
            m_Pages.Clear();
            Volatile.Write(ref m_Snapshot, VegetationClusterDataSnapshot.Empty);
        }
    }

    private ulong NextGeneration()
    {
        m_NextGeneration++;
        if (m_NextGeneration == 0)
        {
            m_NextGeneration++;
        }

        return m_NextGeneration;
    }

    private ulong NextRevision()
    {
        m_NextRevision++;
        if (m_NextRevision == 0)
        {
            m_NextRevision++;
        }

        return m_NextRevision;
    }

    private static VegetationClusterDataSnapshot BuildSnapshot(
        IReadOnlyDictionary<Guid, VegetationResidentClusterRecord> clusters,
        IReadOnlyDictionary<Guid, VegetationResidentPageData> pages,
        ulong revision)
    {
        VegetationResidentClusterRecord[] orderedClusters = clusters.Values.ToArray();
        Array.Sort(
            orderedClusters,
            static (left, right) => left.Guid.CompareTo(right.Guid));

        var complete = new List<VegetationResidentClusterData>(orderedClusters.Length);
        for (int clusterIndex = 0; clusterIndex < orderedClusters.Length; clusterIndex++)
        {
            VegetationResidentClusterRecord cluster = orderedClusters[clusterIndex];
            var clusterPages = new VegetationResidentPageData[cluster.PageReferences.Length];
            bool missingPage = false;
            for (int pageIndex = 0; pageIndex < cluster.PageReferences.Length; pageIndex++)
            {
                VegetationResidentPageReference reference = cluster.PageReferences[pageIndex];
                if (!pages.TryGetValue(reference.Guid, out VegetationResidentPageData? page))
                {
                    missingPage = true;
                    break;
                }

                ValidatePageForCluster(cluster, reference, page);
                clusterPages[pageIndex] = page;
            }

            if (missingPage)
            {
                continue;
            }

            ValidateCompleteCluster(cluster, clusterPages);
            complete.Add(new VegetationResidentClusterData(cluster, clusterPages));
        }

        return VegetationClusterDataSnapshot.Create(
            revision,
            complete.ToArray());
    }

    private static void ValidatePageForCluster(
        VegetationResidentClusterRecord cluster,
        in VegetationResidentPageReference reference,
        VegetationResidentPageData page)
    {
        if (page.ClusterGuid != cluster.Guid ||
            !string.Equals(page.PackageId, reference.PackageId, StringComparison.Ordinal) ||
            page.Instances.Length != reference.InstanceCount ||
            page.Origin != reference.Origin ||
            page.Bounds != reference.Bounds ||
            page.SizeInBytes != reference.SizeInBytes ||
            !page.ContentHash.SequenceEqual(reference.ContentHash))
        {
            throw new InvalidOperationException(
                $"Vegetation page '{page.Guid:D}' does not match cluster '{cluster.Guid:D}' metadata.");
        }

        for (int speciesIndex = 0; speciesIndex < page.Species.Length; speciesIndex++)
        {
            if (Array.BinarySearch(cluster.Species, page.Species[speciesIndex]) < 0)
            {
                throw new InvalidOperationException(
                    $"Vegetation page '{page.Guid:D}' references species " +
                    $"'{page.Species[speciesIndex].Guid:D}' outside cluster '{cluster.Guid:D}'.");
            }
        }
    }

    private static void ValidateCompleteCluster(
        VegetationResidentClusterRecord cluster,
        VegetationResidentPageData[] pages)
    {
        int instanceCount = 0;
        WorldPosition origin = pages[0].Origin;
        var usedSpecies = new HashSet<VegetationResidentSpecies>();
        for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            VegetationResidentPageData page = pages[pageIndex];
            if (page.Origin != origin)
            {
                throw new InvalidOperationException(
                    $"Vegetation cluster '{cluster.Guid:D}' pages do not share one cell origin.");
            }

            instanceCount = checked(instanceCount + page.Instances.Length);
            for (int speciesIndex = 0; speciesIndex < page.Species.Length; speciesIndex++)
            {
                usedSpecies.Add(page.Species[speciesIndex]);
            }
        }

        // Worker-side cluster loading validates cross-page keys; exact page hashes above bind
        // this publication to that validated closure without rescanning every instance here.
        if (instanceCount != cluster.InstanceCount ||
            usedSpecies.Count != cluster.Species.Length ||
            cluster.Species.Any(species => !usedSpecies.Contains(species)))
        {
            throw new InvalidOperationException(
                $"Vegetation cluster '{cluster.Guid:D}' resident page closure is incomplete.");
        }
    }
}

public readonly struct VegetationResidentSpecies :
    IComparable<VegetationResidentSpecies>,
    IEquatable<VegetationResidentSpecies>
{
    internal VegetationResidentSpecies(Guid guid, string packageId)
    {
        Guid = guid;
        PackageId = packageId;
    }

    public Guid Guid { get; }

    public string PackageId { get; }

    public int CompareTo(VegetationResidentSpecies other)
    {
        int result = StringComparer.Ordinal.Compare(PackageId, other.PackageId);
        return result != 0
            ? result
            : Guid.CompareTo(other.Guid);
    }

    public bool Equals(VegetationResidentSpecies other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) =>
        obj is VegetationResidentSpecies other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(
        Guid,
        StringComparer.Ordinal.GetHashCode(PackageId ?? string.Empty));

    public static bool operator ==(
        VegetationResidentSpecies left,
        VegetationResidentSpecies right) => left.Equals(right);

    public static bool operator !=(
        VegetationResidentSpecies left,
        VegetationResidentSpecies right) => !left.Equals(right);
}

internal readonly record struct VegetationResidentPageReference(
    Guid Guid,
    string PackageId,
    int InstanceCount,
    WorldPosition Origin,
    WorldBounds Bounds,
    long SizeInBytes,
    byte[] ContentHash);

internal sealed class VegetationResidentClusterRecord
{
    private VegetationResidentClusterRecord(
        Guid guid,
        string packageId,
        Guid biomeGuid,
        WorldBounds bounds,
        VegetationResidentSpecies[] species,
        VegetationResidentPageReference[] pageReferences,
        int instanceCount,
        ulong generation)
    {
        Guid = guid;
        PackageId = packageId;
        BiomeGuid = biomeGuid;
        Bounds = bounds;
        Species = species;
        PageReferences = pageReferences;
        InstanceCount = instanceCount;
        Generation = generation;
    }

    public Guid Guid { get; }

    public string PackageId { get; }

    public Guid BiomeGuid { get; }

    public WorldBounds Bounds { get; }

    public VegetationResidentSpecies[] Species { get; }

    public VegetationResidentPageReference[] PageReferences { get; }

    public int InstanceCount { get; }

    public ulong Generation { get; }

    public static VegetationResidentClusterRecord Create(
        CookedVegetationCluster cluster)
    {
        if (cluster.Guid == Guid.Empty ||
            string.IsNullOrWhiteSpace(cluster.PackageId) ||
            cluster.GeneratedSchemaVersion <= 0 ||
            cluster.Biome.Guid == Guid.Empty ||
            string.IsNullOrWhiteSpace(cluster.Biome.PackageId) ||
            !cluster.Bounds.IsValid ||
            cluster.Species is not { Count: > 0 } ||
            cluster.Pages is not { Count: > 0 } ||
            cluster.InstanceCount <= 0)
        {
            throw new InvalidDataException("Vegetation cluster runtime data is invalid.");
        }

        VegetationResidentSpecies[] species = cluster.Species
            .Select(reference => new VegetationResidentSpecies(reference.Guid, reference.PackageId))
            .ToArray();
        Array.Sort(species);
        for (int index = 0; index < species.Length; index++)
        {
            if (species[index].Guid == Guid.Empty ||
                string.IsNullOrWhiteSpace(species[index].PackageId) ||
                (index > 0 && species[index - 1].CompareTo(species[index]) == 0))
            {
                throw new InvalidDataException(
                    $"Vegetation cluster '{cluster.Guid:D}' species metadata is invalid.");
            }
        }

        var pages = new VegetationResidentPageReference[cluster.Pages.Count];
        int instanceCount = 0;
        for (int index = 0; index < pages.Length; index++)
        {
            CookedVegetationInstancePageReference reference = cluster.Pages[index]
                ?? throw new InvalidDataException(
                    $"Vegetation cluster '{cluster.Guid:D}' has a null page reference.");
            if (reference.Guid == Guid.Empty ||
                string.IsNullOrWhiteSpace(reference.PackageId) ||
                reference.InstanceCount <= 0 ||
                !reference.Origin.IsFinite ||
                !reference.Bounds.IsValid ||
                reference.SizeInBytes <= 0 ||
                reference.ContentHash is not { Length: VegetationCookedContainer.HashSize })
            {
                throw new InvalidDataException(
                    $"Vegetation cluster '{cluster.Guid:D}' page metadata is invalid.");
            }

            pages[index] = new VegetationResidentPageReference(
                reference.Guid,
                reference.PackageId,
                reference.InstanceCount,
                reference.Origin,
                reference.Bounds,
                reference.SizeInBytes,
                reference.ContentHash.ToArray());
            instanceCount = checked(instanceCount + reference.InstanceCount);
        }

        Array.Sort(pages, static (left, right) => left.Guid.CompareTo(right.Guid));
        for (int index = 1; index < pages.Length; index++)
        {
            if (pages[index - 1].Guid == pages[index].Guid)
            {
                throw new InvalidDataException(
                    $"Vegetation cluster '{cluster.Guid:D}' repeats page '{pages[index].Guid:D}'.");
            }
        }

        if (instanceCount != cluster.InstanceCount)
        {
            throw new InvalidDataException(
                $"Vegetation cluster '{cluster.Guid:D}' instance count is inconsistent.");
        }

        return new VegetationResidentClusterRecord(
            cluster.Guid,
            cluster.PackageId,
            cluster.Biome.Guid,
            cluster.Bounds,
            species,
            pages,
            cluster.InstanceCount,
            generation: 0);
    }

    public VegetationResidentClusterRecord WithGeneration(ulong generation)
    {
        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return new VegetationResidentClusterRecord(
            Guid,
            PackageId,
            BiomeGuid,
            Bounds,
            Species,
            PageReferences,
            InstanceCount,
            generation);
    }
}

public sealed class VegetationResidentClusterData
{
    private readonly VegetationResidentSpecies[] m_Species;
    private readonly VegetationResidentPageData[] m_Pages;

    internal VegetationResidentClusterData(
        VegetationResidentClusterRecord cluster,
        VegetationResidentPageData[] pages)
    {
        Guid = cluster.Guid;
        BiomeGuid = cluster.BiomeGuid;
        Bounds = cluster.Bounds;
        m_Species = cluster.Species;
        m_Pages = pages;
        InstanceCount = cluster.InstanceCount;
        Generation = cluster.Generation;
        Origin = pages[0].Origin;
    }

    public Guid Guid { get; }

    public Guid BiomeGuid { get; }

    public WorldPosition Origin { get; }

    public WorldBounds Bounds { get; }

    public ReadOnlySpan<VegetationResidentSpecies> Species => m_Species;

    public ReadOnlySpan<VegetationResidentPageData> Pages => m_Pages;

    public int PageCount => m_Pages.Length;

    public int InstanceCount { get; }

    public ulong Generation { get; }

    public bool ContainsSpecies(Guid speciesGuid)
    {
        for (int index = 0; index < m_Species.Length; index++)
        {
            if (m_Species[index].Guid == speciesGuid)
            {
                return true;
            }
        }

        return false;
    }
}

public readonly struct VegetationResidentInstance
{
    internal VegetationResidentInstance(
        Guid instanceGuid,
        ulong stableKey,
        Guid speciesGuid,
        Vector3 localPosition,
        Quaternion orientation,
        float uniformScale,
        float conservativeRadius)
    {
        InstanceGuid = instanceGuid;
        StableKey = stableKey;
        SpeciesGuid = speciesGuid;
        LocalPosition = localPosition;
        Orientation = orientation;
        UniformScale = uniformScale;
        ConservativeRadius = conservativeRadius;
    }

    public Guid InstanceGuid { get; }

    public ulong StableKey { get; }

    public Guid SpeciesGuid { get; }

    public Vector3 LocalPosition { get; }

    public Quaternion Orientation { get; }

    public float UniformScale { get; }

    public float ConservativeRadius { get; }
}

public sealed class VegetationResidentPageData
{
    private readonly VegetationResidentSpecies[] m_Species;
    private readonly VegetationResidentInstance[] m_Instances;
    private readonly CookedVegetationInstancePageAcceleration? m_Acceleration;
    private readonly byte[] m_ContentHash;

    private VegetationResidentPageData(
        Guid guid,
        Guid clusterGuid,
        string packageId,
        WorldPosition origin,
        WorldBounds bounds,
        VegetationResidentSpecies[] species,
        VegetationResidentInstance[] instances,
        CookedVegetationInstancePageAcceleration? acceleration,
        long sizeInBytes,
        byte[] contentHash,
        ulong generation)
    {
        Guid = guid;
        ClusterGuid = clusterGuid;
        PackageId = packageId;
        Origin = origin;
        Bounds = bounds;
        m_Species = species;
        m_Instances = instances;
        m_Acceleration = acceleration;
        SizeInBytes = sizeInBytes;
        m_ContentHash = contentHash;
        Generation = generation;
    }

    public Guid Guid { get; }

    public Guid ClusterGuid { get; }

    public string PackageId { get; }

    public WorldPosition Origin { get; }

    public WorldBounds Bounds { get; }

    public ReadOnlySpan<VegetationResidentSpecies> Species => m_Species;

    public ReadOnlySpan<VegetationResidentInstance> Instances => m_Instances;

    public CookedVegetationInstancePageAcceleration? Acceleration => m_Acceleration;

    public int InstanceCount => m_Instances.Length;

    public long SizeInBytes { get; }

    public ReadOnlySpan<byte> ContentHash => m_ContentHash;

    public ulong Generation { get; }

    internal static VegetationResidentPageData Create(
        CookedVegetationInstancePage page,
        VegetationInstancePagePayloadIdentity payloadIdentity)
    {
        if (page.Guid == Guid.Empty ||
            page.ClusterGuid == Guid.Empty ||
            string.IsNullOrWhiteSpace(page.PackageId) ||
            page.GeneratedSchemaVersion <= 0 ||
            !page.Origin.IsFinite ||
            !page.Bounds.IsValid ||
            page.Species is not { Count: > 0 } ||
            page.Instances is not { Count: > 0 })
        {
            throw new InvalidDataException("Vegetation page runtime data is invalid.");
        }

        if (!payloadIdentity.IsValid || payloadIdentity.PageGuid != page.Guid)
        {
            throw new InvalidDataException(
                $"Vegetation page '{page.Guid:D}' payload identity is invalid.");
        }

        var species = new VegetationResidentSpecies[page.Species.Count];
        for (int index = 0; index < species.Length; index++)
        {
            CookedVegetationSpeciesReference reference = page.Species[index];
            if (reference.Guid == Guid.Empty || string.IsNullOrWhiteSpace(reference.PackageId))
            {
                throw new InvalidDataException(
                    $"Vegetation page '{page.Guid:D}' species metadata is invalid.");
            }

            species[index] = new VegetationResidentSpecies(reference.Guid, reference.PackageId);
        }

        for (int index = 1; index < species.Length; index++)
        {
            if (species[index - 1].CompareTo(species[index]) >= 0)
            {
                throw new InvalidDataException(
                    $"Vegetation page '{page.Guid:D}' species are not unique and ordered.");
            }
        }

        var instances = new VegetationResidentInstance[page.Instances.Count];
        ulong previousStableKey = 0;
        for (int index = 0; index < instances.Length; index++)
        {
            CookedVegetationInstance instance = page.Instances[index];
            if (instance.StableKey == 0 ||
                instance.StableKey <= previousStableKey ||
                instance.SpeciesIndex >= species.Length ||
                !IsFinite(instance.LocalPosition) ||
                !IsFinite(instance.Orientation) ||
                !float.IsFinite(instance.UniformScale) ||
                instance.UniformScale <= 0.0f ||
                !float.IsFinite(instance.ConservativeRadius) ||
                instance.ConservativeRadius <= 0.0f)
            {
                throw new InvalidDataException(
                    $"Vegetation page '{page.Guid:D}' instance {index} is invalid.");
            }

            var worldPosition = new WorldPosition(
                page.Origin.X + instance.LocalPosition.X,
                page.Origin.Y + instance.LocalPosition.Y,
                page.Origin.Z + instance.LocalPosition.Z);
            if (!worldPosition.IsFinite)
            {
                throw new InvalidDataException(
                    $"Vegetation page '{page.Guid:D}' instance {index} has an invalid world position.");
            }

            instances[index] = new VegetationResidentInstance(
                VegetationRuntimeInstanceIdentity.Create(
                    page.ClusterGuid,
                    page.Guid,
                    instance.StableKey),
                instance.StableKey,
                species[checked((int)instance.SpeciesIndex)].Guid,
                instance.LocalPosition,
                instance.Orientation,
                instance.UniformScale,
                instance.ConservativeRadius);
            previousStableKey = instance.StableKey;
        }

        return new VegetationResidentPageData(
            page.Guid,
            page.ClusterGuid,
            page.PackageId,
            page.Origin,
            page.Bounds,
            species,
            instances,
            page.Acceleration,
            payloadIdentity.SizeInBytes,
            payloadIdentity.ContentHash.ToArray(),
            generation: 0);
    }

    internal VegetationResidentPageData WithGeneration(ulong generation)
    {
        if (generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return new VegetationResidentPageData(
            Guid,
            ClusterGuid,
            PackageId,
            Origin,
            Bounds,
            m_Species,
            m_Instances,
            m_Acceleration,
            SizeInBytes,
            m_ContentHash,
            generation);
    }

    private static bool IsFinite(in Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(in Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}

public static class VegetationRuntimeInstanceIdentity
{
    private static readonly byte[] s_Domain =
        Encoding.ASCII.GetBytes("Arisen.VegetationRuntimeInstance.v1");

    public static Guid Create(Guid clusterGuid, Guid pageGuid, ulong stableKey)
    {
        if (clusterGuid == Guid.Empty || pageGuid == Guid.Empty || stableKey == 0)
        {
            throw new ArgumentException(
                "Vegetation runtime instance identity requires cluster/page GUIDs and a stable key.");
        }

        Span<byte> identity = stackalloc byte[s_Domain.Length + 40];
        s_Domain.CopyTo(identity);
        clusterGuid.TryWriteBytes(identity[s_Domain.Length..], bigEndian: true, out _);
        pageGuid.TryWriteBytes(identity[(s_Domain.Length + 16)..], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(identity[(s_Domain.Length + 32)..], stableKey);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(identity, hash);
        hash[6] = (byte)((hash[6] & 0x0f) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}

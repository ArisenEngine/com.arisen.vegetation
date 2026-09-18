using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain;
using ArisenEngine.Terrain.Assets;

namespace ArisenEngine.Vegetation.Assets;

public enum VegetationScatterExclusionKind
{
    Soft = 1,
    Hard = 2
}

public readonly record struct VegetationScatterExclusion(
    WorldBounds Bounds,
    VegetationScatterExclusionKind Kind);

public readonly record struct VegetationScatterCandidate(
    ulong StableKey,
    long SpatialCellX,
    long SpatialCellZ,
    int Ordinal,
    double WorldX,
    double WorldZ,
    double DensityRank,
    double ScaleRank,
    double YawRank,
    double TiltRank);

public sealed record VegetationScatterBakeDescriptor(
    Guid WorldGuid,
    CookedVegetationBiome Biome,
    CookedVegetationSpecies Species,
    CookedTerrainRoot TerrainRoot,
    IReadOnlyList<CookedTerrainTile> TerrainTiles,
    WorldPartitionSettings Partition,
    WorldCellKey Cell,
    string EntryId,
    float UnscaledConservativeRadius,
    IReadOnlyList<VegetationScatterExclusion> Exclusions);

public sealed record VegetationScatterCookRequest(
    Guid WorldGuid,
    AssetRef<VegetationBiomeSourceAsset> Biome,
    AssetRef<TerrainRootSourceAsset> TerrainRoot,
    WorldPartitionSettings Partition,
    WorldCellKey Cell,
    string EntryId,
    float UnscaledConservativeRadius,
    IReadOnlyList<VegetationScatterExclusion> Exclusions);

public readonly record struct VegetationScatterBakeMetrics(
    int CandidateCount,
    int DensityRejectedCount,
    int TerrainRejectedCount,
    int RuleRejectedCount,
    int ExclusionRejectedCount,
    int SpacingRejectedCount,
    int OwnershipRejectedCount,
    int AcceptedCount);

public enum VegetationScatterBakeDisposition
{
    NoOutput = 0,
    Output = 1
}

public sealed record VegetationScatterBakeResult
{
    private readonly VegetationClusterCookDescriptor? m_Cluster;

    private VegetationScatterBakeResult(
        VegetationScatterBakeDisposition disposition,
        VegetationClusterCookDescriptor? cluster,
        AssetMetadata clusterMetadata,
        IReadOnlyList<AssetMetadata> pageMetadata,
        IReadOnlyList<byte[]> pageContentHashes,
        byte[] placementContentHash,
        VegetationScatterBakeMetrics metrics)
    {
        if ((disposition == VegetationScatterBakeDisposition.Output) != (cluster != null))
        {
            throw new ArgumentException(
                "Vegetation scatter bake disposition does not match cluster output.",
                nameof(disposition));
        }

        Disposition = disposition;
        m_Cluster = cluster;
        ClusterMetadata = clusterMetadata ?? throw new ArgumentNullException(nameof(clusterMetadata));
        PageMetadata = pageMetadata ?? throw new ArgumentNullException(nameof(pageMetadata));
        PageContentHashes = pageContentHashes ??
            throw new ArgumentNullException(nameof(pageContentHashes));
        if (PageMetadata.Count != PageContentHashes.Count)
        {
            throw new ArgumentException(
                "Vegetation scatter page metadata and content-hash counts must match.",
                nameof(pageContentHashes));
        }
        PlacementContentHash = placementContentHash ??
            throw new ArgumentNullException(nameof(placementContentHash));
        Metrics = metrics;
    }

    public VegetationScatterBakeDisposition Disposition { get; }

    public VegetationClusterCookDescriptor Cluster => m_Cluster ??
        throw new InvalidOperationException(
            "A no-output vegetation scatter plan does not contain a cluster descriptor.");

    public AssetMetadata ClusterMetadata { get; }

    public IReadOnlyList<AssetMetadata> PageMetadata { get; }

    public IReadOnlyList<byte[]> PageContentHashes { get; }

    public byte[] PlacementContentHash { get; }

    public VegetationScatterBakeMetrics Metrics { get; }

    public bool HasOutput => Disposition == VegetationScatterBakeDisposition.Output;

    public static VegetationScatterBakeResult CreateOutput(
        VegetationClusterCookDescriptor cluster,
        AssetMetadata clusterMetadata,
        IReadOnlyList<AssetMetadata> pageMetadata,
        IReadOnlyList<byte[]> pageContentHashes,
        byte[] placementContentHash,
        VegetationScatterBakeMetrics metrics) => new(
            VegetationScatterBakeDisposition.Output,
            cluster ?? throw new ArgumentNullException(nameof(cluster)),
            clusterMetadata,
            pageMetadata,
            pageContentHashes,
            placementContentHash,
            metrics);

    public static VegetationScatterBakeResult CreateNoOutput(
        AssetMetadata clusterMetadata,
        byte[] placementContentHash,
        VegetationScatterBakeMetrics metrics) => new(
            VegetationScatterBakeDisposition.NoOutput,
            null,
            clusterMetadata ?? throw new ArgumentNullException(nameof(clusterMetadata)),
            Array.Empty<AssetMetadata>(),
            Array.Empty<byte[]>(),
            placementContentHash ?? throw new ArgumentNullException(nameof(placementContentHash)),
            metrics);
}

public static class VegetationScatterIdentity
{
    public const string ClusterChildKind = "vegetation-cluster";
    public const string PageChildKind = "vegetation-instance-page";
    public const string ClusterImporter = "ArisenVegetationScatterClusterImporter";
    public const string PageImporter = "ArisenVegetationScatterPageImporter";

    public static string CreateClusterChildKey(
        Guid worldGuid,
        Guid terrainRootGuid,
        string terrainRootPackageId,
        Guid speciesGuid,
        string speciesPackageId,
        string entryId,
        WorldCellKey cell)
    {
        if (worldGuid == Guid.Empty || terrainRootGuid == Guid.Empty || speciesGuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Vegetation scatter identity requires world, terrain-root, and species GUIDs.");
        }

        string canonicalEntryId = RequireEntryId(entryId);
        string canonicalTerrainPackageId = RequirePackageId(
            terrainRootPackageId,
            nameof(terrainRootPackageId));
        string canonicalSpeciesPackageId = RequirePackageId(
            speciesPackageId,
            nameof(speciesPackageId));
        WorldCellId cellId = WorldCellIdentity.Create(worldGuid, cell.Coordinate, cell.Layer);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"recipe=1;world={worldGuid:N};cell={cellId.Value:N};terrain={terrainRootGuid:N};" +
            $"terrainPackage={canonicalTerrainPackageId};species={speciesGuid:N};" +
            $"speciesPackage={canonicalSpeciesPackageId};entry={canonicalEntryId}");
    }

    public static Guid CreateClusterGuid(
        Guid biomeGuid,
        string packageId,
        Guid worldGuid,
        Guid terrainRootGuid,
        string terrainRootPackageId,
        Guid speciesGuid,
        string speciesPackageId,
        string entryId,
        WorldCellKey cell)
    {
        return GeneratedAssetIdentity.CreateChildGuid(
            biomeGuid,
            packageId,
            ClusterChildKind,
            CreateClusterChildKey(
                worldGuid,
                terrainRootGuid,
                terrainRootPackageId,
                speciesGuid,
                speciesPackageId,
                entryId,
                cell));
    }

    public static AssetMetadata CreateClusterMetadata(
        Guid biomeGuid,
        string packageId,
        Guid worldGuid,
        Guid terrainRootGuid,
        string terrainRootPackageId,
        Guid speciesGuid,
        string speciesPackageId,
        string entryId,
        WorldCellKey cell)
    {
        return GeneratedAssetIdentity.CreateChildMetadata(
            biomeGuid,
            packageId,
            ClusterChildKind,
            CreateClusterChildKey(
                worldGuid,
                terrainRootGuid,
                terrainRootPackageId,
                speciesGuid,
                speciesPackageId,
                entryId,
                cell),
            VegetationAssetTypes.Cluster,
            ClusterImporter);
    }

    public static string CreatePageChildKey(int pageIndex, ReadOnlySpan<byte> contentHash)
    {
        if (pageIndex < 0 || contentHash.Length != VegetationCookedContainer.HashSize)
        {
            throw new ArgumentException(
                "Vegetation scatter page identity requires a non-negative index and SHA-256 hash.");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"page={pageIndex};content={Convert.ToHexString(contentHash).ToLowerInvariant()}");
    }

    public static Guid CreatePageGuid(
        Guid clusterGuid,
        string packageId,
        int pageIndex,
        ReadOnlySpan<byte> contentHash)
    {
        return GeneratedAssetIdentity.CreateChildGuid(
            clusterGuid,
            packageId,
            PageChildKind,
            CreatePageChildKey(pageIndex, contentHash));
    }

    public static AssetMetadata CreatePageMetadata(
        Guid clusterGuid,
        string packageId,
        int pageIndex,
        ReadOnlySpan<byte> contentHash)
    {
        return GeneratedAssetIdentity.CreateChildMetadata(
            clusterGuid,
            packageId,
            PageChildKind,
            CreatePageChildKey(pageIndex, contentHash),
            VegetationAssetTypes.InstancePage,
            PageImporter);
    }

    internal static string RequireEntryId(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId) ||
            entryId.Length > VegetationBiomeSourceAssetLoader.MaximumEntryIdLength ||
            !string.Equals(entryId, entryId.Trim(), StringComparison.Ordinal) ||
            entryId.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= 'A' and <= 'Z') &&
                !(character is >= '0' and <= '9') &&
                character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "Vegetation scatter entry ID must be canonical ASCII text.",
                nameof(entryId));
        }

        return entryId;
    }

    internal static string RequirePackageId(string packageId, string parameterName)
    {
        if (string.IsNullOrEmpty(packageId) ||
            packageId.Length > VegetationBiomeSourceAssetLoader.MaximumPackageIdLength ||
            packageId.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException(
                "Vegetation scatter package ID must be canonical lowercase ASCII text.",
                parameterName);
        }

        return packageId;
    }
}

public static class VegetationScatterCandidateSequence
{
    public const int SequenceVersion = 1;
    public const double SpatialCellSize = 1.0;

    private const double Unit53Scale = 1.0 / 9_007_199_254_740_992.0;
    private const ulong LaneStride = 0x9e3779b97f4a7c15UL;
    private static readonly byte[] s_Domain =
        Encoding.ASCII.GetBytes("arisen.vegetation.scatter-candidate.v1");

    public static VegetationScatterCandidate Create(
        Guid worldGuid,
        Guid biomeGuid,
        Guid terrainRootGuid,
        Guid speciesGuid,
        string entryId,
        ulong globalSeed,
        ulong seedSalt,
        long spatialCellX,
        long spatialCellZ,
        int ordinal)
    {
        ulong baseSeed = CreateSeed(
            worldGuid,
            biomeGuid,
            terrainRootGuid,
            speciesGuid,
            entryId,
            globalSeed,
            seedSalt);
        return Create(baseSeed, spatialCellX, spatialCellZ, ordinal);
    }

    internal static ulong CreateSeed(
        Guid worldGuid,
        Guid biomeGuid,
        Guid terrainRootGuid,
        Guid speciesGuid,
        string entryId,
        ulong globalSeed,
        ulong seedSalt)
    {
        if (worldGuid == Guid.Empty || biomeGuid == Guid.Empty ||
            terrainRootGuid == Guid.Empty || speciesGuid == Guid.Empty)
        {
            throw new ArgumentException(
                "Vegetation scatter candidates require biome, terrain-root, and species GUIDs.");
        }

        string canonicalEntryId = VegetationScatterIdentity.RequireEntryId(entryId);

        Span<byte> fixedIdentity = stackalloc byte[80];
        worldGuid.TryWriteBytes(fixedIdentity, bigEndian: true, out _);
        biomeGuid.TryWriteBytes(fixedIdentity[16..], bigEndian: true, out _);
        terrainRootGuid.TryWriteBytes(fixedIdentity[32..], bigEndian: true, out _);
        speciesGuid.TryWriteBytes(fixedIdentity[48..], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedIdentity[64..], globalSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(fixedIdentity[72..], seedSalt);
        using IncrementalHash identityHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identityHash.AppendData(s_Domain);
        identityHash.AppendData(fixedIdentity);
        Span<byte> entryBytes = stackalloc byte[canonicalEntryId.Length];
        Encoding.ASCII.GetBytes(canonicalEntryId, entryBytes);
        identityHash.AppendData(entryBytes);
        Span<byte> digest = stackalloc byte[32];
        if (!identityHash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
        {
            throw new InvalidOperationException("Vegetation scatter seed hashing failed.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    internal static VegetationScatterCandidate Create(
        ulong baseSeed,
        long spatialCellX,
        long spatialCellZ,
        int ordinal)
    {
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        ulong stableKey = Mix(
            baseSeed ^
            Mix(unchecked((ulong)spatialCellX) + 0x243f6a8885a308d3UL) ^
            Mix(unchecked((ulong)spatialCellZ) + 0x13198a2e03707344UL) ^
            Mix(unchecked((ulong)ordinal) + 0xa4093822299f31d0UL));
        if (stableKey == 0)
        {
            stableKey = 0x6a09e667f3bcc909UL;
        }

        double jitterX = ToUnit(Lane(stableKey, 0));
        double jitterZ = ToUnit(Lane(stableKey, 1));
        return new VegetationScatterCandidate(
            stableKey,
            spatialCellX,
            spatialCellZ,
            ordinal,
            spatialCellX + jitterX,
            spatialCellZ + jitterZ,
            ToUnit(Lane(stableKey, 2)),
            ToUnit(Lane(stableKey, 3)),
            ToUnit(Lane(stableKey, 4)),
            ToUnit(Lane(stableKey, 5)));
    }

    private static ulong Lane(ulong stableKey, int lane) =>
        Mix(unchecked(stableKey + (unchecked((ulong)lane) + 1UL) * LaneStride));

    private static double ToUnit(ulong value) => (value >> 11) * Unit53Scale;

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value = unchecked(value * 0xbf58476d1ce4e5b9UL);
        value ^= value >> 27;
        value = unchecked(value * 0x94d049bb133111ebUL);
        return value ^ (value >> 31);
    }
}

public static class VegetationScatterBaker
{
    public const int CurrentGeneratedSchemaVersion = 1;
    public const int MaximumCandidateCountPerBake = 1_048_576;

    private static readonly byte[] s_PlacementDomain =
        Encoding.ASCII.GetBytes("arisen.vegetation.scatter-page-content.v1");

    public static VegetationScatterBakeResult Build(
        IAssetDatabase assetDatabase,
        VegetationScatterCookRequest request)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Partition);
        ArgumentNullException.ThrowIfNull(request.Exclusions);
        _ = VegetationScatterIdentity.RequireEntryId(request.EntryId);
        if (!VegetationBiomeAssetCooker.TryLoadCooked(
                assetDatabase,
                request.Biome,
                out CookedVegetationBiome biome,
                out string biomeDiagnostic))
        {
            throw new InvalidOperationException(biomeDiagnostic);
        }

        CookedVegetationBiomeEntry entry = ResolveEntry(biome, request.EntryId);
        if (!VegetationSpeciesAssetCooker.TryLoadCooked(
                assetDatabase,
                entry.Species.ToAssetRef(),
                out CookedVegetationSpecies species,
                out string speciesDiagnostic))
        {
            throw new InvalidOperationException(speciesDiagnostic);
        }

        if (!TerrainRootAssetCooker.TryLoadCooked(
                assetDatabase,
                request.TerrainRoot,
                out CookedTerrainRoot terrainRoot,
                out string terrainDiagnostic))
        {
            throw new InvalidOperationException(terrainDiagnostic);
        }

        CookedTerrainTile[] tiles = LoadPinnedTerrainTiles(assetDatabase, terrainRoot);
        return Build(new VegetationScatterBakeDescriptor(
            request.WorldGuid,
            biome,
            species,
            terrainRoot,
            tiles,
            request.Partition,
            request.Cell,
            request.EntryId,
            request.UnscaledConservativeRadius,
            request.Exclusions));
    }

    internal static VegetationScatterBakeResult Build(VegetationScatterBakeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor);

        CookedVegetationBiomeEntry entry = ResolveEntry(descriptor.Biome, descriptor.EntryId);
        ValidateEntryAndSpecies(
            entry,
            descriptor.Species,
            descriptor.UnscaledConservativeRadius);
        int[] layerIndices = ResolveLayerIndices(entry, descriptor.TerrainRoot);
        var sampler = new CookedTerrainSurfaceSampler(
            descriptor.TerrainRoot,
            descriptor.TerrainTiles);
        WorldPosition pageOrigin = CanonicalizeWorldPosition(
            WorldPartitionCoordinates.GetCellOrigin(
                descriptor.Partition,
                descriptor.Cell.Coordinate));
        ulong candidateSeed = VegetationScatterCandidateSequence.CreateSeed(
            descriptor.WorldGuid,
            descriptor.Biome.Guid,
            descriptor.TerrainRoot.Guid,
            descriptor.Species.Guid,
            entry.EntryId,
            descriptor.Biome.GlobalSeed,
            entry.SeedSalt);

        ScatterRegion region = BuildRegion(
            descriptor.TerrainRoot,
            descriptor.Partition,
            descriptor.Cell.Coordinate,
            entry.MinimumSpacing);
        CandidateRange candidateRange = BuildCandidateRange(region, entry.Density);
        var eligible = new CandidateEvaluation[candidateRange.MaximumCandidateCount];
        int eligibleCount = 0;
        int densityRejected = 0;
        int terrainRejected = 0;
        int ruleRejected = 0;
        int exclusionRejected = 0;

        int wholeDensity = checked((int)Math.Floor(entry.Density));
        double fractionalDensity = entry.Density - wholeDensity;
        for (long spatialZ = candidateRange.MinimumZ;
             spatialZ < candidateRange.MaximumZExclusive;
             spatialZ++)
        {
            for (long spatialX = candidateRange.MinimumX;
                 spatialX < candidateRange.MaximumXExclusive;
                 spatialX++)
            {
                for (int ordinal = 0; ordinal < candidateRange.OrdinalsPerSpatialCell; ordinal++)
                {
                    VegetationScatterCandidate candidate = VegetationScatterCandidateSequence.Create(
                        candidateSeed,
                        spatialX,
                        spatialZ,
                        ordinal);
                    if (ordinal >= wholeDensity && candidate.DensityRank >= fractionalDensity)
                    {
                        densityRejected++;
                        continue;
                    }

                    if (!sampler.TrySample(
                            new WorldPosition(candidate.WorldX, 0.0, candidate.WorldZ),
                            out CookedTerrainSurfaceSample surface))
                    {
                        terrainRejected++;
                        continue;
                    }

                    if (!PassesTerrainRules(entry, layerIndices, surface))
                    {
                        ruleRejected++;
                        continue;
                    }

                    CanonicalPlacement placement = CanonicalizePlacement(
                        descriptor,
                        surface.SurfacePosition);
                    if (IsExcluded(
                            entry.ExclusionPolicy,
                            descriptor.Exclusions,
                            placement.WorldPosition))
                    {
                        exclusionRejected++;
                        continue;
                    }

                    eligible[eligibleCount++] = new CandidateEvaluation(
                        candidate.StableKey,
                        candidate.ScaleRank,
                        candidate.YawRank,
                        candidate.TiltRank,
                        placement.WorldPosition,
                        placement.Owner,
                        placement.OwnerLocalPosition);
                }
            }
        }

        Array.Sort(eligible, 0, eligibleCount, CandidateEvaluationComparer.Instance);
        for (int index = 1; index < eligibleCount; index++)
        {
            if (eligible[index - 1].StableKey == eligible[index].StableKey)
            {
                throw Invalid(
                    descriptor,
                    $"candidate stable key '{eligible[index].StableKey}' collided");
            }
        }

        string packageId = descriptor.Biome.PackageId;
        AssetMetadata clusterMetadata = VegetationScatterIdentity.CreateClusterMetadata(
            descriptor.Biome.Guid,
            packageId,
            descriptor.WorldGuid,
            descriptor.TerrainRoot.Guid,
            descriptor.TerrainRoot.PackageId,
            descriptor.Species.Guid,
            descriptor.Species.PackageId,
            entry.EntryId,
            descriptor.Cell);
        int acceptedLimit = VegetationClusterAssetCooker.MaximumTotalInstanceCount;
        int[] acceptedIndices = ApplySpacing(
            descriptor,
            entry,
            region,
            eligible,
            eligibleCount,
            acceptedLimit,
            out int acceptedCount,
            out int spacingRejected,
            out int ownershipRejected);
        if (acceptedCount == 0)
        {
            var metrics = new VegetationScatterBakeMetrics(
                candidateRange.MaximumCandidateCount,
                densityRejected,
                terrainRejected,
                ruleRejected,
                exclusionRejected,
                spacingRejected,
                ownershipRejected,
                acceptedCount);
            return VegetationScatterBakeResult.CreateNoOutput(
                clusterMetadata,
                ComputePlacementContentHash(
                    descriptor,
                    entry,
                    pageOrigin,
                    Array.Empty<VegetationCookedInstanceInput>()),
                metrics);
        }

        VegetationCookedInstanceInput[] instances = BuildInstances(
            descriptor,
            descriptor.Species,
            eligible,
            acceptedIndices,
            acceptedCount);
        byte[] placementHash = ComputePlacementContentHash(
            descriptor,
            entry,
            pageOrigin,
            instances);
        if (instances.Length > VegetationClusterAssetCooker.MaximumTotalInstanceCount)
        {
            throw Invalid(
                descriptor,
                $"accepted instance count '{instances.Length}' exceeds the cluster limit " +
                $"'{VegetationClusterAssetCooker.MaximumTotalInstanceCount}'");
        }

        int pageCapacity = Math.Min(
            entry.ClusterSize,
            VegetationInstancePageAssetCooker.MaximumInstanceCount);
        int pageCount = checked((instances.Length + pageCapacity - 1) / pageCapacity);
        if (pageCount > VegetationClusterAssetCooker.MaximumPageCount)
        {
            throw Invalid(
                descriptor,
                $"accepted instance count '{instances.Length}' requires page count '{pageCount}', " +
                $"exceeding the cluster limit '{VegetationClusterAssetCooker.MaximumPageCount}'");
        }

        var pages = new VegetationInstancePageCookDescriptor[pageCount];
        var pageMetadata = new AssetMetadata[pageCount];
        var pageContentHashes = new byte[pageCount][];
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            int pageStart = checked(pageIndex * pageCapacity);
            int pageLength = Math.Min(pageCapacity, instances.Length - pageStart);
            var pageInstances = new VegetationCookedInstanceInput[pageLength];
            Array.Copy(instances, pageStart, pageInstances, 0, pageLength);
            byte[] pageHash = ComputePlacementContentHash(
                descriptor,
                entry,
                pageOrigin,
                pageInstances);
            AssetMetadata metadata = VegetationScatterIdentity.CreatePageMetadata(
                clusterMetadata.Guid,
                packageId,
                pageIndex,
                pageHash);
            pageMetadata[pageIndex] = metadata;
            pageContentHashes[pageIndex] = pageHash;
            pages[pageIndex] = new VegetationInstancePageCookDescriptor(
                metadata.Guid,
                clusterMetadata.Guid,
                packageId,
                CurrentGeneratedSchemaVersion,
                pageOrigin,
                [new CookedVegetationSpeciesReference(
                    descriptor.Species.Guid,
                    descriptor.Species.PackageId)],
                Array.AsReadOnly(pageInstances));
        }

        var clusterDescriptor = new VegetationClusterCookDescriptor(
            clusterMetadata.Guid,
            packageId,
            CurrentGeneratedSchemaVersion,
            new CookedVegetationBiomeReference(
                descriptor.Biome.Guid,
                descriptor.Biome.PackageId),
            Array.AsReadOnly(pages),
            [descriptor.Species]);
        return VegetationScatterBakeResult.CreateOutput(
            clusterDescriptor,
            clusterMetadata,
            Array.AsReadOnly(pageMetadata),
            Array.AsReadOnly(pageContentHashes),
            placementHash,
            new VegetationScatterBakeMetrics(
                candidateRange.MaximumCandidateCount,
                densityRejected,
                terrainRejected,
                ruleRejected,
                exclusionRejected,
                spacingRejected,
                ownershipRejected,
                acceptedCount));
    }

    private static CookedTerrainTile[] LoadPinnedTerrainTiles(
        IAssetDatabase assetDatabase,
        CookedTerrainRoot terrainRoot)
    {
        var tiles = new CookedTerrainTile[terrainRoot.Tiles.Count];
        for (int index = 0; index < terrainRoot.Tiles.Count; index++)
        {
            CookedTerrainTileReference reference = terrainRoot.Tiles[index]
                ?? throw new InvalidOperationException(
                    $"[VegetationScatterBaker] Terrain root '{terrainRoot.Guid:D}' has a null " +
                    $"tile reference at '{index}'.");
            if (!assetDatabase.TryGetAssetDescriptor(
                    reference.Guid,
                    out AssetDescriptor descriptor) ||
                descriptor.Guid != reference.Guid ||
                !string.Equals(
                    descriptor.AssetType,
                    TerrainAssetTypes.Tile,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(descriptor.PackageId, terrainRoot.PackageId, StringComparison.Ordinal) ||
                !assetDatabase.TryGetCookedArtifact(
                    reference.Guid,
                    TerrainTileAssetCooker.RuntimeVariant,
                    out CookedAssetRecord artifact) ||
                artifact.Guid != reference.Guid ||
                !string.Equals(
                    artifact.AssetType,
                    TerrainAssetTypes.Tile,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    artifact.Variant,
                    TerrainTileAssetCooker.RuntimeVariant,
                    StringComparison.Ordinal) ||
                artifact.SizeInBytes != reference.PayloadBytes ||
                !assetDatabase.TryLoadCookedAsset(
                    reference.Guid,
                    TerrainTileAssetCooker.RuntimeVariant,
                    TerrainAssetTypes.Tile,
                    out CookedAssetHandle handle))
            {
                throw new InvalidOperationException(
                    $"[VegetationScatterBaker] Terrain tile '{reference.Guid:D}' does not have the " +
                    "exact cataloged artifact pinned by its cooked root.");
            }

            try
            {
                ReadOnlyMemory<byte> bytes = assetDatabase.GetCookedAssetBytes(handle);
                if (bytes.Length != reference.PayloadBytes ||
                    reference.ContentHash.Length != SHA256.HashSizeInBytes ||
                    !SHA256.HashData(bytes.Span).AsSpan().SequenceEqual(reference.ContentHash))
                {
                    throw new InvalidOperationException(
                        $"[VegetationScatterBaker] Terrain tile '{reference.Guid:D}' does not match " +
                        "the exact size/SHA-256 pin in its cooked root.");
                }

                if (!TerrainTileAssetCooker.TryReadPayload(
                        reference.Guid,
                        terrainRoot.Guid,
                        terrainRoot.LayerSetGuid,
                        terrainRoot.PackageId,
                        bytes.Span,
                        artifact.Path,
                        out CookedTerrainTile tile,
                        out string diagnostic))
                {
                    throw new InvalidOperationException(diagnostic);
                }

                tiles[index] = tile;
            }
            finally
            {
                assetDatabase.Release(handle);
            }
        }

        return tiles;
    }

    private static void ValidateDescriptor(VegetationScatterBakeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor.Biome);
        ArgumentNullException.ThrowIfNull(descriptor.Species);
        ArgumentNullException.ThrowIfNull(descriptor.TerrainRoot);
        ArgumentNullException.ThrowIfNull(descriptor.TerrainTiles);
        ArgumentNullException.ThrowIfNull(descriptor.Partition);
        ArgumentNullException.ThrowIfNull(descriptor.Exclusions);
        _ = VegetationScatterIdentity.CreateClusterChildKey(
            descriptor.WorldGuid,
            descriptor.TerrainRoot.Guid,
            descriptor.TerrainRoot.PackageId,
            descriptor.Species.Guid,
            descriptor.Species.PackageId,
            descriptor.EntryId,
            descriptor.Cell);
        _ = VegetationScatterIdentity.RequirePackageId(
            descriptor.Biome.PackageId,
            nameof(descriptor.Biome.PackageId));
        if (!descriptor.Partition.Origin.IsFinite ||
            !descriptor.Partition.CellSize.IsFinite ||
            descriptor.Partition.CellSize.X <= 0.0 ||
            descriptor.Partition.CellSize.Y <= 0.0 ||
            descriptor.Partition.CellSize.Z <= 0.0)
        {
            throw Invalid(descriptor, "world partition origin or cell size is invalid");
        }

        if (!float.IsFinite(descriptor.UnscaledConservativeRadius) ||
            descriptor.UnscaledConservativeRadius <= 0.0f)
        {
            throw Invalid(descriptor, "unscaled species conservative radius is invalid");
        }

        for (int index = 0; index < descriptor.Exclusions.Count; index++)
        {
            VegetationScatterExclusion exclusion = descriptor.Exclusions[index];
            if (!exclusion.Bounds.IsValid || !Enum.IsDefined(exclusion.Kind))
            {
                throw Invalid(descriptor, $"exclusion '{index}' is invalid");
            }
        }
    }

    private static CookedVegetationBiomeEntry ResolveEntry(
        CookedVegetationBiome biome,
        string entryId)
    {
        CookedVegetationBiomeEntry? selected = null;
        for (int index = 0; index < biome.Entries.Count; index++)
        {
            CookedVegetationBiomeEntry candidate = biome.Entries[index];
            if (string.Equals(candidate.EntryId, entryId, StringComparison.Ordinal))
            {
                if (selected != null)
                {
                    throw new InvalidOperationException(
                        $"[VegetationScatterBaker] Biome '{biome.Guid:D}' duplicates entry '{entryId}'.");
                }

                selected = candidate;
            }
        }

        return selected ?? throw new InvalidOperationException(
            $"[VegetationScatterBaker] Biome '{biome.Guid:D}' has no entry '{entryId}'.");
    }

    private static void ValidateEntryAndSpecies(
        CookedVegetationBiomeEntry entry,
        CookedVegetationSpecies species,
        float unscaledConservativeRadius)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(species);
        _ = VegetationScatterIdentity.RequireEntryId(entry.EntryId);
        _ = VegetationScatterIdentity.RequirePackageId(
            entry.Species.PackageId,
            nameof(entry.Species.PackageId));
        _ = VegetationScatterIdentity.RequirePackageId(
            species.PackageId,
            nameof(species.PackageId));
        if (entry.Species.Guid != species.Guid ||
            species.Guid == Guid.Empty ||
            !string.Equals(entry.Species.PackageId, species.PackageId, StringComparison.Ordinal) ||
            !float.IsFinite(entry.Density) ||
            entry.Density <= 0.0f ||
            entry.Density > VegetationBiomeSourceAssetLoader.MaximumDensity ||
            !IsRange(
                entry.AltitudeRange,
                VegetationBiomeSourceAssetLoader.MinimumAltitude,
                VegetationBiomeSourceAssetLoader.MaximumAltitude) ||
            !IsRange(
                entry.SlopeRangeDegrees,
                VegetationBiomeSourceAssetLoader.MinimumSlopeDegrees,
                VegetationBiomeSourceAssetLoader.MaximumSlopeDegrees) ||
            !VegetationSourceValidation.IsBounded(
                entry.MinimumSpacing,
                VegetationBiomeSourceAssetLoader.MinimumSpacing,
                VegetationBiomeSourceAssetLoader.MaximumSpacing) ||
            entry.ClusterSize is < VegetationBiomeSourceAssetLoader.MinimumClusterSize or
                > VegetationBiomeSourceAssetLoader.MaximumClusterSize ||
            !Enum.IsDefined(entry.ExclusionPolicy) ||
            entry.LayerWeightRules is null ||
            entry.LayerWeightRules.Count > VegetationBiomeSourceAssetLoader.MaximumLayerWeightRuleCount ||
            !IsRange(
                species.ScaleRange,
                VegetationSpeciesSourceAssetLoader.MinimumScale,
                VegetationSpeciesSourceAssetLoader.MaximumScale) ||
            !IsRange(
                species.YawRangeDegrees,
                VegetationSpeciesSourceAssetLoader.MinimumYawDegrees,
                VegetationSpeciesSourceAssetLoader.MaximumYawDegrees) ||
            !IsRange(
                species.TiltRangeDegrees,
                VegetationSpeciesSourceAssetLoader.MinimumTiltDegrees,
                VegetationSpeciesSourceAssetLoader.MaximumTiltDegrees))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] Entry '{entry.EntryId}' or its cooked species identity is invalid.");
        }

        double maximumRadius = (double)unscaledConservativeRadius * species.ScaleRange.Maximum;
        if (!double.IsFinite(maximumRadius) || maximumRadius > float.MaxValue)
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] Entry '{entry.EntryId}' conservative radius overflows " +
                "the instance-page float representation.");
        }

        var layerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < entry.LayerWeightRules.Count; index++)
        {
            CookedVegetationLayerWeightRule rule = entry.LayerWeightRules[index]
                ?? throw new InvalidOperationException(
                    $"[VegetationScatterBaker] Entry '{entry.EntryId}' layer rule '{index}' is null.");
            if (!IsAsciiIdentifier(
                    rule.LayerId,
                    VegetationBiomeSourceAssetLoader.MaximumLayerIdLength) ||
                !layerIds.Add(rule.LayerId) ||
                !IsRange(
                    rule.WeightRange,
                    VegetationBiomeSourceAssetLoader.MinimumLayerWeight,
                    VegetationBiomeSourceAssetLoader.MaximumLayerWeight))
            {
                throw new InvalidOperationException(
                    $"[VegetationScatterBaker] Entry '{entry.EntryId}' layer rule '{index}' is invalid.");
            }
        }
    }

    private static int[] ResolveLayerIndices(
        CookedVegetationBiomeEntry entry,
        CookedTerrainRoot terrainRoot)
    {
        var indices = new int[entry.LayerWeightRules.Count];
        for (int ruleIndex = 0; ruleIndex < entry.LayerWeightRules.Count; ruleIndex++)
        {
            string layerId = entry.LayerWeightRules[ruleIndex].LayerId;
            int layerIndex = -1;
            for (int index = 0; index < terrainRoot.Layers.Count; index++)
            {
                if (string.Equals(
                        terrainRoot.Layers[index].Id,
                        layerId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    layerIndex = index;
                    break;
                }
            }

            if (layerIndex < 0 || layerIndex >= TerrainCookedFormat.WeightChannelCount)
            {
                throw new InvalidOperationException(
                    $"[VegetationScatterBaker] Entry '{entry.EntryId}' requires unavailable terrain " +
                    $"layer '{layerId}'; available layers are " +
                    $"[{string.Join(", ", terrainRoot.Layers.Select(layer => layer.Id))}].");
            }

            indices[ruleIndex] = layerIndex;
        }

        return indices;
    }

    private static ScatterRegion BuildRegion(
        CookedTerrainRoot terrainRoot,
        WorldPartitionSettings partition,
        WorldCellCoordinate cell,
        float minimumSpacing)
    {
        WorldPosition cellOrigin = WorldPartitionCoordinates.GetCellOrigin(partition, cell);
        double cellMaxX = cellOrigin.X + partition.CellSize.X;
        double cellMaxZ = cellOrigin.Z + partition.CellSize.Z;
        double terrainMaxX = terrainRoot.WorldPlacement.X +
            ((terrainRoot.HeightSourceWidth - 1) * terrainRoot.SampleSpacing.X);
        double terrainMaxZ = terrainRoot.WorldPlacement.Z +
            ((terrainRoot.HeightSourceHeight - 1) * terrainRoot.SampleSpacing.Z);
        double ownedMinX = Math.Max(cellOrigin.X, terrainRoot.WorldPlacement.X);
        double ownedMinZ = Math.Max(cellOrigin.Z, terrainRoot.WorldPlacement.Z);
        double ownedMaxX = Math.Min(cellMaxX, terrainMaxX);
        double ownedMaxZ = Math.Min(cellMaxZ, terrainMaxZ);
        if (!double.IsFinite(ownedMinX) || !double.IsFinite(ownedMinZ) ||
            !double.IsFinite(ownedMaxX) || !double.IsFinite(ownedMaxZ) ||
            ownedMinX >= ownedMaxX || ownedMinZ >= ownedMaxZ)
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] Cell ({cell.X}, {cell.Y}, {cell.Z}) has no terrain area.");
        }

        return new ScatterRegion(
            Math.Max(terrainRoot.WorldPlacement.X, ownedMinX - minimumSpacing),
            Math.Max(terrainRoot.WorldPlacement.Z, ownedMinZ - minimumSpacing),
            Math.Min(terrainMaxX, ownedMaxX + minimumSpacing),
            Math.Min(terrainMaxZ, ownedMaxZ + minimumSpacing));
    }

    private static CandidateRange BuildCandidateRange(ScatterRegion region, float density)
    {
        long minimumX = CheckedFloor(region.MinimumX);
        long minimumZ = CheckedFloor(region.MinimumZ);
        long maximumXExclusive = CheckedCeiling(region.MaximumX);
        long maximumZExclusive = CheckedCeiling(region.MaximumZ);
        int ordinals = checked((int)Math.Ceiling(density));
        long spatialCellCount = checked(
            (maximumXExclusive - minimumX) * (maximumZExclusive - minimumZ));
        long candidateCount = checked(spatialCellCount * ordinals);
        if (candidateCount <= 0 || candidateCount > MaximumCandidateCountPerBake)
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] Candidate count '{candidateCount}' exceeds bounded limit " +
                $"'{MaximumCandidateCountPerBake}'.");
        }

        return new CandidateRange(
            minimumX,
            minimumZ,
            maximumXExclusive,
            maximumZExclusive,
            ordinals,
            checked((int)candidateCount));
    }

    private static bool PassesTerrainRules(
        CookedVegetationBiomeEntry entry,
        IReadOnlyList<int> layerIndices,
        in CookedTerrainSurfaceSample surface)
    {
        double altitude = surface.SurfacePosition.Y;
        if (altitude < entry.AltitudeRange.Minimum || altitude > entry.AltitudeRange.Maximum)
        {
            return false;
        }

        float slopeDegrees = MathF.Acos(Math.Clamp(surface.Normal.Y, -1.0f, 1.0f)) *
            (180.0f / MathF.PI);
        if (!float.IsFinite(slopeDegrees) ||
            slopeDegrees < entry.SlopeRangeDegrees.Minimum ||
            slopeDegrees > entry.SlopeRangeDegrees.Maximum)
        {
            return false;
        }

        for (int index = 0; index < entry.LayerWeightRules.Count; index++)
        {
            CookedVegetationLayerWeightRule rule = entry.LayerWeightRules[index];
            float weight = GetWeight(surface.LayerWeights, layerIndices[index]);
            if (!float.IsFinite(weight) ||
                weight < rule.WeightRange.Minimum ||
                weight > rule.WeightRange.Maximum)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsExcluded(
        VegetationExclusionPolicy policy,
        IReadOnlyList<VegetationScatterExclusion> exclusions,
        in WorldPosition position)
    {
        for (int index = 0; index < exclusions.Count; index++)
        {
            VegetationScatterExclusion exclusion = exclusions[index];
            if (exclusion.Kind == VegetationScatterExclusionKind.Soft &&
                policy == VegetationExclusionPolicy.IgnoreSoft)
            {
                continue;
            }

            WorldBounds bounds = exclusion.Bounds;
            if (position.X >= bounds.Min.X && position.X < bounds.Max.X &&
                position.Y >= bounds.Min.Y && position.Y < bounds.Max.Y &&
                position.Z >= bounds.Min.Z && position.Z < bounds.Max.Z)
            {
                return true;
            }
        }

        return false;
    }

    private static CanonicalPlacement CanonicalizePlacement(
        VegetationScatterBakeDescriptor descriptor,
        in WorldPosition surfacePosition)
    {
        WorldPosition position = CanonicalizeWorldPosition(surfacePosition);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            WorldCellCoordinate owner = WorldPartitionCoordinates.GetCoordinate(
                descriptor.Partition,
                position);
            WorldPosition ownerOrigin = CanonicalizeWorldPosition(
                WorldPartitionCoordinates.GetCellOrigin(descriptor.Partition, owner));
            Vector3 localPosition = ToFiniteLocalPosition(
                descriptor,
                position,
                ownerOrigin);
            WorldPosition canonicalWorldPosition = CanonicalizeWorldPosition(new WorldPosition(
                ownerOrigin.X + localPosition.X,
                ownerOrigin.Y + localPosition.Y,
                ownerOrigin.Z + localPosition.Z));
            WorldCellCoordinate canonicalOwner = WorldPartitionCoordinates.GetCoordinate(
                descriptor.Partition,
                canonicalWorldPosition);
            if (canonicalOwner == owner)
            {
                return new CanonicalPlacement(
                    canonicalWorldPosition,
                    owner,
                    localPosition);
            }

            position = canonicalWorldPosition;
        }

        throw Invalid(
            descriptor,
            $"surface position {surfacePosition} did not converge to stable world-cell ownership");
    }

    private static int[] ApplySpacing(
        VegetationScatterBakeDescriptor descriptor,
        CookedVegetationBiomeEntry entry,
        ScatterRegion region,
        CandidateEvaluation[] eligible,
        int eligibleCount,
        int acceptedLimit,
        out int acceptedCount,
        out int spacingRejected,
        out int ownershipRejected)
    {
        double bucketSize = entry.MinimumSpacing;
        var bucketHeads = new Dictionary<SpacingBucket, int>(
            Math.Min(eligibleCount, VegetationBiomeSourceAssetLoader.MaximumClusterSize * 2));
        int[] nextCandidate = new int[eligibleCount];
        var acceptedOwned = new int[Math.Min(eligibleCount, acceptedLimit)];
        acceptedCount = 0;
        spacingRejected = 0;
        ownershipRejected = 0;
        double minimumDistanceSquared = entry.MinimumSpacing * entry.MinimumSpacing;

        for (int candidateIndex = 0; candidateIndex < eligibleCount; candidateIndex++)
        {
            CandidateEvaluation candidate = eligible[candidateIndex];
            SpacingBucket bucket = GetSpacingBucket(region, candidate.WorldPosition, bucketSize);
            bool blocked = false;
            for (int offsetZ = -1; offsetZ <= 1 && !blocked; offsetZ++)
            {
                for (int offsetX = -1; offsetX <= 1 && !blocked; offsetX++)
                {
                    var neighbor = new SpacingBucket(
                        checked(bucket.X + offsetX),
                        checked(bucket.Z + offsetZ));
                    int otherIndex = bucketHeads.TryGetValue(neighbor, out int head)
                        ? head
                        : -1;
                    while (otherIndex >= 0)
                    {
                        CandidateEvaluation other = eligible[otherIndex];
                        double deltaX = candidate.WorldPosition.X - other.WorldPosition.X;
                        double deltaZ = candidate.WorldPosition.Z - other.WorldPosition.Z;
                        if ((deltaX * deltaX) + (deltaZ * deltaZ) < minimumDistanceSquared)
                        {
                            blocked = true;
                            break;
                        }

                        otherIndex = nextCandidate[otherIndex];
                    }
                }
            }

            nextCandidate[candidateIndex] = bucketHeads.TryGetValue(bucket, out int previous)
                ? previous
                : -1;
            bucketHeads[bucket] = candidateIndex;
            if (blocked)
            {
                spacingRejected++;
                continue;
            }

            if (candidate.Owner == descriptor.Cell.Coordinate)
            {
                if (acceptedCount >= acceptedLimit)
                {
                    throw Invalid(
                        descriptor,
                        $"accepted instance count '{acceptedCount + 1}' exceeds the cluster limit " +
                        $"'{acceptedLimit}' for entry '{entry.EntryId}'");
                }

                acceptedOwned[acceptedCount++] = candidateIndex;
            }
            else
            {
                ownershipRejected++;
            }
        }

        return acceptedOwned;
    }

    private static SpacingBucket GetSpacingBucket(
        in ScatterRegion region,
        in WorldPosition position,
        double bucketSize) => new(
            CheckedFloor((position.X - region.MinimumX) / bucketSize),
            CheckedFloor((position.Z - region.MinimumZ) / bucketSize));

    private static VegetationCookedInstanceInput[] BuildInstances(
        VegetationScatterBakeDescriptor descriptor,
        CookedVegetationSpecies species,
        IReadOnlyList<CandidateEvaluation> eligible,
        IReadOnlyList<int> acceptedIndices,
        int acceptedCount)
    {
        var instances = new VegetationCookedInstanceInput[acceptedCount];
        for (int index = 0; index < acceptedCount; index++)
        {
            CandidateEvaluation evaluation = eligible[acceptedIndices[index]];
            if (evaluation.Owner != descriptor.Cell.Coordinate)
            {
                throw Invalid(descriptor, "accepted placement has mismatched world-cell ownership");
            }

            float scale = Lerp(species.ScaleRange, evaluation.ScaleRank);
            float yaw = DegreesToRadians(Lerp(species.YawRangeDegrees, evaluation.YawRank));
            float tilt = DegreesToRadians(Lerp(species.TiltRangeDegrees, evaluation.TiltRank));
            Quaternion orientation = Quaternion.Normalize(
                Quaternion.CreateFromYawPitchRoll(yaw, tilt, 0.0f));
            float conservativeRadius = (float)(
                (double)descriptor.UnscaledConservativeRadius * scale);
            if (!float.IsFinite(scale) || scale <= 0.0f ||
                !float.IsFinite(conservativeRadius) || conservativeRadius <= 0.0f ||
                !IsFinite(orientation))
            {
                throw Invalid(descriptor, $"instance '{index}' transform or radius overflowed");
            }

            var input = new VegetationCookedInstanceInput(
                evaluation.StableKey,
                SpeciesIndex: 0,
                evaluation.OwnerLocalPosition,
                orientation,
                scale,
                conservativeRadius);
            try
            {
                instances[index] = VegetationInstancePageAssetCooker.CanonicalizeInstanceInput(
                    input,
                    "vegetation scatter placement",
                    $"instance {index}");
            }
            catch (Exception error) when (error is InvalidDataException or ArgumentException)
            {
                throw Invalid(descriptor, $"instance '{index}' is not page-canonical: {error.Message}");
            }
        }

        return instances;
    }

    private static byte[] ComputePlacementContentHash(
        VegetationScatterBakeDescriptor descriptor,
        CookedVegetationBiomeEntry entry,
        WorldPosition pageOrigin,
        IReadOnlyList<VegetationCookedInstanceInput> instances)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(s_PlacementDomain);
        Span<byte> identity = stackalloc byte[124];
        descriptor.WorldGuid.TryWriteBytes(identity, bigEndian: true, out _);
        descriptor.Biome.Guid.TryWriteBytes(identity[16..], bigEndian: true, out _);
        descriptor.TerrainRoot.Guid.TryWriteBytes(identity[32..], bigEndian: true, out _);
        descriptor.Species.Guid.TryWriteBytes(identity[48..], bigEndian: true, out _);
        WorldCellId cellId = WorldCellIdentity.Create(
            descriptor.WorldGuid,
            descriptor.Cell.Coordinate,
            descriptor.Cell.Layer);
        cellId.Value.TryWriteBytes(identity[64..], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32LittleEndian(identity[80..], instances.Count);
        BinaryPrimitives.WriteDoubleLittleEndian(identity[84..], pageOrigin.X);
        BinaryPrimitives.WriteDoubleLittleEndian(identity[92..], pageOrigin.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(identity[100..], pageOrigin.Z);
        BinaryPrimitives.WriteUInt64LittleEndian(identity[108..], descriptor.Biome.GlobalSeed);
        BinaryPrimitives.WriteUInt64LittleEndian(identity[116..], entry.SeedSalt);
        hash.AppendData(identity);
        AppendCanonicalText(hash, descriptor.Biome.PackageId);
        AppendCanonicalText(hash, descriptor.TerrainRoot.PackageId);
        AppendCanonicalText(hash, descriptor.Species.PackageId);
        AppendCanonicalText(hash, entry.EntryId);

        Span<byte> encoded = stackalloc byte[VegetationInstancePageAssetCooker.InstanceStride];
        for (int index = 0; index < instances.Count; index++)
        {
            VegetationCookedInstanceInput instance = instances[index];
            VegetationInstancePageAssetCooker.WriteCanonicalInstanceRecord(
                encoded,
                instance,
                "vegetation scatter placement hash",
                $"instance {index} orientation");
            hash.AppendData(encoded);
        }

        return hash.GetHashAndReset();
    }

    private static void AppendCanonicalText(IncrementalHash hash, string value)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        Span<byte> bytes = stackalloc byte[value.Length];
        Encoding.ASCII.GetBytes(value, bytes);
        hash.AppendData(bytes);
    }

    private static Vector3 ToFiniteLocalPosition(
        VegetationScatterBakeDescriptor descriptor,
        in WorldPosition position,
        in WorldPosition origin)
    {
        var result = new Vector3(
            CanonicalizeZero((float)(position.X - origin.X)),
            CanonicalizeZero((float)(position.Y - origin.Y)),
            CanonicalizeZero((float)(position.Z - origin.Z)));
        if (!float.IsFinite(result.X) ||
            !float.IsFinite(result.Y) ||
            !float.IsFinite(result.Z))
        {
            throw Invalid(descriptor, $"position {position} is outside page-local float range");
        }

        return result;
    }

    private static WorldPosition CanonicalizeWorldPosition(in WorldPosition value)
    {
        var result = new WorldPosition(
            CanonicalizeZero(value.X),
            CanonicalizeZero(value.Y),
            CanonicalizeZero(value.Z));
        if (!result.IsFinite)
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] World position {value} is non-finite.");
        }

        return result;
    }

    private static double CanonicalizeZero(double value) => value == 0.0 ? 0.0 : value;

    private static float CanonicalizeZero(float value) => value == 0.0f ? 0.0f : value;

    private static float GetWeight(Vector4 weights, int index) => index switch
    {
        0 => weights.X,
        1 => weights.Y,
        2 => weights.Z,
        3 => weights.W,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private static float Lerp(VegetationValueRange range, double amount) =>
        (float)((double)range.Minimum + (((double)range.Maximum - range.Minimum) * amount));

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180.0f);

    private static bool IsRange(VegetationValueRange range, float minimum, float maximum) =>
        float.IsFinite(range.Minimum) &&
        float.IsFinite(range.Maximum) &&
        range.Minimum >= minimum &&
        range.Maximum <= maximum &&
        range.Minimum <= range.Maximum;

    private static bool IsAsciiIdentifier(string value, int maximumLength) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '.' or '-' or '_');

    private static bool IsFinite(in Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    private static long CheckedFloor(double value)
    {
        double result = Math.Floor(value);
        if (!double.IsFinite(result) || result < long.MinValue || result > long.MaxValue)
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] Spatial coordinate '{value}' is outside Int64 range.");
        }

        return (long)result;
    }

    private static long CheckedCeiling(double value)
    {
        double result = Math.Ceiling(value);
        if (!double.IsFinite(result) || result < long.MinValue || result > long.MaxValue)
        {
            throw new InvalidOperationException(
                $"[VegetationScatterBaker] Spatial coordinate '{value}' is outside Int64 range.");
        }

        return (long)result;
    }

    private static InvalidOperationException Invalid(
        VegetationScatterBakeDescriptor descriptor,
        string diagnostic)
    {
        return new InvalidOperationException(
            $"[VegetationScatterBaker] Biome '{descriptor.Biome.Guid:D}' entry " +
            $"'{descriptor.EntryId}' cell ({descriptor.Cell.Coordinate.X}, " +
            $"{descriptor.Cell.Coordinate.Y}, {descriptor.Cell.Coordinate.Z}) is invalid: " +
            $"{diagnostic}.");
    }

    private readonly record struct ScatterRegion(
        double MinimumX,
        double MinimumZ,
        double MaximumX,
        double MaximumZ);

    private readonly record struct CandidateRange(
        long MinimumX,
        long MinimumZ,
        long MaximumXExclusive,
        long MaximumZExclusive,
        int OrdinalsPerSpatialCell,
        int MaximumCandidateCount);

    private readonly record struct CanonicalPlacement(
        WorldPosition WorldPosition,
        WorldCellCoordinate Owner,
        Vector3 OwnerLocalPosition);

    private readonly record struct SpacingBucket(long X, long Z);

    private readonly record struct CandidateEvaluation(
        ulong StableKey,
        double ScaleRank,
        double YawRank,
        double TiltRank,
        WorldPosition WorldPosition,
        WorldCellCoordinate Owner,
        Vector3 OwnerLocalPosition);

    private sealed class CandidateEvaluationComparer : IComparer<CandidateEvaluation>
    {
        public static CandidateEvaluationComparer Instance { get; } = new();

        public int Compare(CandidateEvaluation left, CandidateEvaluation right) =>
            left.StableKey.CompareTo(right.StableKey);
    }
}

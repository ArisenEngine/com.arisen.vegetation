using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Vegetation.Assets;

internal sealed record VegetationClusterCookPreparation(
    CookedVegetationCluster Cluster,
    byte[] ClusterPayload,
    IReadOnlyList<CookedVegetationInstancePage> Pages,
    IReadOnlyList<byte[]> PagePayloads);

internal enum CookedVegetationClusterSectionType : uint
{
    Metadata = 1,
    Strings = 2,
    Species = 3,
    Pages = 4,
    SpatialNodes = 5,
    SpeciesAcceleration = 6
}

public static class VegetationClusterAssetCooker
{
    public const string RuntimeVariant = "runtime.vegetation-cluster.v1";
    public const string CookedExtension = ".arivegetationcluster";
    public const int CookedFormatVersion = 1;

    internal const int HeaderSize = 128;
    internal const int HashOffset = 64;
    internal const int MetadataStride = 128;
    internal const int SpeciesStride = 24;
    internal const int PageStride = 144;
    internal const int SpatialNodeStride = 72;
    internal const int SpeciesAccelerationStride = 96;
    internal const int MinimumPageCount = 1;
    internal const int MaximumPageCount = 4_096;
    internal const int MaximumSpeciesCount =
        VegetationInstancePageAssetCooker.MaximumSpeciesCount;
    internal const int CurrentGeneratedSchemaVersion = 1;

    private const int MaxCookedClusterBytes = 4 * 1024 * 1024;
    private const int MaxStringBytes = 16 * 1024;
    private const int MaxStringCount = 2 + MaximumSpeciesCount + MaximumPageCount;
    internal const int MaximumTotalInstanceCount = 1024 * 1024;

    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIVCLUS");
    private static readonly HashSet<uint> s_KnownSections =
    [
        (uint)CookedVegetationClusterSectionType.Metadata,
        (uint)CookedVegetationClusterSectionType.Strings,
        (uint)CookedVegetationClusterSectionType.Species,
        (uint)CookedVegetationClusterSectionType.Pages,
        (uint)CookedVegetationClusterSectionType.SpatialNodes,
        (uint)CookedVegetationClusterSectionType.SpeciesAcceleration
    ];

    public static CookedVegetationClusterArtifact Cook(
        IAssetDatabase assetDatabase,
        VegetationClusterCookDescriptor descriptor)
    {
        using var zone = Profiler.Zone("Vegetation.CookCluster");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(descriptor);

        VegetationClusterCookPreparation preparation = Prepare(assetDatabase, descriptor);
        var preparedPages = new PreparedPage[preparation.Pages.Count];
        for (int index = 0; index < preparation.Pages.Count; index++)
        {
            preparedPages[index] = new PreparedPage(
                preparation.Pages[index],
                preparation.Cluster.Pages[index]);
        }

        lock (VegetationInstancePageAssetCooker.PublicationGate)
        {
            // Recheck every immutable child while publication is serialized, before any commit.
            for (int index = 0; index < preparedPages.Length; index++)
            {
                PreparedPage prepared = preparedPages[index];
                byte[] payload = preparation.PagePayloads[index];
                VegetationInstancePageAssetCooker.ValidatePublicationCompatibility(
                    assetDatabase,
                    prepared.Page,
                    payload);
                if (payload.LongLength != prepared.Reference.SizeInBytes ||
                    !SHA256.HashData(payload).AsSpan().SequenceEqual(prepared.Reference.ContentHash))
                {
                    throw new InvalidOperationException(
                        $"[VegetationClusterAssetCooker] Page '{prepared.Page.Guid:D}' changed " +
                        "after cluster preflight.");
                }
            }

            // Child artifacts must be durable and exact before the root can publish their hashes.
            foreach (PreparedPage prepared in preparedPages)
            {
                CookedVegetationInstancePageArtifact artifact =
                    VegetationInstancePageAssetCooker.Cook(assetDatabase, prepared.Page);
                ValidatePublishedPage(assetDatabase, prepared, artifact);
            }

            return PublishCluster(
                assetDatabase,
                preparation.Cluster,
                preparation.ClusterPayload);
        }
    }

    internal static VegetationClusterCookPreparation Prepare(
        IAssetDatabase assetDatabase,
        VegetationClusterCookDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptorOwnership(assetDatabase, descriptor);

        var builtPages = new CookedVegetationInstancePage[descriptor.Pages.Count];
        for (int index = 0; index < descriptor.Pages.Count; index++)
        {
            VegetationInstancePageCookDescriptor pageDescriptor = descriptor.Pages[index] ??
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Cluster '{descriptor.Guid:D}' page descriptor " +
                    $"'{index}' is null.");
            builtPages[index] = VegetationInstancePageAssetCooker.BuildForCook(
                assetDatabase,
                pageDescriptor);
        }

        Array.Sort(builtPages, ComparePages);
        ValidateBuiltPages(assetDatabase, descriptor, builtPages);
        CookedVegetationSpeciesReference[] species = BuildCanonicalSpecies(builtPages);
        ValidateAuthoredBiomeSpecies(
            assetDatabase,
            descriptor.Guid,
            descriptor.Biome,
            species);
        WorldBounds bounds = ComputeBounds(builtPages);
        int instanceCount = CountInstancesAndValidateStableKeys(builtPages);
        var pageReferences = new CookedVegetationInstancePageReference[builtPages.Length];
        var pagePayloads = new byte[builtPages.Length][];
        for (int index = 0; index < builtPages.Length; index++)
        {
            CookedVegetationInstancePage page = builtPages[index];
            byte[] pagePayload = VegetationInstancePageAssetCooker.WritePayload(page);
            pagePayloads[index] = pagePayload;
            pageReferences[index] = new CookedVegetationInstancePageReference(
                page.Guid,
                page.PackageId,
                page.Instances.Count,
                page.Origin,
                page.Bounds,
                pagePayload.LongLength,
                SHA256.HashData(pagePayload));
        }

        var cluster = new CookedVegetationCluster(
            descriptor.Guid,
            descriptor.PackageId,
            descriptor.GeneratedSchemaVersion,
            descriptor.Biome,
            bounds,
            Array.AsReadOnly(species),
            Array.AsReadOnly(pageReferences),
            instanceCount);
        cluster = cluster with
        {
            Acceleration = BuildAcceleration(
                cluster,
                builtPages,
                descriptor.SpeciesData)
        };
        return new VegetationClusterCookPreparation(
            cluster,
            WritePayload(cluster),
            Array.AsReadOnly(builtPages),
            Array.AsReadOnly(pagePayloads));
    }

    internal static CookedVegetationClusterArtifact Cook(
        IAssetDatabase assetDatabase,
        CookedVegetationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(cluster);
        byte[] payload = WritePayload(cluster);
        return PublishCluster(assetDatabase, cluster, payload);
    }

    private static CookedVegetationClusterArtifact PublishCluster(
        IAssetDatabase assetDatabase,
        CookedVegetationCluster cluster,
        byte[] payload)
    {
        lock (VegetationInstancePageAssetCooker.PublicationGate)
        {
            using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
                cluster.Guid,
                RuntimeVariant,
                CookedExtension);
            VegetationCookedContainer.WriteAtomicallyIfChanged(write.OutputPath, payload);
            CookedAssetRecord output = write.Commit(VegetationAssetTypes.Cluster);
            if (output.SizeInBytes != payload.LongLength)
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Published cluster '{cluster.Guid:D}' exact byte " +
                    "size does not match the prevalidated payload.");
            }

            VegetationCookedAssetDependency[] dependencies = BuildDependencies(cluster);
            return new CookedVegetationClusterArtifact(
                cluster.Guid,
                cluster.PackageId,
                RuntimeVariant,
                output.Path,
                cluster.Pages.Count,
                cluster.InstanceCount,
                cluster.Bounds,
                output.SizeInBytes,
                Array.AsReadOnly(dependencies));
        }
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationClusterSourceAsset> clusterRef,
        out CookedVegetationCluster cluster,
        out string diagnostic)
    {
        return TryLoadCookedCore(
            assetDatabase,
            clusterRef,
            validateAuthoredAvailability: true,
            out cluster,
            out diagnostic);
    }

    internal static bool TryLoadCookedForRuntimeCooking(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationClusterSourceAsset> clusterRef,
        out CookedVegetationCluster cluster,
        out string diagnostic)
    {
        return TryLoadCookedCore(
            assetDatabase,
            clusterRef,
            validateAuthoredAvailability: false,
            out cluster,
            out diagnostic);
    }

    private static bool TryLoadCookedCore(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationClusterSourceAsset> clusterRef,
        bool validateAuthoredAvailability,
        out CookedVegetationCluster cluster,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        cluster = null!;
        if (!clusterRef.IsValid)
        {
            diagnostic = "[VegetationClusterAssetCooker] Cooked vegetation cluster ref is empty.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(clusterRef.AssetType) &&
            !string.Equals(
                clusterRef.AssetType,
                VegetationAssetTypes.Cluster,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationClusterAssetCooker] Vegetation cluster '{clusterRef.Guid:D}' ref has asset type " +
                $"'{clusterRef.AssetType}', expected '{VegetationAssetTypes.Cluster}'.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(clusterRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(
                descriptor.AssetType,
                VegetationAssetTypes.Cluster,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationClusterAssetCooker] Vegetation cluster '{clusterRef.Guid:D}' has no cataloged " +
                $"'{VegetationAssetTypes.Cluster}' identity.";
            return false;
        }

        if (!IsCanonicalPackageId(descriptor.PackageId) ||
            (!string.IsNullOrEmpty(clusterRef.PackageId) &&
             !string.Equals(clusterRef.PackageId, descriptor.PackageId, StringComparison.Ordinal)))
        {
            diagnostic =
                $"[VegetationClusterAssetCooker] Vegetation cluster '{clusterRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{clusterRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                clusterRef.Guid,
                RuntimeVariant,
                VegetationAssetTypes.Cluster,
                out CookedAssetHandle handle))
        {
            diagnostic =
                $"[VegetationClusterAssetCooker] Vegetation cluster '{clusterRef.Guid:D}' variant " +
                $"'{RuntimeVariant}' is unavailable.";
            return false;
        }

        try
        {
            string diagnosticPath = assetDatabase.TryGetCookedArtifact(
                clusterRef.Guid,
                RuntimeVariant,
                out CookedAssetRecord? artifact)
                ? artifact.Path
                : $"{clusterRef.Guid:D}:{RuntimeVariant}";
            if (!TryReadPayload(
                    clusterRef.Guid,
                    descriptor.PackageId,
                    assetDatabase.GetCookedAssetBytes(handle).Span,
                    diagnosticPath,
                    out cluster,
                    out diagnostic))
            {
                return false;
            }

            try
            {
                ValidateLoadedDependencies(
                    assetDatabase,
                    cluster,
                    validateAuthoredAvailability);
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                cluster = null!;
                diagnostic =
                    $"[VegetationClusterAssetCooker] Cooked vegetation cluster '{diagnosticPath}' " +
                    $"has an invalid dependency: {ex.Message}";
                return false;
            }
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static byte[] WritePayload(CookedVegetationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        cluster = cluster with
        {
            Acceleration = cluster.Acceleration ?? BuildAcceleration(cluster, null, null)
        };
        ValidateCluster(cluster, $"vegetation cluster '{cluster.Guid:D}'");
        string[] strings = BuildCanonicalStrings(cluster);
        Dictionary<string, uint> stringIndices = BuildStringIndices(strings);
        byte[] metadata = BuildMetadataSection(cluster, stringIndices);
        byte[] species = BuildSpeciesSection(cluster, stringIndices);
        byte[] pages = BuildPageSection(cluster, stringIndices);
        byte[] spatialNodes = BuildSpatialNodeSection(cluster.Acceleration!, $"vegetation cluster '{cluster.Guid:D}'");
        byte[] speciesAcceleration = BuildSpeciesAccelerationSection(
            cluster.Acceleration!,
            $"vegetation cluster '{cluster.Guid:D}'");
        VegetationCookedSectionPayload[] sections =
        [
            new(
                (uint)CookedVegetationClusterSectionType.Metadata,
                VegetationCookedSectionFlags.Required,
                1,
                MetadataStride,
                metadata),
            new(
                (uint)CookedVegetationClusterSectionType.Strings,
                VegetationCookedSectionFlags.Required,
                checked((uint)strings.Length),
                0,
                VegetationCookedContainer.BuildStringSection(strings)),
            new(
                (uint)CookedVegetationClusterSectionType.Species,
                VegetationCookedSectionFlags.Required,
                checked((uint)cluster.Species.Count),
                SpeciesStride,
                species),
            new(
                (uint)CookedVegetationClusterSectionType.Pages,
                VegetationCookedSectionFlags.Required,
                checked((uint)cluster.Pages.Count),
                PageStride,
                pages),
            new(
                (uint)CookedVegetationClusterSectionType.SpatialNodes,
                VegetationCookedSectionFlags.None,
                checked((uint)cluster.Acceleration!.SpatialNodes.Count),
                SpatialNodeStride,
                spatialNodes),
            new(
                (uint)CookedVegetationClusterSectionType.SpeciesAcceleration,
                VegetationCookedSectionFlags.None,
                checked((uint)cluster.Acceleration.Species.Count),
                SpeciesAccelerationStride,
                speciesAcceleration)
        ];

        byte[] output = VegetationCookedContainer.Build(
            HeaderSize,
            MaxCookedClusterBytes,
            sections,
            out _);
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], VegetationCookedContainer.EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], cluster.GeneratedSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderSize);
        VegetationCookedContainer.WriteGuid(header[24..40], cluster.Guid);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], cluster.Pages.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[44..], cluster.Species.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..], cluster.InstanceCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[52..], sections.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[56..], checked((ulong)output.Length));
        VegetationCookedContainer.FinalizeHash(output, HeaderSize, HashOffset);
        return output;
    }

    internal static bool TryReadPayload(
        Guid expectedClusterGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out CookedVegetationCluster cluster,
        out string diagnostic)
    {
        using var zone = Profiler.Zone("Vegetation.ReadClusterPayload");
        try
        {
            cluster = ReadPayload(
                expectedClusterGuid,
                RequireCanonicalPackageId(expectedPackageId, "expected cluster package id"),
                bytes,
                diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or OverflowException or ArgumentException)
        {
            cluster = null!;
            diagnostic = ex.Message;
            return false;
        }
    }

    internal static VegetationCookedAssetDependency[] BuildDependencies(
        CookedVegetationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ValidateCluster(cluster, $"vegetation cluster '{cluster.Guid:D}'");
        var dependencies = new Dictionary<DependencyKey, VegetationCookedAssetDependency>();
        AddDependency(
            dependencies,
            new VegetationCookedAssetDependency(
                cluster.Biome.Guid,
                cluster.Biome.PackageId,
                VegetationAssetTypes.Biome,
                VegetationBiomeAssetCooker.RuntimeVariant,
                true));
        foreach (CookedVegetationSpeciesReference species in cluster.Species)
        {
            AddDependency(
                dependencies,
                new VegetationCookedAssetDependency(
                    species.Guid,
                    species.PackageId,
                    VegetationAssetTypes.Species,
                    VegetationSpeciesAssetCooker.RuntimeVariant,
                    true));
        }

        foreach (CookedVegetationInstancePageReference page in cluster.Pages)
        {
            AddDependency(
                dependencies,
                new VegetationCookedAssetDependency(
                    page.Guid,
                    page.PackageId,
                    VegetationAssetTypes.InstancePage,
                    VegetationInstancePageAssetCooker.RuntimeVariant,
                    true));
        }

        VegetationCookedAssetDependency[] result = dependencies.Values.ToArray();
        Array.Sort(result, CompareDependencies);
        return result;
    }

    private static CookedVegetationCluster ReadPayload(
        Guid expectedClusterGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        string context = $"vegetation cluster '{diagnosticPath}'";
        if (bytes.Length < HeaderSize)
        {
            throw VegetationCookedContainer.Invalid(context, "header is truncated");
        }

        if (!bytes[..8].SequenceEqual(s_Magic))
        {
            throw VegetationCookedContainer.Invalid(context, "magic is not ARIVCLUS");
        }

        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int generatedSchemaVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        Guid clusterGuid = VegetationCookedContainer.ReadGuid(bytes[24..40]);
        int pageCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        int speciesCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        int instanceCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[52..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]);
        if (endian != VegetationCookedContainer.EndianMarker ||
            formatVersion != CookedFormatVersion ||
            generatedSchemaVersion != CurrentGeneratedSchemaVersion ||
            headerSize != HeaderSize ||
            clusterGuid == Guid.Empty || clusterGuid != expectedClusterGuid ||
            pageCount is < MinimumPageCount or > MaximumPageCount ||
            speciesCount is <= 0 or > MaximumSpeciesCount ||
            instanceCount is <= 0 or > MaximumTotalInstanceCount ||
            declaredSize != checked((ulong)bytes.Length))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "header identity, version, counts, or exact size is invalid");
        }

        VegetationCookedContainer.EnsureZero(bytes[96..HeaderSize], context, "reserved header bytes");
        Dictionary<uint, VegetationCookedSectionDescriptor> sections =
            VegetationCookedContainer.ReadDirectory(
                bytes,
                HeaderSize,
                HashOffset,
                sectionCount,
                MaxCookedClusterBytes,
                s_KnownSections,
                context);
        VegetationCookedSectionDescriptor metadataSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationClusterSectionType.Metadata,
            MetadataStride,
            1,
            1,
            context);
        VegetationCookedSectionDescriptor stringsSection =
            VegetationCookedContainer.RequireVariableSection(
                sections,
                (uint)CookedVegetationClusterSectionType.Strings,
                MaxStringCount,
                context);
        VegetationCookedSectionDescriptor speciesSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationClusterSectionType.Species,
            SpeciesStride,
            checked((uint)speciesCount),
            MaximumSpeciesCount,
            context);
        VegetationCookedSectionDescriptor pagesSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationClusterSectionType.Pages,
            PageStride,
            checked((uint)pageCount),
            MaximumPageCount,
            context);
        VegetationCookedSectionDescriptor? spatialNodesSection = null;
        VegetationCookedSectionDescriptor? speciesAccelerationSection = null;
        if (sections.TryGetValue(
                (uint)CookedVegetationClusterSectionType.SpatialNodes,
                out VegetationCookedSectionDescriptor encodedSpatialNodes))
        {
            if (encodedSpatialNodes.Flags != VegetationCookedSectionFlags.None ||
                encodedSpatialNodes.Stride != SpatialNodeStride ||
                encodedSpatialNodes.Count is < 1 or > (MaximumPageCount * 2 - 1))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "optional spatial-node section has invalid flags, stride, or count");
            }

            spatialNodesSection = encodedSpatialNodes;
        }

        if (sections.TryGetValue(
                (uint)CookedVegetationClusterSectionType.SpeciesAcceleration,
                out VegetationCookedSectionDescriptor encodedSpeciesAcceleration))
        {
            if (encodedSpeciesAcceleration.Flags != VegetationCookedSectionFlags.None ||
                encodedSpeciesAcceleration.Stride != SpeciesAccelerationStride ||
                encodedSpeciesAcceleration.Count != checked((uint)speciesCount))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "optional species-acceleration section has invalid flags, stride, or count");
            }

            speciesAccelerationSection = encodedSpeciesAcceleration;
        }

        if (spatialNodesSection.HasValue != speciesAccelerationSection.HasValue)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "spatial-node and species-acceleration sections must be published together");
        }

        string[] strings = VegetationCookedContainer.ReadStrings(
            bytes,
            stringsSection,
            MaxStringBytes,
            context);
        ReadOnlySpan<byte> metadata = VegetationCookedContainer.GetSection(bytes, metadataSection);
        Guid metadataClusterGuid = VegetationCookedContainer.ReadGuid(metadata[..16]);
        string packageId = VegetationCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[16..]),
            "cluster package id",
            context);
        string biomePackageId = VegetationCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[20..]),
            "biome package id",
            context);
        Guid biomeGuid = VegetationCookedContainer.ReadGuid(metadata[24..40]);
        var bounds = new WorldBounds(
            ReadCanonicalPosition(metadata[40..64], context, "cluster minimum bounds"),
            ReadCanonicalPosition(metadata[64..88], context, "cluster maximum bounds"));
        uint metadataInstanceCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[88..]);
        uint metadataSpeciesCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[92..]);
        uint metadataPageCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[96..]);
        int metadataGeneratedSchemaVersion = BinaryPrimitives.ReadInt32LittleEndian(metadata[100..]);
        VegetationCookedContainer.EnsureZero(metadata[104..], context, "metadata reserved bytes");
        if (metadataClusterGuid != clusterGuid ||
            biomeGuid == Guid.Empty ||
            metadataInstanceCount != checked((uint)instanceCount) ||
            metadataSpeciesCount != checked((uint)speciesCount) ||
            metadataPageCount != checked((uint)pageCount) ||
            metadataGeneratedSchemaVersion != generatedSchemaVersion)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "metadata identity, counts, or generated schema do not match the header");
        }

        ReadOnlySpan<byte> encodedSpecies = VegetationCookedContainer.GetSection(bytes, speciesSection);
        var species = new CookedVegetationSpeciesReference[speciesCount];
        for (int index = 0; index < species.Length; index++)
        {
            ReadOnlySpan<byte> record = encodedSpecies.Slice(index * SpeciesStride, SpeciesStride);
            species[index] = new CookedVegetationSpeciesReference(
                VegetationCookedContainer.ReadGuid(record[..16]),
                VegetationCookedContainer.ReadString(
                    strings,
                    BinaryPrimitives.ReadUInt32LittleEndian(record[16..]),
                    $"species {index} package id",
                    context));
            if (BinaryPrimitives.ReadUInt32LittleEndian(record[20..]) != 0)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species '{index}' reserved data is nonzero");
            }
        }

        ReadOnlySpan<byte> encodedPages = VegetationCookedContainer.GetSection(bytes, pagesSection);
        var pages = new CookedVegetationInstancePageReference[pageCount];
        for (int index = 0; index < pages.Length; index++)
        {
            ReadOnlySpan<byte> record = encodedPages.Slice(index * PageStride, PageStride);
            Guid pageGuid = VegetationCookedContainer.ReadGuid(record[..16]);
            string pagePackageId = VegetationCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record[16..]),
                $"page {index} package id",
                context);
            int pageInstanceCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record[20..]));
            WorldPosition origin = ReadCanonicalPosition(
                record[24..48],
                context,
                $"page {index} origin");
            var pageBounds = new WorldBounds(
                ReadCanonicalPosition(record[48..72], context, $"page {index} minimum bounds"),
                ReadCanonicalPosition(record[72..96], context, $"page {index} maximum bounds"));
            ulong rawSize = BinaryPrimitives.ReadUInt64LittleEndian(record[96..]);
            if (rawSize > long.MaxValue)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"page '{index}' exact byte size is outside the signed runtime range");
            }

            byte[] contentHash = record[104..136].ToArray();
            VegetationCookedContainer.EnsureZero(
                record[136..],
                context,
                $"page {index} reserved bytes");
            pages[index] = new CookedVegetationInstancePageReference(
                pageGuid,
                pagePackageId,
                pageInstanceCount,
                origin,
                pageBounds,
                checked((long)rawSize),
                contentHash);
        }

        var cluster = new CookedVegetationCluster(
            clusterGuid,
            packageId,
            generatedSchemaVersion,
            new CookedVegetationBiomeReference(biomeGuid, biomePackageId),
            bounds,
            Array.AsReadOnly(species),
            Array.AsReadOnly(pages),
            instanceCount);
        if (spatialNodesSection.HasValue)
        {
            ReadOnlySpan<byte> spatialNodeBytes = VegetationCookedContainer.GetSection(
                bytes,
                spatialNodesSection.Value);
            var spatialNodes = new CookedVegetationSpatialNode[
                checked((int)spatialNodesSection.Value.Count)];
            for (int index = 0; index < spatialNodes.Length; index++)
            {
                ReadOnlySpan<byte> record = spatialNodeBytes.Slice(
                    index * SpatialNodeStride,
                    SpatialNodeStride);
                spatialNodes[index] = new CookedVegetationSpatialNode(
                    ReadBounds(record[..48], context, $"spatial node {index} bounds"),
                    BinaryPrimitives.ReadInt32LittleEndian(record[48..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[52..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[56..]),
                    BinaryPrimitives.ReadInt32LittleEndian(record[60..]));
                VegetationCookedContainer.EnsureZero(
                    record[64..],
                    context,
                    $"spatial node {index} reserved bytes");
            }

            ReadOnlySpan<byte> speciesAccelerationBytes = VegetationCookedContainer.GetSection(
                bytes,
                speciesAccelerationSection!.Value);
            var speciesAcceleration = new CookedVegetationSpeciesAcceleration[speciesCount];
            for (int index = 0; index < speciesAcceleration.Length; index++)
            {
                ReadOnlySpan<byte> record = speciesAccelerationBytes.Slice(
                    index * SpeciesAccelerationStride,
                    SpeciesAccelerationStride);
                uint speciesIndex = BinaryPrimitives.ReadUInt32LittleEndian(record);
                uint rawFirstPage = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
                uint rawLastPage = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
                uint rawPageCount = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
                uint rawInstanceCount = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
                if (speciesIndex != (uint)index ||
                    rawFirstPage > int.MaxValue ||
                    rawLastPage > int.MaxValue ||
                    rawPageCount > int.MaxValue ||
                    rawInstanceCount > int.MaxValue)
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"species acceleration '{index}' identity or count is invalid");
                }

                speciesAcceleration[index] = new CookedVegetationSpeciesAcceleration(
                    species[index],
                    checked((int)rawFirstPage),
                    checked((int)rawLastPage),
                    checked((int)rawPageCount),
                    checked((int)rawInstanceCount),
                    ReadBounds(record[20..68], context, $"species acceleration {index} bounds"),
                    ReadCanonicalNonNegativeFloat(
                        record[68..72],
                        context,
                        $"species acceleration {index} maximum radius"),
                    ReadCanonicalNonNegativeFloat(
                        record[72..76],
                        context,
                        $"species acceleration {index} maximum LOD distance"),
                    ReadCanonicalNonNegativeFloat(
                        record[76..80],
                        context,
                        $"species acceleration {index} maximum LOD screen error"));
                VegetationCookedContainer.EnsureZero(
                    record[80..],
                    context,
                    $"species acceleration {index} reserved bytes");
            }

            cluster = cluster with
            {
                Acceleration = new CookedVegetationClusterAcceleration(
                    bounds,
                    Array.AsReadOnly(spatialNodes),
                    Array.AsReadOnly(speciesAcceleration))
            };
        }
        ValidateCluster(cluster, context);
        if (!string.Equals(cluster.PackageId, expectedPackageId, StringComparison.Ordinal))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"package identity '{cluster.PackageId}' does not match expected '{expectedPackageId}'");
        }

        ValidateCanonicalStrings(strings, BuildCanonicalStrings(cluster), context);
        return cluster;
    }

    private static byte[] BuildMetadataSection(
        CookedVegetationCluster cluster,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[MetadataStride];
        VegetationCookedContainer.WriteGuid(bytes.AsSpan(0, 16), cluster.Guid);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), stringIndices[cluster.PackageId]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), stringIndices[cluster.Biome.PackageId]);
        VegetationCookedContainer.WriteGuid(bytes.AsSpan(24, 16), cluster.Biome.Guid);
        WritePosition(bytes.AsSpan(40, 24), cluster.Bounds.Min);
        WritePosition(bytes.AsSpan(64, 24), cluster.Bounds.Max);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88), checked((uint)cluster.InstanceCount));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), checked((uint)cluster.Species.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), checked((uint)cluster.Pages.Count));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(100), cluster.GeneratedSchemaVersion);
        return bytes;
    }

    private static byte[] BuildSpeciesSection(
        CookedVegetationCluster cluster,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[checked(cluster.Species.Count * SpeciesStride)];
        for (int index = 0; index < cluster.Species.Count; index++)
        {
            CookedVegetationSpeciesReference species = cluster.Species[index];
            Span<byte> record = bytes.AsSpan(index * SpeciesStride, SpeciesStride);
            VegetationCookedContainer.WriteGuid(record[..16], species.Guid);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], stringIndices[species.PackageId]);
        }

        return bytes;
    }

    private static byte[] BuildPageSection(
        CookedVegetationCluster cluster,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[checked(cluster.Pages.Count * PageStride)];
        for (int index = 0; index < cluster.Pages.Count; index++)
        {
            CookedVegetationInstancePageReference page = cluster.Pages[index];
            Span<byte> record = bytes.AsSpan(index * PageStride, PageStride);
            VegetationCookedContainer.WriteGuid(record[..16], page.Guid);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], stringIndices[page.PackageId]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[20..], checked((uint)page.InstanceCount));
            WritePosition(record[24..48], page.Origin);
            WritePosition(record[48..72], page.Bounds.Min);
            WritePosition(record[72..96], page.Bounds.Max);
            BinaryPrimitives.WriteUInt64LittleEndian(record[96..], checked((ulong)page.SizeInBytes));
            page.ContentHash.CopyTo(record[104..136]);
        }

        return bytes;
    }

    private static CookedVegetationClusterAcceleration BuildAcceleration(
        CookedVegetationCluster cluster,
        IReadOnlyList<CookedVegetationInstancePage>? pages,
        IReadOnlyList<CookedVegetationSpecies>? speciesData)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        string context = $"vegetation cluster '{cluster.Guid:D}' acceleration";
        CookedVegetationSpatialNode[] spatialNodes = BuildSpatialNodes(cluster.Pages, context);
        var species = new CookedVegetationSpeciesAcceleration[cluster.Species.Count];
        var speciesIndices = new Dictionary<SpeciesIdentity, int>();
        for (int index = 0; index < cluster.Species.Count; index++)
        {
            CookedVegetationSpeciesReference reference = cluster.Species[index];
            speciesIndices.Add(new SpeciesIdentity(reference.Guid, reference.PackageId), index);
            species[index] = new CookedVegetationSpeciesAcceleration(
                reference,
                int.MaxValue,
                -1,
                0,
                0,
                cluster.Bounds,
                0.0f,
                0.0f,
                0.0f);
        }

        var pageSets = new HashSet<int>[cluster.Species.Count];
        var pageBounds = new WorldBounds?[cluster.Species.Count];
        var maximumRadius = new float[cluster.Species.Count];
        for (int index = 0; index < pageSets.Length; index++)
        {
            pageSets[index] = [];
        }

        if (pages == null)
        {
            for (int speciesIndex = 0; speciesIndex < pageSets.Length; speciesIndex++)
            {
                for (int pageIndex = 0; pageIndex < cluster.Pages.Count; pageIndex++)
                {
                    pageSets[speciesIndex].Add(pageIndex);
                }
            }
        }

        if (pages != null)
        {
            if (pages.Count != cluster.Pages.Count)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "built page count does not match the cluster page references");
            }

            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                CookedVegetationInstancePage page = pages[pageIndex];
                if (page.Guid != cluster.Pages[pageIndex].Guid)
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"built page '{page.Guid:D}' does not match canonical page index '{pageIndex}'");
                }

                CookedVegetationInstancePageAcceleration pageAcceleration =
                    page.Acceleration ?? VegetationInstancePageAssetCooker.BuildAcceleration(page);
                for (int rangeIndex = 0; rangeIndex < pageAcceleration.SpeciesRanges.Count; rangeIndex++)
                {
                    CookedVegetationSpeciesInstanceRange range =
                        pageAcceleration.SpeciesRanges[rangeIndex];
                    CookedVegetationSpeciesReference pageSpecies = page.Species[checked((int)range.SpeciesIndex)];
                    if (!speciesIndices.TryGetValue(
                            new SpeciesIdentity(pageSpecies.Guid, pageSpecies.PackageId),
                            out int clusterSpeciesIndex))
                    {
                        throw VegetationCookedContainer.Invalid(
                            context,
                            $"page '{page.Guid:D}' references species outside the cluster table");
                    }

                    pageSets[clusterSpeciesIndex].Add(pageIndex);
                    species[clusterSpeciesIndex] = species[clusterSpeciesIndex] with
                    {
                        InstanceCount = checked(
                            species[clusterSpeciesIndex].InstanceCount + range.InstanceCount),
                        Bounds = pageBounds[clusterSpeciesIndex].HasValue
                            ? Union(pageBounds[clusterSpeciesIndex]!.Value, range.Bounds)
                            : range.Bounds
                    };
                    pageBounds[clusterSpeciesIndex] = species[clusterSpeciesIndex].Bounds;
                    maximumRadius[clusterSpeciesIndex] = Math.Max(
                        maximumRadius[clusterSpeciesIndex],
                        range.MaximumConservativeRadius);
                }
            }
        }

        for (int index = 0; index < species.Length; index++)
        {
            HashSet<int> coveredPages = pageSets[index];
            if (coveredPages.Count == 0)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species '{index}' has no page coverage");
            }

            int firstPage = coveredPages.Min();
            int lastPage = coveredPages.Max();
            CookedVegetationSpeciesAcceleration current = species[index];
            species[index] = current with
            {
                FirstPageIndex = firstPage,
                LastPageIndex = lastPage,
                PageCount = coveredPages.Count,
                Bounds = pageBounds[index] ?? cluster.Bounds,
                MaximumConservativeRadius = pages == null
                    ? 0.0f
                    : CanonicalizePositive(maximumRadius[index])
            };
        }

        if (speciesData != null)
        {
            var suppliedSpecies = new HashSet<SpeciesIdentity>();
            foreach (CookedVegetationSpecies speciesDescriptor in speciesData)
            {
                if (speciesDescriptor == null ||
                    !suppliedSpecies.Add(new SpeciesIdentity(
                        speciesDescriptor.Guid,
                        speciesDescriptor.PackageId)) ||
                    !speciesIndices.TryGetValue(
                        new SpeciesIdentity(speciesDescriptor.Guid, speciesDescriptor.PackageId),
                        out int speciesIndex))
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        "species LOD data does not match the cluster species table");
                }

                if (speciesDescriptor.Lods == null || speciesDescriptor.Lods.Count == 0)
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"species '{speciesIndex}' has no LOD descriptors");
                }

                CookedVegetationSpeciesLod lod = speciesDescriptor.Lods[^1];
                species[speciesIndex] = species[speciesIndex] with
                {
                    MaximumLodDistance = CanonicalizeNonNegative(lod.MaximumDistance),
                    MaximumLodScreenError = CanonicalizeNonNegative(lod.MaximumScreenError)
                };
            }

            if (suppliedSpecies.Count != cluster.Species.Count)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "species LOD data does not cover every cluster species");
            }
        }

        var acceleration = new CookedVegetationClusterAcceleration(
            cluster.Bounds,
            Array.AsReadOnly(spatialNodes),
            Array.AsReadOnly(species));
        ValidateAcceleration(cluster, acceleration, context);
        return acceleration;
    }

    private static CookedVegetationSpatialNode[] BuildSpatialNodes(
        IReadOnlyList<CookedVegetationInstancePageReference> pages,
        string context)
    {
        var pageIndices = Enumerable.Range(0, pages.Count).ToArray();
        var nodes = new List<CookedVegetationSpatialNode>(checked((pages.Count * 2) - 1));
        BuildSpatialNodeRange(pageIndices, 0, pageIndices.Length, pages, nodes, context);
        return nodes.ToArray();
    }

    private static int BuildSpatialNodeRange(
        int[] pageIndices,
        int start,
        int count,
        IReadOnlyList<CookedVegetationInstancePageReference> pages,
        List<CookedVegetationSpatialNode> nodes,
        string context)
    {
        int nodeIndex = nodes.Count;
        nodes.Add(default);
        if (count == 1)
        {
            int pageIndex = pageIndices[start];
            nodes[nodeIndex] = new CookedVegetationSpatialNode(
                pages[pageIndex].Bounds,
                -1,
                -1,
                pageIndex,
                1);
            return nodeIndex;
        }

        WorldBounds bounds = pages[pageIndices[start]].Bounds;
        for (int index = 1; index < count; index++)
        {
            bounds = Union(bounds, pages[pageIndices[start + index]].Bounds);
        }

        double spreadX = bounds.Max.X - bounds.Min.X;
        double spreadY = bounds.Max.Y - bounds.Min.Y;
        double spreadZ = bounds.Max.Z - bounds.Min.Z;
        int axis = spreadY > spreadX ? 1 : 0;
        if ((axis == 1 ? spreadY : spreadX) < spreadZ)
        {
            axis = 2;
        }

        for (int index = 1; index < count; index++)
        {
            int value = pageIndices[start + index];
            int cursor = index - 1;
            while (cursor >= 0 && CompareSpatialPages(
                       value,
                       pageIndices[start + cursor],
                       pages,
                       axis) < 0)
            {
                pageIndices[start + cursor + 1] = pageIndices[start + cursor];
                cursor--;
            }

            pageIndices[start + cursor + 1] = value;
        }

        int leftCount = count / 2;
        int left = BuildSpatialNodeRange(
            pageIndices,
            start,
            leftCount,
            pages,
            nodes,
            context);
        int right = BuildSpatialNodeRange(
            pageIndices,
            start + leftCount,
            count - leftCount,
            pages,
            nodes,
            context);
        nodes[nodeIndex] = new CookedVegetationSpatialNode(
            bounds,
            left,
            right,
            -1,
            count);
        return nodeIndex;
    }

    private static int CompareSpatialPages(
        int leftIndex,
        int rightIndex,
        IReadOnlyList<CookedVegetationInstancePageReference> pages,
        int axis)
    {
        CookedVegetationInstancePageReference left = pages[leftIndex];
        CookedVegetationInstancePageReference right = pages[rightIndex];
        double leftCenter = axis switch
        {
            1 => (left.Bounds.Min.Y + left.Bounds.Max.Y) * 0.5,
            2 => (left.Bounds.Min.Z + left.Bounds.Max.Z) * 0.5,
            _ => (left.Bounds.Min.X + left.Bounds.Max.X) * 0.5
        };
        double rightCenter = axis switch
        {
            1 => (right.Bounds.Min.Y + right.Bounds.Max.Y) * 0.5,
            2 => (right.Bounds.Min.Z + right.Bounds.Max.Z) * 0.5,
            _ => (right.Bounds.Min.X + right.Bounds.Max.X) * 0.5
        };
        int result = leftCenter.CompareTo(rightCenter);
        return result != 0 ? result : ComparePageReferences(left, right);
    }

    private static byte[] BuildSpatialNodeSection(
        CookedVegetationClusterAcceleration acceleration,
        string context)
    {
        byte[] bytes = new byte[checked(acceleration.SpatialNodes.Count * SpatialNodeStride)];
        for (int index = 0; index < acceleration.SpatialNodes.Count; index++)
        {
            CookedVegetationSpatialNode node = acceleration.SpatialNodes[index];
            Span<byte> record = bytes.AsSpan(index * SpatialNodeStride, SpatialNodeStride);
            WriteBounds(record[..48], node.Bounds);
            BinaryPrimitives.WriteInt32LittleEndian(record[48..], node.LeftChildIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[52..], node.RightChildIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[56..], node.FirstPageIndex);
            BinaryPrimitives.WriteInt32LittleEndian(record[60..], node.PageCount);
            VegetationCookedContainer.EnsureZero(record[64..], context, $"spatial node {index} reserved bytes");
        }

        return bytes;
    }

    private static byte[] BuildSpeciesAccelerationSection(
        CookedVegetationClusterAcceleration acceleration,
        string context)
    {
        byte[] bytes = new byte[checked(acceleration.Species.Count * SpeciesAccelerationStride)];
        for (int index = 0; index < acceleration.Species.Count; index++)
        {
            CookedVegetationSpeciesAcceleration species = acceleration.Species[index];
            Span<byte> record = bytes.AsSpan(index * SpeciesAccelerationStride, SpeciesAccelerationStride);
            BinaryPrimitives.WriteUInt32LittleEndian(record, checked((uint)index));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], checked((uint)species.FirstPageIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], checked((uint)species.LastPageIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], checked((uint)species.PageCount));
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], checked((uint)species.InstanceCount));
            WriteBounds(record[20..68], species.Bounds);
            WriteCanonicalFloat(record[68..], species.MaximumConservativeRadius);
            WriteCanonicalFloat(record[72..], species.MaximumLodDistance);
            WriteCanonicalFloat(record[76..], species.MaximumLodScreenError);
            VegetationCookedContainer.EnsureZero(record[80..], context, $"species acceleration {index} reserved bytes");
        }

        return bytes;
    }

    private static void ValidateDescriptorOwnership(
        IAssetDatabase assetDatabase,
        VegetationClusterCookDescriptor descriptor)
    {
        if (descriptor.Guid == Guid.Empty ||
            !IsCanonicalPackageId(descriptor.PackageId) ||
            descriptor.GeneratedSchemaVersion != CurrentGeneratedSchemaVersion ||
            descriptor.Biome.Guid == Guid.Empty ||
            !IsCanonicalPackageId(descriptor.Biome.PackageId) ||
            descriptor.Pages == null ||
            descriptor.Pages.Count is < MinimumPageCount or > MaximumPageCount)
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Cluster '{descriptor.Guid:D}' has invalid identity, " +
                "generated schema, biome, package, or page count.");
        }

        ValidateDatabaseAsset(
            assetDatabase,
            descriptor.Guid,
            descriptor.PackageId,
            VegetationAssetTypes.Cluster,
            $"cluster '{descriptor.Guid:D}'");
        ValidateDatabaseAsset(
            assetDatabase,
            descriptor.Biome.Guid,
            descriptor.Biome.PackageId,
            VegetationAssetTypes.Biome,
            $"cluster '{descriptor.Guid:D}' biome");

        var pageGuids = new HashSet<Guid>();
        for (int pageIndex = 0; pageIndex < descriptor.Pages.Count; pageIndex++)
        {
            VegetationInstancePageCookDescriptor page = descriptor.Pages[pageIndex] ??
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Cluster '{descriptor.Guid:D}' page descriptor " +
                    $"'{pageIndex}' is null.");
            if (page.Guid == Guid.Empty ||
                page.ClusterGuid != descriptor.Guid ||
                !IsCanonicalPackageId(page.PackageId) ||
                !string.Equals(page.PackageId, descriptor.PackageId, StringComparison.Ordinal) ||
                page.GeneratedSchemaVersion != descriptor.GeneratedSchemaVersion ||
                !pageGuids.Add(page.Guid) ||
                page.Species == null ||
                page.Species.Count <= 0)
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Cluster '{descriptor.Guid:D}' page '{pageIndex}' " +
                    "has invalid, duplicate, or mismatched identity/package/schema/species data.");
            }

            ValidateDatabaseAsset(
                assetDatabase,
                page.Guid,
                page.PackageId,
                VegetationAssetTypes.InstancePage,
                $"cluster '{descriptor.Guid:D}' page '{page.Guid:D}'");
            var speciesGuids = new HashSet<Guid>();
            foreach (CookedVegetationSpeciesReference species in page.Species)
            {
                if (species.Guid == Guid.Empty ||
                    !IsCanonicalPackageId(species.PackageId) ||
                    !speciesGuids.Add(species.Guid))
                {
                    throw new InvalidOperationException(
                        $"[VegetationClusterAssetCooker] Cluster '{descriptor.Guid:D}' page " +
                        $"'{page.Guid:D}' has invalid or duplicate species identity '{species.Guid:D}'.");
                }

                ValidateDatabaseAsset(
                    assetDatabase,
                    species.Guid,
                    species.PackageId,
                    VegetationAssetTypes.Species,
                    $"cluster '{descriptor.Guid:D}' page '{page.Guid:D}' species");
            }
        }
    }

    internal static void ValidateAuthoredBiomeSpecies(
        IAssetDatabase assetDatabase,
        CookedVegetationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ValidateAuthoredBiomeSpecies(
            assetDatabase,
            cluster.Guid,
            cluster.Biome,
            cluster.Species);
    }

    private static void ValidateAuthoredBiomeSpecies(
        IAssetDatabase assetDatabase,
        Guid clusterGuid,
        CookedVegetationBiomeReference biomeReference,
        IReadOnlyList<CookedVegetationSpeciesReference> clusterSpecies)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        VegetationBiomeSourceDescriptor biome = VegetationBiomeSourceAssetLoader.LoadSource(
            assetDatabase,
            biomeReference.ToAssetRef());
        var biomeSpecies = new HashSet<SpeciesIdentity>();
        foreach (VegetationBiomeEntryDescriptor entry in biome.Entries)
        {
            biomeSpecies.Add(new SpeciesIdentity(
                entry.Species.Guid,
                entry.Species.PackageId));
        }

        foreach (CookedVegetationSpeciesReference species in clusterSpecies)
        {
            if (!biomeSpecies.Contains(new SpeciesIdentity(species.Guid, species.PackageId)))
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Cluster '{clusterGuid:D}' species " +
                    $"'{species.PackageId}:{species.Guid:D}' is not declared by authored biome " +
                    $"'{biomeReference.PackageId}:{biomeReference.Guid:D}'.");
            }
        }
    }

    private static void ValidateBuiltPages(
        IAssetDatabase assetDatabase,
        VegetationClusterCookDescriptor descriptor,
        IReadOnlyList<CookedVegetationInstancePage> pages)
    {
        CookedVegetationInstancePage? previous = null;
        foreach (CookedVegetationInstancePage page in pages)
        {
            if (page.Guid == Guid.Empty ||
                page.ClusterGuid != descriptor.Guid ||
                page.GeneratedSchemaVersion != descriptor.GeneratedSchemaVersion ||
                !IsCanonicalPackageId(page.PackageId) ||
                !string.Equals(page.PackageId, descriptor.PackageId, StringComparison.Ordinal) ||
                page.Instances == null ||
                page.Instances.Count <= 0 ||
                page.Species == null ||
                page.Species.Count <= 0 ||
                !page.Origin.IsFinite ||
                !page.Bounds.IsValid ||
                (previous != null && ComparePages(previous, page) >= 0))
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Built page '{page.Guid:D}' has invalid or " +
                    "noncanonical cluster, schema, package, count, origin, bounds, or order.");
            }

            ValidateDatabaseAsset(
                assetDatabase,
                page.Guid,
                page.PackageId,
                VegetationAssetTypes.InstancePage,
                $"built cluster page '{page.Guid:D}'");
            foreach (CookedVegetationSpeciesReference species in page.Species)
            {
                ValidateDatabaseAsset(
                    assetDatabase,
                    species.Guid,
                    species.PackageId,
                    VegetationAssetTypes.Species,
                    $"built cluster page '{page.Guid:D}' species");
            }

            previous = page;
        }
    }

    private static void ValidateCluster(CookedVegetationCluster cluster, string context)
    {
        if (cluster.Guid == Guid.Empty ||
            !IsCanonicalPackageId(cluster.PackageId) ||
            cluster.GeneratedSchemaVersion != CurrentGeneratedSchemaVersion ||
            cluster.Biome.Guid == Guid.Empty ||
            !IsCanonicalPackageId(cluster.Biome.PackageId) ||
            !cluster.Bounds.IsValid ||
            !IsCanonicalBounds(cluster.Bounds) ||
            cluster.Species == null ||
            cluster.Species.Count is <= 0 or > MaximumSpeciesCount ||
            cluster.Pages == null ||
            cluster.Pages.Count is < MinimumPageCount or > MaximumPageCount ||
            cluster.InstanceCount is <= 0 or > MaximumTotalInstanceCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "identity, package, generated schema, biome, bounds, or counts are invalid");
        }

        var speciesGuids = new HashSet<Guid>();
        CookedVegetationSpeciesReference? previousSpecies = null;
        foreach (CookedVegetationSpeciesReference species in cluster.Species)
        {
            if (species.Guid == Guid.Empty ||
                !IsCanonicalPackageId(species.PackageId) ||
                !speciesGuids.Add(species.Guid) ||
                (previousSpecies.HasValue && CompareSpecies(previousSpecies.Value, species) >= 0))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "species identities are invalid, duplicate, or not in canonical package/GUID order");
            }

            previousSpecies = species;
        }

        var pageGuids = new HashSet<Guid>();
        CookedVegetationInstancePageReference? previousPage = null;
        long totalInstances = 0;
        foreach (CookedVegetationInstancePageReference page in cluster.Pages)
        {
            if (page.Guid == Guid.Empty ||
                !IsCanonicalPackageId(page.PackageId) ||
                !string.Equals(page.PackageId, cluster.PackageId, StringComparison.Ordinal) ||
                !pageGuids.Add(page.Guid) ||
                page.InstanceCount is <= 0 or > VegetationInstancePageAssetCooker.MaximumInstanceCount ||
                !IsCanonicalPosition(page.Origin) ||
                !page.Bounds.IsValid ||
                !IsCanonicalBounds(page.Bounds) ||
                page.SizeInBytes is < VegetationInstancePageAssetCooker.HeaderSize or
                    > VegetationInstancePageAssetCooker.MaxCookedPageBytes ||
                page.ContentHash == null ||
                page.ContentHash.Length != VegetationCookedContainer.HashSize ||
                page.ContentHash.All(static value => value == 0) ||
                (previousPage != null && ComparePageReferences(previousPage, page) >= 0))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "page identities, counts, origins, bounds, byte sizes, hashes, or order are invalid");
            }

            totalInstances = checked(totalInstances + page.InstanceCount);
            if (totalInstances > MaximumTotalInstanceCount)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"total instance count exceeds '{MaximumTotalInstanceCount}'");
            }

            previousPage = page;
        }

        if (totalInstances != cluster.InstanceCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "page instance counts do not equal the cluster instance count");
        }

        WorldBounds computedBounds = ComputeReferenceBounds(cluster.Pages);
        if (!BoundsBitwiseEqual(computedBounds, cluster.Bounds))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "cluster bounds do not exactly equal the union of page bounds");
        }

        if (cluster.Acceleration != null)
        {
            ValidateAcceleration(cluster, cluster.Acceleration, context);
        }
    }

    private static void ValidateAcceleration(
        CookedVegetationCluster cluster,
        CookedVegetationClusterAcceleration acceleration,
        string context)
    {
        if (!acceleration.Bounds.IsValid ||
            !IsCanonicalBounds(acceleration.Bounds) ||
            !BoundsBitwiseEqual(acceleration.Bounds, cluster.Bounds) ||
            acceleration.SpatialNodes == null ||
            acceleration.SpatialNodes.Count is < 1 or > (MaximumPageCount * 2 - 1) ||
            acceleration.Species == null ||
            acceleration.Species.Count != cluster.Species.Count)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "acceleration bounds, node count, or species count is invalid");
        }

        bool[] coveredPages = new bool[cluster.Pages.Count];
        bool[] referencedNodes = new bool[acceleration.SpatialNodes.Count];
        referencedNodes[0] = true;
        for (int nodeIndex = 0; nodeIndex < acceleration.SpatialNodes.Count; nodeIndex++)
        {
            CookedVegetationSpatialNode node = acceleration.SpatialNodes[nodeIndex];
            if (!node.Bounds.IsValid ||
                !IsCanonicalBounds(node.Bounds) ||
                !ContainsBounds(cluster.Bounds, node.Bounds) ||
                node.PageCount <= 0 ||
                node.PageCount > cluster.Pages.Count)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"spatial node '{nodeIndex}' bounds or page count is invalid");
            }

            if (node.IsLeaf)
            {
                if (node.FirstPageIndex < 0 ||
                    node.FirstPageIndex >= cluster.Pages.Count ||
                    node.PageCount != 1 ||
                    !BoundsBitwiseEqual(
                        node.Bounds,
                        cluster.Pages[node.FirstPageIndex].Bounds) ||
                    coveredPages[node.FirstPageIndex])
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"spatial leaf '{nodeIndex}' does not exactly cover one page");
                }

                coveredPages[node.FirstPageIndex] = true;
                continue;
            }

            if (node.LeftChildIndex <= nodeIndex ||
                node.LeftChildIndex >= acceleration.SpatialNodes.Count ||
                node.RightChildIndex <= nodeIndex ||
                node.RightChildIndex >= acceleration.SpatialNodes.Count ||
                node.FirstPageIndex != -1 ||
                node.PageCount < 2 ||
                referencedNodes[node.LeftChildIndex] ||
                referencedNodes[node.RightChildIndex])
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"spatial node '{nodeIndex}' child or range identity is invalid");
            }

            referencedNodes[node.LeftChildIndex] = true;
            referencedNodes[node.RightChildIndex] = true;

            CookedVegetationSpatialNode left = acceleration.SpatialNodes[node.LeftChildIndex];
            CookedVegetationSpatialNode right = acceleration.SpatialNodes[node.RightChildIndex];
            WorldBounds expectedBounds = Union(left.Bounds, right.Bounds);
            if (!BoundsBitwiseEqual(node.Bounds, expectedBounds) ||
                node.PageCount != left.PageCount + right.PageCount)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"spatial node '{nodeIndex}' bounds or page range is not conservative");
            }
        }

        if (coveredPages.Any(covered => !covered))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "spatial hierarchy does not cover every cluster page exactly once");
        }

        if (referencedNodes.Any(referenced => !referenced))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "spatial hierarchy contains an unreachable node");
        }

        CookedVegetationSpeciesAcceleration? previousSpecies = null;
        for (int speciesIndex = 0; speciesIndex < acceleration.Species.Count; speciesIndex++)
        {
            CookedVegetationSpeciesAcceleration species = acceleration.Species[speciesIndex];
            CookedVegetationSpeciesReference expectedReference = cluster.Species[speciesIndex];
            if (species == null ||
                !species.Species.Equals(expectedReference) ||
                (previousSpecies != null &&
                 CompareSpecies(previousSpecies.Species, species.Species) >= 0) ||
                species.FirstPageIndex < 0 ||
                species.LastPageIndex < species.FirstPageIndex ||
                species.LastPageIndex >= cluster.Pages.Count ||
                species.PageCount <= 0 ||
                species.PageCount > cluster.Pages.Count ||
                species.InstanceCount < 0 ||
                species.InstanceCount > cluster.InstanceCount ||
                !species.Bounds.IsValid ||
                !IsCanonicalBounds(species.Bounds) ||
                !ContainsBounds(cluster.Bounds, species.Bounds) ||
                !IsCanonicalNonNegative(species.MaximumConservativeRadius) ||
                !IsCanonicalNonNegative(species.MaximumLodDistance) ||
                !IsCanonicalNonNegative(species.MaximumLodScreenError))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species acceleration '{speciesIndex}' is invalid");
            }

            previousSpecies = species;
        }
    }

    private static void ValidateLoadedDependencies(
        IAssetDatabase assetDatabase,
        CookedVegetationCluster cluster,
        bool validateAuthoredAvailability)
    {
        ValidateDatabaseAsset(
            assetDatabase,
            cluster.Biome.Guid,
            cluster.Biome.PackageId,
            VegetationAssetTypes.Biome,
            $"cluster '{cluster.Guid:D}' biome");

        HashSet<SpeciesIdentity>? biomeSpecies = null;
        if (validateAuthoredAvailability)
        {
            if (!VegetationBiomeAssetCooker.TryLoadCooked(
                    assetDatabase,
                    cluster.Biome.ToAssetRef(),
                    out CookedVegetationBiome biome,
                    out string biomeDiagnostic))
            {
                throw new InvalidOperationException(biomeDiagnostic);
            }

            biomeSpecies = new HashSet<SpeciesIdentity>();
            foreach (CookedVegetationBiomeEntry entry in biome.Entries)
            {
                biomeSpecies.Add(new SpeciesIdentity(entry.Species.Guid, entry.Species.PackageId));
            }
        }

        foreach (CookedVegetationSpeciesReference species in cluster.Species)
        {
            if (biomeSpecies != null &&
                !biomeSpecies.Contains(new SpeciesIdentity(species.Guid, species.PackageId)))
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Cluster '{cluster.Guid:D}' species " +
                    $"'{species.PackageId}:{species.Guid:D}' is not declared by biome " +
                    $"'{cluster.Biome.PackageId}:{cluster.Biome.Guid:D}'.");
            }

            ValidateDatabaseAsset(
                assetDatabase,
                species.Guid,
                species.PackageId,
                VegetationAssetTypes.Species,
                $"cluster '{cluster.Guid:D}' species");
            if (validateAuthoredAvailability &&
                !VegetationSpeciesAssetCooker.TryLoadCooked(
                    assetDatabase,
                    species.ToAssetRef(),
                    out _,
                    out string speciesDiagnostic))
            {
                throw new InvalidOperationException(speciesDiagnostic);
            }
        }

        var loadedPages = new CookedVegetationInstancePage[cluster.Pages.Count];
        for (int index = 0; index < cluster.Pages.Count; index++)
        {
            CookedVegetationInstancePageReference reference = cluster.Pages[index];
            ValidateDatabaseAsset(
                assetDatabase,
                reference.Guid,
                reference.PackageId,
                VegetationAssetTypes.InstancePage,
                $"cluster '{cluster.Guid:D}' page");
            if (!VegetationInstancePageAssetCooker.TryLoadCookedForCluster(
                    assetDatabase,
                    new AssetRef<VegetationInstancePageSourceAsset>(
                        reference.Guid,
                        VegetationAssetTypes.InstancePage,
                        reference.PackageId),
                    cluster.Guid,
                    out CookedVegetationInstancePage page,
                    out VegetationInstancePagePayloadIdentity payloadIdentity,
                    out string pageDiagnostic))
            {
                throw new InvalidOperationException(pageDiagnostic);
            }

            ValidateLoadedPageReference(reference, page, payloadIdentity);
            if (page.GeneratedSchemaVersion != cluster.GeneratedSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Loaded page '{page.Guid:D}' generated schema " +
                    $"'{page.GeneratedSchemaVersion}' does not match cluster schema " +
                    $"'{cluster.GeneratedSchemaVersion}'.");
            }

            loadedPages[index] = page;
        }

        CookedVegetationSpeciesReference[] loadedSpecies = BuildCanonicalSpecies(loadedPages);
        if (!loadedSpecies.SequenceEqual(cluster.Species))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Cluster '{cluster.Guid:D}' species table does not " +
                "exactly equal the canonical union of its pages.");
        }

        int instanceCount = CountInstancesAndValidateStableKeys(loadedPages);
        if (instanceCount != cluster.InstanceCount ||
            !BoundsBitwiseEqual(ComputeBounds(loadedPages), cluster.Bounds))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Cluster '{cluster.Guid:D}' loaded page counts or " +
                "bounds do not match the root.");
        }
    }

    private static void ValidatePublishedPage(
        IAssetDatabase assetDatabase,
        PreparedPage prepared,
        CookedVegetationInstancePageArtifact artifact)
    {
        CookedVegetationInstancePageReference expected = prepared.Reference;
        if (artifact.PageGuid != expected.Guid ||
            artifact.ClusterGuid != prepared.Page.ClusterGuid ||
            !string.Equals(artifact.PackageId, expected.PackageId, StringComparison.Ordinal) ||
            !string.Equals(
                artifact.Variant,
                VegetationInstancePageAssetCooker.RuntimeVariant,
                StringComparison.Ordinal) ||
            artifact.InstanceCount != expected.InstanceCount ||
            !PositionBitwiseEqual(artifact.Origin, expected.Origin) ||
            !BoundsBitwiseEqual(artifact.Bounds, expected.Bounds) ||
            artifact.SizeInBytes != expected.SizeInBytes ||
            !artifact.ContentHash.AsSpan().SequenceEqual(expected.ContentHash) ||
            !artifact.Species.SequenceEqual(prepared.Page.Species))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Published page '{expected.Guid:D}' does not match " +
                "the prevalidated in-memory payload.");
        }

        ValidateExactPageArtifactBytes(assetDatabase, expected, expectedPayload: null);
    }

    private static void ValidateLoadedPageReference(
        CookedVegetationInstancePageReference reference,
        CookedVegetationInstancePage page,
        VegetationInstancePagePayloadIdentity payloadIdentity)
    {
        if (page.Guid != reference.Guid ||
            !string.Equals(page.PackageId, reference.PackageId, StringComparison.Ordinal) ||
            page.Instances.Count != reference.InstanceCount ||
            !PositionBitwiseEqual(page.Origin, reference.Origin) ||
            !BoundsBitwiseEqual(page.Bounds, reference.Bounds))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Loaded page '{reference.Guid:D}' does not match " +
                "the cluster reference.");
        }

        if (!payloadIdentity.IsValid ||
            payloadIdentity.PageGuid != reference.Guid ||
            payloadIdentity.SizeInBytes != reference.SizeInBytes ||
            !payloadIdentity.ContentHash.SequenceEqual(reference.ContentHash))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Loaded page '{reference.Guid:D}' byte size or " +
                "full-file SHA-256 does not match the cluster reference.");
        }
    }

    private static void ValidateExactPageArtifactBytes(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePageReference reference,
        byte[]? expectedPayload)
    {
        if (!assetDatabase.TryGetCookedArtifact(
                reference.Guid,
                VegetationInstancePageAssetCooker.RuntimeVariant,
                out CookedAssetRecord artifact) ||
            !string.Equals(
                artifact.AssetType,
                VegetationAssetTypes.InstancePage,
                StringComparison.OrdinalIgnoreCase) ||
            artifact.SizeInBytes != reference.SizeInBytes)
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Page '{reference.Guid:D}' has no exact registered " +
                "instance-page artifact.");
        }

        if (!assetDatabase.TryLoadCookedAsset(
                reference.Guid,
                VegetationInstancePageAssetCooker.RuntimeVariant,
                VegetationAssetTypes.InstancePage,
                out CookedAssetHandle handle))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Page '{reference.Guid:D}' cannot be loaded for " +
                "size/hash validation.");
        }

        try
        {
            ReadOnlySpan<byte> bytes = assetDatabase.GetCookedAssetBytes(handle).Span;
            if (bytes.Length != reference.SizeInBytes ||
                (expectedPayload != null && !bytes.SequenceEqual(expectedPayload)) ||
                !SHA256.HashData(bytes).AsSpan().SequenceEqual(reference.ContentHash))
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Page '{reference.Guid:D}' byte size, payload, " +
                    "or full-file SHA-256 does not match the cluster reference.");
            }
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    private static CookedVegetationSpeciesReference[] BuildCanonicalSpecies(
        IReadOnlyList<CookedVegetationInstancePage> pages)
    {
        var species = new SortedSet<CookedVegetationSpeciesReference>(SpeciesComparer.Instance);
        var packageByGuid = new Dictionary<Guid, string>();
        foreach (CookedVegetationInstancePage page in pages)
        {
            foreach (CookedVegetationSpeciesReference reference in page.Species)
            {
                if (packageByGuid.TryGetValue(reference.Guid, out string? packageId) &&
                    !string.Equals(packageId, reference.PackageId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"[VegetationClusterAssetCooker] Species '{reference.Guid:D}' appears with " +
                        $"conflicting package owners '{packageId}' and '{reference.PackageId}'.");
                }

                packageByGuid.TryAdd(reference.Guid, reference.PackageId);
                species.Add(reference);
            }
        }

        if (species.Count is <= 0 or > MaximumSpeciesCount)
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] Canonical species count '{species.Count}' is outside " +
                $"1..{MaximumSpeciesCount}.");
        }

        return species.ToArray();
    }

    private static int CountInstancesAndValidateStableKeys(
        IReadOnlyList<CookedVegetationInstancePage> pages)
    {
        long total = 0;
        var cursors = new PriorityQueue<PageInstanceCursor, ulong>(pages.Count);
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            CookedVegetationInstancePage page = pages[pageIndex];
            total = checked(total + page.Instances.Count);
            if (total > MaximumTotalInstanceCount)
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Total instance count exceeds " +
                    $"'{MaximumTotalInstanceCount}'.");
            }

            if (page.Instances.Count > 0)
            {
                cursors.Enqueue(
                    new PageInstanceCursor(pageIndex, 0),
                    page.Instances[0].StableKey);
            }
        }

        bool hasPrevious = false;
        ulong previousKey = 0;
        while (cursors.TryDequeue(out PageInstanceCursor cursor, out ulong stableKey))
        {
            if (hasPrevious && stableKey == previousKey)
            {
                throw new InvalidOperationException(
                    $"[VegetationClusterAssetCooker] Stable instance key '{stableKey}' " +
                    "appears in more than one cluster page.");
            }

            hasPrevious = true;
            previousKey = stableKey;
            int nextInstanceIndex = checked(cursor.InstanceIndex + 1);
            CookedVegetationInstancePage page = pages[cursor.PageIndex];
            if (nextInstanceIndex < page.Instances.Count)
            {
                cursors.Enqueue(
                    new PageInstanceCursor(cursor.PageIndex, nextInstanceIndex),
                    page.Instances[nextInstanceIndex].StableKey);
            }
        }

        return checked((int)total);
    }

    private static WorldBounds ComputeBounds(IReadOnlyList<CookedVegetationInstancePage> pages)
    {
        if (pages.Count == 0)
        {
            throw new InvalidOperationException(
                "[VegetationClusterAssetCooker] Cannot compute bounds for an empty page set.");
        }

        WorldBounds bounds = pages[0].Bounds;
        for (int index = 1; index < pages.Count; index++)
        {
            bounds = Union(bounds, pages[index].Bounds);
        }

        return bounds;
    }

    private static WorldBounds ComputeReferenceBounds(
        IReadOnlyList<CookedVegetationInstancePageReference> pages)
    {
        WorldBounds bounds = pages[0].Bounds;
        for (int index = 1; index < pages.Count; index++)
        {
            bounds = Union(bounds, pages[index].Bounds);
        }

        return bounds;
    }

    private static WorldBounds Union(WorldBounds left, WorldBounds right) =>
        new(
            new WorldPosition(
                CanonicalizeZero(Math.Min(left.Min.X, right.Min.X)),
                CanonicalizeZero(Math.Min(left.Min.Y, right.Min.Y)),
                CanonicalizeZero(Math.Min(left.Min.Z, right.Min.Z))),
            new WorldPosition(
                CanonicalizeZero(Math.Max(left.Max.X, right.Max.X)),
                CanonicalizeZero(Math.Max(left.Max.Y, right.Max.Y)),
                CanonicalizeZero(Math.Max(left.Max.Z, right.Max.Z))));

    private static string[] BuildCanonicalStrings(CookedVegetationCluster cluster)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal)
        {
            cluster.PackageId,
            cluster.Biome.PackageId
        };
        foreach (CookedVegetationSpeciesReference species in cluster.Species)
        {
            strings.Add(species.PackageId);
        }

        foreach (CookedVegetationInstancePageReference page in cluster.Pages)
        {
            strings.Add(page.PackageId);
        }

        return strings.ToArray();
    }

    private static Dictionary<string, uint> BuildStringIndices(IReadOnlyList<string> strings)
    {
        var indices = new Dictionary<string, uint>(strings.Count, StringComparer.Ordinal);
        for (int index = 0; index < strings.Count; index++)
        {
            indices.Add(strings[index], checked((uint)index));
        }

        return indices;
    }

    private static void ValidateCanonicalStrings(
        IReadOnlyList<string> encoded,
        IReadOnlyList<string> expected,
        string context)
    {
        if (!encoded.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "string table contains missing, unused, duplicate, or noncanonical entries");
        }
    }

    private static void ValidateDatabaseAsset(
        IAssetDatabase assetDatabase,
        Guid guid,
        string packageId,
        string assetType,
        string context)
    {
        if (!assetDatabase.TryGetAssetDescriptor(guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, assetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(descriptor.PackageId, packageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationClusterAssetCooker] {context} '{guid:D}' must have exact package " +
                $"'{packageId}' and asset type '{assetType}' identity ownership.");
        }
    }

    private static void AddDependency(
        IDictionary<DependencyKey, VegetationCookedAssetDependency> dependencies,
        VegetationCookedAssetDependency dependency)
    {
        dependencies.TryAdd(
            new DependencyKey(
                dependency.Guid,
                dependency.PackageId,
                dependency.AssetType,
                dependency.Variant),
            dependency);
    }

    private static int CompareDependencies(
        VegetationCookedAssetDependency left,
        VegetationCookedAssetDependency right)
    {
        int result = left.Guid.CompareTo(right.Guid);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.AssetType, right.AssetType);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(left.Variant, right.Variant);
        return result != 0 ? result : left.Required.CompareTo(right.Required);
    }

    private static int ComparePages(
        CookedVegetationInstancePage? left,
        CookedVegetationInstancePage? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        int result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        return result != 0 ? result : left.Guid.CompareTo(right.Guid);
    }

    private static int ComparePageReferences(
        CookedVegetationInstancePageReference left,
        CookedVegetationInstancePageReference right)
    {
        int result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        return result != 0 ? result : left.Guid.CompareTo(right.Guid);
    }

    private static int CompareSpecies(
        CookedVegetationSpeciesReference left,
        CookedVegetationSpeciesReference right)
    {
        int result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        return result != 0 ? result : left.Guid.CompareTo(right.Guid);
    }

    private static void WritePosition(Span<byte> bytes, WorldPosition position)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, position.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[8..], position.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[16..], position.Z);
    }

    private static void WriteBounds(Span<byte> bytes, WorldBounds bounds)
    {
        WritePosition(bytes[..24], bounds.Min);
        WritePosition(bytes[24..48], bounds.Max);
    }

    private static WorldBounds ReadBounds(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        var bounds = new WorldBounds(
            ReadCanonicalPosition(bytes[..24], context, $"{field} minimum"),
            ReadCanonicalPosition(bytes[24..48], context, $"{field} maximum"));
        if (!bounds.IsValid)
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} is invalid");
        }

        return bounds;
    }

    private static float ReadCanonicalPositiveFloat(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        float value = ReadCanonicalNonNegativeFloat(bytes, context, field);
        if (value <= 0.0f)
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} is not positive");
        }

        return value;
    }

    private static float ReadCanonicalNonNegativeFloat(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(value) || bits == 0x80000000U || value < 0.0f)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"{field} is non-finite, negative, or encoded as negative zero");
        }

        return value;
    }

    private static WorldPosition ReadCanonicalPosition(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        var position = new WorldPosition(
            BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[8..]),
            BinaryPrimitives.ReadDoubleLittleEndian(bytes[16..]));
        if (!IsCanonicalPosition(position))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"{field} is non-finite or contains negative zero");
        }

        return position;
    }

    private static bool IsCanonicalBounds(WorldBounds value) =>
        IsCanonicalPosition(value.Min) && IsCanonicalPosition(value.Max);

    private static bool IsCanonicalPosition(WorldPosition value) =>
        IsCanonicalDouble(value.X) &&
        IsCanonicalDouble(value.Y) &&
        IsCanonicalDouble(value.Z);

    private static bool IsCanonicalDouble(double value) =>
        double.IsFinite(value) &&
        BitConverter.DoubleToInt64Bits(value) != unchecked((long)0x8000000000000000UL);

    private static double CanonicalizeZero(double value) => value == 0.0 ? 0.0 : value;

    private static float CanonicalizePositive(float value) => value > 0.0f ? value : 0.0f;

    private static float CanonicalizeNonNegative(float value) => value == 0.0f ? 0.0f : value;

    private static void WriteCanonicalFloat(Span<byte> bytes, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(bytes, CanonicalizeNonNegative(value));

    private static bool IsCanonicalNonNegative(float value) =>
        float.IsFinite(value) && value >= 0.0f && BitConverter.SingleToInt32Bits(value) != unchecked((int)0x80000000U);

    private static bool ContainsBounds(WorldBounds outer, WorldBounds inner) =>
        inner.Min.X >= outer.Min.X &&
        inner.Min.Y >= outer.Min.Y &&
        inner.Min.Z >= outer.Min.Z &&
        inner.Max.X <= outer.Max.X &&
        inner.Max.Y <= outer.Max.Y &&
        inner.Max.Z <= outer.Max.Z;

    private static bool PositionBitwiseEqual(WorldPosition left, WorldPosition right) =>
        DoubleBitwiseEqual(left.X, right.X) &&
        DoubleBitwiseEqual(left.Y, right.Y) &&
        DoubleBitwiseEqual(left.Z, right.Z);

    private static bool BoundsBitwiseEqual(WorldBounds left, WorldBounds right) =>
        PositionBitwiseEqual(left.Min, right.Min) &&
        PositionBitwiseEqual(left.Max, right.Max);

    private static bool DoubleBitwiseEqual(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    private static bool IsCanonicalPackageId(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= 128 &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static string RequireCanonicalPackageId(string packageId, string context)
    {
        if (!IsCanonicalPackageId(packageId))
        {
            throw new ArgumentException(
                $"Vegetation {context} '{packageId}' must be canonical lowercase ASCII.",
                nameof(packageId));
        }

        return packageId;
    }

    private sealed record PreparedPage(
        CookedVegetationInstancePage Page,
        CookedVegetationInstancePageReference Reference);

    private readonly record struct PageInstanceCursor(
        int PageIndex,
        int InstanceIndex);

    private sealed class SpeciesComparer : IComparer<CookedVegetationSpeciesReference>
    {
        internal static SpeciesComparer Instance { get; } = new();

        public int Compare(
            CookedVegetationSpeciesReference left,
            CookedVegetationSpeciesReference right) => CompareSpecies(left, right);
    }

    private readonly record struct DependencyKey(
        Guid Guid,
        string PackageId,
        string AssetType,
        string Variant);

    private readonly record struct SpeciesIdentity(
        Guid Guid,
        string PackageId);
}

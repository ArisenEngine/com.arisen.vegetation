using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Vegetation.Assets;

internal enum CookedVegetationInstancePageSectionType : uint
{
    Metadata = 1,
    Strings = 2,
    Species = 3,
    Instances = 4,
    Acceleration = 5
}

public static class VegetationInstancePageAssetCooker
{
    public const string RuntimeVariant = "runtime.vegetation-instance-page.v1";
    public const string CookedExtension = ".arivegetationpage";
    public const int CookedFormatVersion = 1;

    internal const int HeaderSize = 128;
    internal const int HashOffset = 64;
    internal const int MetadataStride = 128;
    internal const int SpeciesStride = 24;
    internal const int InstanceStride = 40;
    internal const int AccelerationStride = 72;
    internal const int MaximumInstanceCount = 65_536;
    internal const int MaximumSpeciesCount = 1_024;
    internal const int CurrentGeneratedSchemaVersion = 1;
    internal const uint TransformEncoding = 1;

    internal const int MaxCookedPageBytes = 8 * 1024 * 1024;
    private const int MaxStringBytes = 16 * 1024;
    private const int MaxStringCount = 1 + MaximumSpeciesCount;
    private const double SourceQuaternionNormTolerance = 0.001;
    private const double QuaternionNormTolerance = 0.000125;

    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIVPAGE");
    internal static object PublicationGate { get; } = new();
    private static readonly HashSet<uint> s_KnownSections =
    [
        (uint)CookedVegetationInstancePageSectionType.Metadata,
        (uint)CookedVegetationInstancePageSectionType.Strings,
        (uint)CookedVegetationInstancePageSectionType.Species,
        (uint)CookedVegetationInstancePageSectionType.Instances,
        (uint)CookedVegetationInstancePageSectionType.Acceleration
    ];

    public static CookedVegetationInstancePageArtifact Cook(
        IAssetDatabase assetDatabase,
        VegetationInstancePageCookDescriptor descriptor)
    {
        CookedVegetationInstancePage page = BuildForCook(assetDatabase, descriptor);
        return Cook(assetDatabase, page);
    }

    internal static CookedVegetationInstancePage BuildForCook(
        IAssetDatabase assetDatabase,
        VegetationInstancePageCookDescriptor descriptor)
    {
        using var zone = Profiler.Zone("Vegetation.BuildInstancePage");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(descriptor);

        try
        {
            return BuildForCookCore(assetDatabase, descriptor);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"[VegetationInstancePageAssetCooker] Instance-page descriptor " +
                $"'{descriptor.Guid:D}' is invalid: {ex.Message}",
                ex);
        }
    }

    private static CookedVegetationInstancePage BuildForCookCore(
        IAssetDatabase assetDatabase,
        VegetationInstancePageCookDescriptor descriptor)
    {
        string context = $"vegetation instance page '{descriptor.Guid:D}'";
        string packageId = RequireCanonicalPackageId(descriptor.PackageId, "instance-page package id");
        if (descriptor.Guid == Guid.Empty ||
            descriptor.ClusterGuid == Guid.Empty ||
            descriptor.GeneratedSchemaVersion != CurrentGeneratedSchemaVersion ||
            !descriptor.Origin.IsFinite ||
            descriptor.Species == null ||
            descriptor.Species.Count is < 1 or > MaximumSpeciesCount ||
            descriptor.Instances == null ||
            descriptor.Instances.Count is < 1 or > MaximumInstanceCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "identity, generated schema, origin, species count, or instance count is invalid");
        }

        ValidateDatabaseAsset(
            assetDatabase,
            descriptor.Guid,
            packageId,
            VegetationAssetTypes.InstancePage,
            context);
        ValidateDatabaseAsset(
            assetDatabase,
            descriptor.ClusterGuid,
            packageId,
            VegetationAssetTypes.Cluster,
            $"{context} parent cluster");
        WorldPosition canonicalOrigin = CanonicalizeOrigin(descriptor.Origin);

        var indexedSpecies = new IndexedSpecies[descriptor.Species.Count];
        var uniqueSpecies = new HashSet<SpeciesKey>();
        for (int index = 0; index < descriptor.Species.Count; index++)
        {
            CookedVegetationSpeciesReference species = descriptor.Species[index];
            string speciesPackageId = RequireCanonicalPackageId(
                species.PackageId,
                $"instance-page species {index} package id");
            var canonicalSpecies = new CookedVegetationSpeciesReference(
                species.Guid,
                speciesPackageId);
            if (species.Guid == Guid.Empty || !uniqueSpecies.Add(new SpeciesKey(species.Guid, speciesPackageId)))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species '{index}' has an empty or duplicate identity");
            }

            ValidateDatabaseAsset(
                assetDatabase,
                species.Guid,
                speciesPackageId,
                VegetationAssetTypes.Species,
                $"{context} species {index}");
            indexedSpecies[index] = new IndexedSpecies(index, canonicalSpecies);
        }

        Array.Sort(indexedSpecies, CompareIndexedSpecies);
        var canonicalSpeciesTable = new CookedVegetationSpeciesReference[indexedSpecies.Length];
        var remappedSpeciesIndices = new uint[indexedSpecies.Length];
        for (int canonicalIndex = 0; canonicalIndex < indexedSpecies.Length; canonicalIndex++)
        {
            IndexedSpecies indexed = indexedSpecies[canonicalIndex];
            canonicalSpeciesTable[canonicalIndex] = indexed.Species;
            remappedSpeciesIndices[indexed.OriginalIndex] = checked((uint)canonicalIndex);
        }

        var canonicalInstances = new CookedVegetationInstance[descriptor.Instances.Count];
        var usedSpecies = new bool[canonicalSpeciesTable.Length];
        for (int index = 0; index < descriptor.Instances.Count; index++)
        {
            VegetationCookedInstanceInput? input = descriptor.Instances[index];
            if (input == null ||
                input.StableKey == 0 ||
                (uint)input.SpeciesIndex >= (uint)descriptor.Species.Count)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"instance '{index}' has an invalid stable key or species index");
            }

            VegetationCookedInstanceInput canonicalInput = CanonicalizeInstanceInput(
                input,
                context,
                $"instance {index}");
            uint canonicalSpeciesIndex = remappedSpeciesIndices[input.SpeciesIndex];
            usedSpecies[checked((int)canonicalSpeciesIndex)] = true;
            canonicalInstances[index] = new CookedVegetationInstance(
                canonicalInput.StableKey,
                canonicalSpeciesIndex,
                canonicalInput.LocalPosition,
                canonicalInput.Orientation,
                canonicalInput.UniformScale,
                canonicalInput.ConservativeRadius);
        }

        Array.Sort(
            canonicalInstances,
            static (left, right) => left.StableKey.CompareTo(right.StableKey));
        for (int index = 1; index < canonicalInstances.Length; index++)
        {
            if (canonicalInstances[index - 1].StableKey == canonicalInstances[index].StableKey)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"stable instance key '{canonicalInstances[index].StableKey}' is duplicated");
            }
        }

        for (int index = 0; index < usedSpecies.Length; index++)
        {
            if (!usedSpecies[index])
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"canonical species '{index}' is unused");
            }
        }

        WorldBounds bounds = ComputeBounds(canonicalOrigin, canonicalInstances, context);
        var page = new CookedVegetationInstancePage(
            descriptor.Guid,
            descriptor.ClusterGuid,
            packageId,
            descriptor.GeneratedSchemaVersion,
            canonicalOrigin,
            bounds,
            Array.AsReadOnly(canonicalSpeciesTable),
            Array.AsReadOnly(canonicalInstances));
        page = page with { Acceleration = BuildAcceleration(page) };
        ValidatePage(page, context);
        return page;
    }

    internal static CookedVegetationInstancePageArtifact Cook(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePage page)
    {
        using var zone = Profiler.Zone("Vegetation.CookInstancePage");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(page);
        string context = $"vegetation instance page '{page.Guid:D}'";
        ValidatePage(page, context);
        ValidateCookedDatabaseOwnership(assetDatabase, page, context);

        VegetationCookedAssetDependency[] dependencies = BuildDependencies(page);
        byte[] payload = WritePayload(page);
        byte[] contentHash = SHA256.HashData(payload);
        lock (PublicationGate)
        {
            ValidatePublicationCompatibility(assetDatabase, page, payload);
            using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
                page.Guid,
                RuntimeVariant,
                CookedExtension);
            VegetationCookedContainer.WriteAtomicallyIfChanged(write.OutputPath, payload);
            CookedAssetRecord output = write.Commit(VegetationAssetTypes.InstancePage);
            return new CookedVegetationInstancePageArtifact(
                page.Guid,
                page.ClusterGuid,
                page.PackageId,
                RuntimeVariant,
                output.Path,
                page.Instances.Count,
                page.Origin,
                page.Bounds,
                output.SizeInBytes,
                contentHash,
                Array.AsReadOnly(page.Species.ToArray()),
                Array.AsReadOnly(dependencies));
        }
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        out CookedVegetationInstancePage page,
        out string diagnostic)
    {
        return TryLoadCookedCore(
            assetDatabase,
            pageRef,
            expectedClusterGuid: null,
            validateSpeciesAvailability: true,
            out page,
            out _,
            out diagnostic);
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        out CookedVegetationInstancePage page,
        out VegetationInstancePagePayloadIdentity payloadIdentity,
        out string diagnostic)
    {
        return TryLoadCookedCore(
            assetDatabase,
            pageRef,
            expectedClusterGuid: null,
            validateSpeciesAvailability: true,
            out page,
            out payloadIdentity,
            out diagnostic);
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        Guid expectedClusterGuid,
        out CookedVegetationInstancePage page,
        out string diagnostic)
    {
        if (expectedClusterGuid == Guid.Empty)
        {
            page = null!;
            diagnostic =
                "[VegetationInstancePageAssetCooker] Expected cluster GUID is empty.";
            return false;
        }

        return TryLoadCookedCore(
            assetDatabase,
            pageRef,
            expectedClusterGuid,
            validateSpeciesAvailability: true,
            out page,
            out _,
            out diagnostic);
    }

    internal static bool TryLoadCookedForCluster(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        Guid expectedClusterGuid,
        out CookedVegetationInstancePage page,
        out string diagnostic)
    {
        return TryLoadCookedForCluster(
            assetDatabase,
            pageRef,
            expectedClusterGuid,
            out page,
            out _,
            out diagnostic);
    }

    internal static bool TryLoadCookedForCluster(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        Guid expectedClusterGuid,
        out CookedVegetationInstancePage page,
        out VegetationInstancePagePayloadIdentity payloadIdentity,
        out string diagnostic)
    {
        if (expectedClusterGuid == Guid.Empty)
        {
            page = null!;
            payloadIdentity = default;
            diagnostic =
                "[VegetationInstancePageAssetCooker] Expected cluster GUID is empty.";
            return false;
        }

        return TryLoadCookedCore(
            assetDatabase,
            pageRef,
            expectedClusterGuid,
            validateSpeciesAvailability: false,
            out page,
            out payloadIdentity,
            out diagnostic);
    }

    internal static bool TryLoadCookedForRuntimeCooking(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        out CookedVegetationInstancePage page,
        out string diagnostic)
    {
        return TryLoadCookedCore(
            assetDatabase,
            pageRef,
            expectedClusterGuid: null,
            validateSpeciesAvailability: false,
            out page,
            out _,
            out diagnostic);
    }

    private static bool TryLoadCookedCore(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationInstancePageSourceAsset> pageRef,
        Guid? expectedClusterGuid,
        bool validateSpeciesAvailability,
        out CookedVegetationInstancePage page,
        out VegetationInstancePagePayloadIdentity payloadIdentity,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        page = null!;
        payloadIdentity = default;
        if (!pageRef.IsValid)
        {
            diagnostic = "[VegetationInstancePageAssetCooker] Cooked instance-page ref is empty.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(pageRef.AssetType) &&
            !string.Equals(
                pageRef.AssetType,
                VegetationAssetTypes.InstancePage,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationInstancePageAssetCooker] Instance page '{pageRef.Guid:D}' ref has asset type " +
                $"'{pageRef.AssetType}', expected '{VegetationAssetTypes.InstancePage}'.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(pageRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(
                descriptor.AssetType,
                VegetationAssetTypes.InstancePage,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationInstancePageAssetCooker] Instance page '{pageRef.Guid:D}' has no cataloged " +
                $"'{VegetationAssetTypes.InstancePage}' identity.";
            return false;
        }

        if (!IsCanonicalPackageId(descriptor.PackageId) ||
            (!string.IsNullOrEmpty(pageRef.PackageId) &&
             !string.Equals(pageRef.PackageId, descriptor.PackageId, StringComparison.Ordinal)))
        {
            diagnostic =
                $"[VegetationInstancePageAssetCooker] Instance page '{pageRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{pageRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                pageRef.Guid,
                RuntimeVariant,
                VegetationAssetTypes.InstancePage,
                out CookedAssetHandle handle))
        {
            diagnostic =
                $"[VegetationInstancePageAssetCooker] Instance page '{pageRef.Guid:D}' variant " +
                $"'{RuntimeVariant}' is unavailable.";
            return false;
        }

        try
        {
            string diagnosticPath = assetDatabase.TryGetCookedArtifact(
                pageRef.Guid,
                RuntimeVariant,
                out CookedAssetRecord? artifact)
                ? artifact.Path
                : $"{pageRef.Guid:D}:{RuntimeVariant}";
            ReadOnlyMemory<byte> payload = assetDatabase.GetCookedAssetBytes(handle);
            if (payload.Length < 56)
            {
                diagnostic =
                    $"[VegetationInstancePageAssetCooker] Cooked instance page '{diagnosticPath}' " +
                    "is truncated before its parent identity.";
                return false;
            }

            Guid embeddedClusterGuid = VegetationCookedContainer.ReadGuid(payload.Span[40..56]);
            Guid clusterGuid = expectedClusterGuid ?? embeddedClusterGuid;
            if (clusterGuid == Guid.Empty ||
                (expectedClusterGuid.HasValue && embeddedClusterGuid != expectedClusterGuid.Value))
            {
                diagnostic =
                    $"[VegetationInstancePageAssetCooker] Cooked instance page '{diagnosticPath}' " +
                    "has an empty or unexpected parent cluster identity.";
                return false;
            }

            try
            {
                ValidateDatabaseAsset(
                    assetDatabase,
                    clusterGuid,
                    descriptor.PackageId,
                    VegetationAssetTypes.Cluster,
                    $"vegetation instance page '{pageRef.Guid:D}' parent cluster");
            }
            catch (InvalidOperationException ex)
            {
                diagnostic = $"[VegetationInstancePageAssetCooker] {ex.Message}";
                return false;
            }

            if (!TryReadPayload(
                    pageRef.Guid,
                    clusterGuid,
                    descriptor.PackageId,
                    payload.Span,
                    diagnosticPath,
                    out page,
                    out diagnostic))
            {
                return false;
            }

            try
            {
                ValidateCookedSpeciesOwnership(assetDatabase, page, diagnosticPath);
                if (validateSpeciesAvailability)
                {
                    ValidateCookedSpeciesAvailability(assetDatabase, page, diagnosticPath);
                }

                payloadIdentity = new VegetationInstancePagePayloadIdentity(
                    page.Guid,
                    payload.Length,
                    SHA256.HashData(payload.Span));
                return true;
            }
            catch (InvalidOperationException ex)
            {
                page = null!;
                diagnostic =
                    $"[VegetationInstancePageAssetCooker] Cooked instance page '{diagnosticPath}' " +
                    $"has an invalid dependency: {ex.Message}";
                return false;
            }
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static byte[] WritePayload(CookedVegetationInstancePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        string context = $"vegetation instance page '{page.Guid:D}'";
        page = page with { Acceleration = page.Acceleration ?? BuildAcceleration(page) };
        ValidatePage(page, context);

        string[] strings = BuildCanonicalStrings(page);
        Dictionary<string, uint> stringIndices = BuildStringIndices(strings);
        byte[] metadata = BuildMetadataSection(page, stringIndices);
        byte[] species = BuildSpeciesSection(page, stringIndices);
        byte[] instances = BuildInstanceSection(page, context);
        byte[] acceleration = BuildAccelerationSection(page.Acceleration!, context);
        VegetationCookedSectionPayload[] sections =
        [
            new(
                (uint)CookedVegetationInstancePageSectionType.Metadata,
                VegetationCookedSectionFlags.Required,
                1,
                MetadataStride,
                metadata),
            new(
                (uint)CookedVegetationInstancePageSectionType.Strings,
                VegetationCookedSectionFlags.Required,
                checked((uint)strings.Length),
                0,
                VegetationCookedContainer.BuildStringSection(strings)),
            new(
                (uint)CookedVegetationInstancePageSectionType.Species,
                VegetationCookedSectionFlags.Required,
                checked((uint)page.Species.Count),
                SpeciesStride,
                species),
            new(
                (uint)CookedVegetationInstancePageSectionType.Instances,
                VegetationCookedSectionFlags.Required,
                checked((uint)page.Instances.Count),
                InstanceStride,
                instances),
            new(
                (uint)CookedVegetationInstancePageSectionType.Acceleration,
                VegetationCookedSectionFlags.None,
                checked((uint)page.Acceleration!.SpeciesRanges.Count),
                AccelerationStride,
                acceleration)
        ];

        byte[] output = VegetationCookedContainer.Build(
            HeaderSize,
            MaxCookedPageBytes,
            sections,
            out _);
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], VegetationCookedContainer.EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], page.GeneratedSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderSize);
        VegetationCookedContainer.WriteGuid(header[24..40], page.Guid);
        VegetationCookedContainer.WriteGuid(header[40..56], page.ClusterGuid);
        BinaryPrimitives.WriteInt32LittleEndian(header[56..], page.Instances.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[60..], page.Species.Count);
        BinaryPrimitives.WriteUInt64LittleEndian(header[96..], checked((ulong)output.Length));
        BinaryPrimitives.WriteInt32LittleEndian(header[104..], sections.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], TransformEncoding);
        VegetationCookedContainer.FinalizeHash(output, HeaderSize, HashOffset);
        return output;
    }

    internal static bool TryReadPayload(
        Guid expectedPageGuid,
        Guid expectedClusterGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out CookedVegetationInstancePage page,
        out string diagnostic)
    {
        using var zone = Profiler.Zone("Vegetation.ReadInstancePagePayload");
        try
        {
            page = ReadPayload(
                expectedPageGuid,
                expectedClusterGuid,
                RequireCanonicalPackageId(expectedPackageId, "expected instance-page package id"),
                bytes,
                diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or OverflowException or ArgumentException)
        {
            page = null!;
            diagnostic =
                $"[VegetationInstancePageAssetCooker] Cooked instance page '{diagnosticPath}' is invalid: " +
                ex.Message;
            return false;
        }
    }

    internal static VegetationCookedAssetDependency[] BuildDependencies(
        CookedVegetationInstancePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        ValidatePage(page, $"vegetation instance page '{page.Guid:D}'");
        VegetationCookedAssetDependency[] dependencies = page.Species
            .Select(species => new VegetationCookedAssetDependency(
                species.Guid,
                species.PackageId,
                VegetationAssetTypes.Species,
                VegetationSpeciesAssetCooker.RuntimeVariant,
                Required: true))
            .ToArray();
        Array.Sort(dependencies, CompareDependencies);
        return dependencies;
    }

    internal static void ValidatePublicationCompatibility(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePage page,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(page);
        if (!assetDatabase.TryGetCookedArtifact(page.Guid, RuntimeVariant, out CookedAssetRecord existing))
        {
            return;
        }

        if (!File.Exists(existing.Path))
        {
            return;
        }

        byte[] publishedBytes = File.ReadAllBytes(existing.Path);
        if (publishedBytes.AsSpan().SequenceEqual(payload))
        {
            return;
        }

        // Raw bytes may legitimately differ under one immutable page identity: format-v1 pages
        // carry optional derived sections such as acceleration ranges that readers reconstruct
        // when absent, and transactional artifact-set publication replaces the published bytes.
        // The canonical placement content must never change for one page GUID.
        if (TryReadPayload(
                page.Guid,
                page.ClusterGuid,
                page.PackageId,
                publishedBytes,
                existing.Path,
                out CookedVegetationInstancePage publishedPage,
                out _) &&
            HasEquivalentCanonicalContent(publishedPage, page))
        {
            return;
        }

        throw new InvalidOperationException(
            $"[VegetationInstancePageAssetCooker] Instance page '{page.Guid:D}' already has " +
            "a published artifact with different canonical placement content. Format-v1 page " +
            "identities are immutable; changed placement content must use a new explicit page GUID.");
    }

    private static bool HasEquivalentCanonicalContent(
        CookedVegetationInstancePage published,
        CookedVegetationInstancePage candidate)
    {
        if (published.Guid != candidate.Guid ||
            published.ClusterGuid != candidate.ClusterGuid ||
            !string.Equals(published.PackageId, candidate.PackageId, StringComparison.Ordinal) ||
            published.GeneratedSchemaVersion != candidate.GeneratedSchemaVersion ||
            !DoubleBitwiseEqual(published.Origin.X, candidate.Origin.X) ||
            !DoubleBitwiseEqual(published.Origin.Y, candidate.Origin.Y) ||
            !DoubleBitwiseEqual(published.Origin.Z, candidate.Origin.Z) ||
            !BoundsBitwiseEqual(published.Bounds, candidate.Bounds) ||
            published.Species.Count != candidate.Species.Count ||
            published.Instances.Count != candidate.Instances.Count)
        {
            return false;
        }

        for (int index = 0; index < published.Species.Count; index++)
        {
            if (published.Species[index] != candidate.Species[index])
            {
                return false;
            }
        }

        for (int index = 0; index < published.Instances.Count; index++)
        {
            if (published.Instances[index] != candidate.Instances[index])
            {
                return false;
            }
        }

        return true;
    }

    private static CookedVegetationInstancePage ReadPayload(
        Guid expectedPageGuid,
        Guid expectedClusterGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        string context = $"vegetation instance page '{diagnosticPath}'";
        if (bytes.Length < HeaderSize)
        {
            throw VegetationCookedContainer.Invalid(context, "header is truncated");
        }

        if (!bytes[..8].SequenceEqual(s_Magic))
        {
            throw VegetationCookedContainer.Invalid(context, "magic is not ARIVPAGE");
        }

        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int generatedSchemaVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        Guid pageGuid = VegetationCookedContainer.ReadGuid(bytes[24..40]);
        Guid clusterGuid = VegetationCookedContainer.ReadGuid(bytes[40..56]);
        int instanceCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[56..]);
        int speciesCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes[96..]);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[104..]);
        uint transformEncoding = BinaryPrimitives.ReadUInt32LittleEndian(bytes[108..]);
        if (endian != VegetationCookedContainer.EndianMarker ||
            formatVersion != CookedFormatVersion ||
            generatedSchemaVersion != CurrentGeneratedSchemaVersion ||
            headerSize != HeaderSize ||
            pageGuid == Guid.Empty || pageGuid != expectedPageGuid ||
            clusterGuid == Guid.Empty || clusterGuid != expectedClusterGuid ||
            instanceCount is < 1 or > MaximumInstanceCount ||
            speciesCount is < 1 or > MaximumSpeciesCount ||
            declaredSize != checked((ulong)bytes.Length) ||
            transformEncoding != TransformEncoding)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "header identity, version, counts, transform encoding, or exact size is invalid");
        }

        VegetationCookedContainer.EnsureZero(bytes[112..HeaderSize], context, "reserved header bytes");
        Dictionary<uint, VegetationCookedSectionDescriptor> sections =
            VegetationCookedContainer.ReadDirectory(
                bytes,
                HeaderSize,
                HashOffset,
                sectionCount,
                MaxCookedPageBytes,
                s_KnownSections,
                context);
        VegetationCookedSectionDescriptor metadataSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationInstancePageSectionType.Metadata,
            MetadataStride,
            1,
            1,
            context);
        VegetationCookedSectionDescriptor stringsSection =
            VegetationCookedContainer.RequireVariableSection(
                sections,
                (uint)CookedVegetationInstancePageSectionType.Strings,
                MaxStringCount,
                context);
        VegetationCookedSectionDescriptor speciesSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationInstancePageSectionType.Species,
            SpeciesStride,
            checked((uint)speciesCount),
            MaximumSpeciesCount,
            context);
        VegetationCookedSectionDescriptor instancesSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationInstancePageSectionType.Instances,
            InstanceStride,
            checked((uint)instanceCount),
            MaximumInstanceCount,
            context);
        VegetationCookedSectionDescriptor? accelerationSection = null;
        if (sections.TryGetValue(
                (uint)CookedVegetationInstancePageSectionType.Acceleration,
                out VegetationCookedSectionDescriptor encodedAcceleration))
        {
            if (encodedAcceleration.Flags != VegetationCookedSectionFlags.None ||
                encodedAcceleration.Stride != AccelerationStride ||
                encodedAcceleration.Count != checked((uint)speciesCount) ||
                encodedAcceleration.Count > MaximumSpeciesCount)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    "optional acceleration section has invalid flags, stride, or count");
            }

            accelerationSection = encodedAcceleration;
        }

        string[] strings = VegetationCookedContainer.ReadStrings(
            bytes,
            stringsSection,
            MaxStringBytes,
            context);
        ReadOnlySpan<byte> metadata = VegetationCookedContainer.GetSection(bytes, metadataSection);
        Guid metadataPageGuid = VegetationCookedContainer.ReadGuid(metadata[..16]);
        Guid metadataClusterGuid = VegetationCookedContainer.ReadGuid(metadata[16..32]);
        string packageId = VegetationCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[32..]),
            "package id",
            context);
        uint metadataTransformEncoding = BinaryPrimitives.ReadUInt32LittleEndian(metadata[36..]);
        var origin = new WorldPosition(
            ReadCanonicalDouble(metadata[40..48], context, "origin X"),
            ReadCanonicalDouble(metadata[48..56], context, "origin Y"),
            ReadCanonicalDouble(metadata[56..64], context, "origin Z"));
        var encodedBounds = new WorldBounds(
            new WorldPosition(
                BinaryPrimitives.ReadDoubleLittleEndian(metadata[64..]),
                BinaryPrimitives.ReadDoubleLittleEndian(metadata[72..]),
                BinaryPrimitives.ReadDoubleLittleEndian(metadata[80..])),
            new WorldPosition(
                BinaryPrimitives.ReadDoubleLittleEndian(metadata[88..]),
                BinaryPrimitives.ReadDoubleLittleEndian(metadata[96..]),
                BinaryPrimitives.ReadDoubleLittleEndian(metadata[104..])));
        uint metadataInstanceCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[112..]);
        uint metadataSpeciesCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[116..]);
        int metadataGeneratedSchema = BinaryPrimitives.ReadInt32LittleEndian(metadata[120..]);
        uint metadataReserved = BinaryPrimitives.ReadUInt32LittleEndian(metadata[124..]);
        if (metadataPageGuid != pageGuid ||
            metadataClusterGuid != clusterGuid ||
            !string.Equals(packageId, expectedPackageId, StringComparison.Ordinal) ||
            !IsCanonicalPackageId(packageId) ||
            metadataTransformEncoding != TransformEncoding ||
            !origin.IsFinite ||
            !encodedBounds.IsValid ||
            metadataInstanceCount != checked((uint)instanceCount) ||
            metadataSpeciesCount != checked((uint)speciesCount) ||
            metadataGeneratedSchema != generatedSchemaVersion ||
            metadataReserved != 0)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "metadata identity, encoding, bounds, counts, schema, or reserved data is invalid");
        }

        ReadOnlySpan<byte> encodedSpecies = VegetationCookedContainer.GetSection(bytes, speciesSection);
        var species = new CookedVegetationSpeciesReference[speciesCount];
        for (int index = 0; index < species.Length; index++)
        {
            ReadOnlySpan<byte> record = encodedSpecies.Slice(index * SpeciesStride, SpeciesStride);
            Guid guid = VegetationCookedContainer.ReadGuid(record[..16]);
            string speciesPackageId = VegetationCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record[16..]),
                $"species {index} package id",
                context);
            uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(record[20..]);
            species[index] = new CookedVegetationSpeciesReference(guid, speciesPackageId);
            if (guid == Guid.Empty || !IsCanonicalPackageId(speciesPackageId) || reserved != 0 ||
                (index > 0 && CompareSpecies(species[index - 1], species[index]) >= 0))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species record '{index}' is empty, duplicated, out of order, or reserved data is nonzero");
            }
        }

        ReadOnlySpan<byte> encodedInstances = VegetationCookedContainer.GetSection(bytes, instancesSection);
        var instances = new CookedVegetationInstance[instanceCount];
        var usedSpecies = new bool[speciesCount];
        ulong previousStableKey = 0;
        for (int index = 0; index < instances.Length; index++)
        {
            ReadOnlySpan<byte> record = encodedInstances.Slice(index * InstanceStride, InstanceStride);
            ulong stableKey = BinaryPrimitives.ReadUInt64LittleEndian(record);
            uint speciesIndex = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            Vector3 localPosition = ReadCanonicalPosition(record[12..24], context, index);
            var packedOrientation = new PackedQuaternion(
                BinaryPrimitives.ReadInt16LittleEndian(record[24..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[26..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[28..]),
                BinaryPrimitives.ReadInt16LittleEndian(record[30..]));
            ValidatePackedQuaternion(packedOrientation, context, $"instance {index} orientation");
            float uniformScale = ReadCanonicalPositiveFloat(
                record[32..36],
                context,
                $"instance {index} uniform scale");
            float conservativeRadius = ReadCanonicalPositiveFloat(
                record[36..40],
                context,
                $"instance {index} conservative radius");
            if (stableKey == 0 ||
                (index > 0 && stableKey <= previousStableKey) ||
                speciesIndex >= speciesCount)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"instance '{index}' has a zero, duplicate, or unordered key or invalid species index");
            }

            usedSpecies[checked((int)speciesIndex)] = true;
            instances[index] = new CookedVegetationInstance(
                stableKey,
                speciesIndex,
                localPosition,
                DecodeQuaternion(packedOrientation),
                uniformScale,
                conservativeRadius);
            previousStableKey = stableKey;
        }

        for (int index = 0; index < usedSpecies.Length; index++)
        {
            if (!usedSpecies[index])
            {
                throw VegetationCookedContainer.Invalid(context, $"species '{index}' is unused");
            }
        }

        WorldBounds computedBounds = ComputeBounds(origin, instances, context);
        if (!BoundsBitwiseEqual(encodedBounds, computedBounds))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "stored world bounds do not bitwise match the decoded instances");
        }

        CookedVegetationInstancePageAcceleration acceleration;
        if (accelerationSection.HasValue)
        {
            ReadOnlySpan<byte> accelerationBytes = VegetationCookedContainer.GetSection(
                bytes,
                accelerationSection.Value);
            var ranges = new CookedVegetationSpeciesInstanceRange[speciesCount];
            for (int index = 0; index < ranges.Length; index++)
            {
                ReadOnlySpan<byte> record = accelerationBytes.Slice(
                    index * AccelerationStride,
                    AccelerationStride);
                uint speciesIndex = BinaryPrimitives.ReadUInt32LittleEndian(record);
                uint rawFirst = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
                uint rawLast = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
                uint rawCount = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
                if (rawFirst > int.MaxValue || rawLast > int.MaxValue || rawCount > int.MaxValue)
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"species range '{index}' index or count is outside the signed range");
                }

                WorldBounds rangeBounds = ReadBounds(
                    record[16..64],
                    context,
                    $"species range {index} bounds");
                float maximumRadius = ReadCanonicalPositiveFloat(
                    record[64..68],
                    context,
                    $"species range {index} maximum radius");
                VegetationCookedContainer.EnsureZero(
                    record[68..],
                    context,
                    $"species range {index} reserved bytes");
                ranges[index] = new CookedVegetationSpeciesInstanceRange(
                    speciesIndex,
                    checked((int)rawFirst),
                    checked((int)rawLast),
                    checked((int)rawCount),
                    rangeBounds,
                    maximumRadius);
            }

            acceleration = new CookedVegetationInstancePageAcceleration(
                computedBounds,
                Array.AsReadOnly(ranges));
        }
        else
        {
            acceleration = BuildAcceleration(
                new CookedVegetationInstancePage(
                    pageGuid,
                    clusterGuid,
                    packageId,
                    generatedSchemaVersion,
                    origin,
                    computedBounds,
                    Array.AsReadOnly(species),
                    Array.AsReadOnly(instances)));
        }

        var page = new CookedVegetationInstancePage(
            pageGuid,
            clusterGuid,
            packageId,
            generatedSchemaVersion,
            origin,
            computedBounds,
            Array.AsReadOnly(species),
            Array.AsReadOnly(instances))
        {
            Acceleration = acceleration
        };
        ValidateCanonicalStrings(strings, BuildCanonicalStrings(page), context);
        ValidatePage(page, context);
        return page;
    }

    private static byte[] BuildMetadataSection(
        CookedVegetationInstancePage page,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[MetadataStride];
        VegetationCookedContainer.WriteGuid(bytes.AsSpan(0, 16), page.Guid);
        VegetationCookedContainer.WriteGuid(bytes.AsSpan(16, 16), page.ClusterGuid);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), stringIndices[page.PackageId]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), TransformEncoding);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(40), page.Origin.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(48), page.Origin.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(56), page.Origin.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(64), page.Bounds.Min.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(72), page.Bounds.Min.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(80), page.Bounds.Min.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(88), page.Bounds.Max.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(96), page.Bounds.Max.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(104), page.Bounds.Max.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(112), checked((uint)page.Instances.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(116), checked((uint)page.Species.Count));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(120), page.GeneratedSchemaVersion);
        return bytes;
    }

    private static byte[] BuildSpeciesSection(
        CookedVegetationInstancePage page,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[checked(page.Species.Count * SpeciesStride)];
        for (int index = 0; index < page.Species.Count; index++)
        {
            CookedVegetationSpeciesReference species = page.Species[index];
            Span<byte> record = bytes.AsSpan(index * SpeciesStride, SpeciesStride);
            VegetationCookedContainer.WriteGuid(record[..16], species.Guid);
            BinaryPrimitives.WriteUInt32LittleEndian(record[16..], stringIndices[species.PackageId]);
        }

        return bytes;
    }

    private static byte[] BuildInstanceSection(
        CookedVegetationInstancePage page,
        string context)
    {
        byte[] bytes = new byte[checked(page.Instances.Count * InstanceStride)];
        for (int index = 0; index < page.Instances.Count; index++)
        {
            CookedVegetationInstance instance = page.Instances[index];
            Span<byte> record = bytes.AsSpan(index * InstanceStride, InstanceStride);
            WriteCanonicalInstanceRecord(
                record,
                instance.StableKey,
                instance.SpeciesIndex,
                instance.LocalPosition,
                instance.Orientation,
                instance.UniformScale,
                instance.ConservativeRadius,
                context,
                $"instance {index} orientation");
        }

        return bytes;
    }

    internal static CookedVegetationInstancePageAcceleration BuildAcceleration(
        CookedVegetationInstancePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        string context = $"vegetation instance page '{page.Guid:D}' acceleration";
        if (page.Species == null || page.Instances == null || page.Species.Count == 0 ||
            page.Instances.Count == 0)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "species and instance data are required");
        }

        var ranges = new CookedVegetationSpeciesInstanceRange[page.Species.Count];
        for (int speciesIndex = 0; speciesIndex < ranges.Length; speciesIndex++)
        {
            int first = -1;
            int last = -1;
            int count = 0;
            float maximumRadius = 0.0f;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            double maxZ = double.NegativeInfinity;
            for (int instanceIndex = 0; instanceIndex < page.Instances.Count; instanceIndex++)
            {
                CookedVegetationInstance instance = page.Instances[instanceIndex];
                if (instance.SpeciesIndex != (uint)speciesIndex)
                {
                    continue;
                }

                first = first < 0 ? instanceIndex : first;
                last = instanceIndex;
                count++;
                maximumRadius = Math.Max(maximumRadius, instance.ConservativeRadius);
                double x = page.Origin.X + instance.LocalPosition.X;
                double y = page.Origin.Y + instance.LocalPosition.Y;
                double z = page.Origin.Z + instance.LocalPosition.Z;
                minX = Math.Min(minX, x - instance.ConservativeRadius);
                minY = Math.Min(minY, y - instance.ConservativeRadius);
                minZ = Math.Min(minZ, z - instance.ConservativeRadius);
                maxX = Math.Max(maxX, x + instance.ConservativeRadius);
                maxY = Math.Max(maxY, y + instance.ConservativeRadius);
                maxZ = Math.Max(maxZ, z + instance.ConservativeRadius);
            }

            if (count == 0 || !float.IsFinite(maximumRadius))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species '{speciesIndex}' has no valid instances");
            }

            var bounds = new WorldBounds(
                new WorldPosition(
                    CanonicalizeZero(minX),
                    CanonicalizeZero(minY),
                    CanonicalizeZero(minZ)),
                new WorldPosition(
                    CanonicalizeZero(maxX),
                    CanonicalizeZero(maxY),
                    CanonicalizeZero(maxZ)));
            if (!bounds.IsValid || !IsCanonicalBounds(bounds))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species '{speciesIndex}' bounds are invalid");
            }

            ranges[speciesIndex] = new CookedVegetationSpeciesInstanceRange(
                checked((uint)speciesIndex),
                first,
                last,
                count,
                bounds,
                CanonicalizeZero(maximumRadius));
        }

        var acceleration = new CookedVegetationInstancePageAcceleration(
            page.Bounds,
            Array.AsReadOnly(ranges));
        ValidateAcceleration(page, acceleration, context);
        return acceleration;
    }

    private static byte[] BuildAccelerationSection(
        CookedVegetationInstancePageAcceleration acceleration,
        string context)
    {
        byte[] bytes = new byte[checked(acceleration.SpeciesRanges.Count * AccelerationStride)];
        for (int index = 0; index < acceleration.SpeciesRanges.Count; index++)
        {
            CookedVegetationSpeciesInstanceRange range = acceleration.SpeciesRanges[index];
            Span<byte> record = bytes.AsSpan(index * AccelerationStride, AccelerationStride);
            BinaryPrimitives.WriteUInt32LittleEndian(record, range.SpeciesIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], checked((uint)range.FirstInstanceIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], checked((uint)range.LastInstanceIndex));
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], checked((uint)range.InstanceCount));
            WriteBounds(record[16..64], range.Bounds);
            WriteCanonicalFloat(record[64..], range.MaximumConservativeRadius);
            VegetationCookedContainer.EnsureZero(record[68..], context, $"species range {index} reserved bytes");
        }

        return bytes;
    }

    private static void ValidateAcceleration(
        CookedVegetationInstancePage page,
        CookedVegetationInstancePageAcceleration acceleration,
        string context)
    {
        if (acceleration.Bounds.IsValid == false ||
            !IsCanonicalBounds(acceleration.Bounds) ||
            !BoundsBitwiseEqual(acceleration.Bounds, page.Bounds) ||
            acceleration.SpeciesRanges == null ||
            acceleration.SpeciesRanges.Count != page.Species.Count)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "page bounds or species range count is invalid");
        }

        for (int speciesIndex = 0; speciesIndex < acceleration.SpeciesRanges.Count; speciesIndex++)
        {
            CookedVegetationSpeciesInstanceRange range = acceleration.SpeciesRanges[speciesIndex];
            if (range == null ||
                range.SpeciesIndex != (uint)speciesIndex ||
                range.FirstInstanceIndex < 0 ||
                range.LastInstanceIndex < range.FirstInstanceIndex ||
                range.LastInstanceIndex >= page.Instances.Count ||
                range.InstanceCount <= 0 ||
                range.InstanceCount > page.Instances.Count ||
                !range.Bounds.IsValid ||
                !IsCanonicalBounds(range.Bounds) ||
                !IsCanonicalPositive(range.MaximumConservativeRadius))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species range '{speciesIndex}' is invalid");
            }

            int first = -1;
            int last = -1;
            int count = 0;
            float maximumRadius = 0.0f;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            double maxZ = double.NegativeInfinity;
            for (int instanceIndex = 0; instanceIndex < page.Instances.Count; instanceIndex++)
            {
                CookedVegetationInstance instance = page.Instances[instanceIndex];
                if (instance.SpeciesIndex != (uint)speciesIndex)
                {
                    continue;
                }

                first = first < 0 ? instanceIndex : first;
                last = instanceIndex;
                count++;
                maximumRadius = Math.Max(maximumRadius, instance.ConservativeRadius);
                double x = page.Origin.X + instance.LocalPosition.X;
                double y = page.Origin.Y + instance.LocalPosition.Y;
                double z = page.Origin.Z + instance.LocalPosition.Z;
                minX = Math.Min(minX, x - instance.ConservativeRadius);
                minY = Math.Min(minY, y - instance.ConservativeRadius);
                minZ = Math.Min(minZ, z - instance.ConservativeRadius);
                maxX = Math.Max(maxX, x + instance.ConservativeRadius);
                maxY = Math.Max(maxY, y + instance.ConservativeRadius);
                maxZ = Math.Max(maxZ, z + instance.ConservativeRadius);
            }

            var expectedBounds = new WorldBounds(
                new WorldPosition(
                    CanonicalizeZero(minX),
                    CanonicalizeZero(minY),
                    CanonicalizeZero(minZ)),
                new WorldPosition(
                    CanonicalizeZero(maxX),
                    CanonicalizeZero(maxY),
                    CanonicalizeZero(maxZ)));
            if (first != range.FirstInstanceIndex ||
                last != range.LastInstanceIndex ||
                count != range.InstanceCount ||
                BitConverter.SingleToInt32Bits(maximumRadius) !=
                    BitConverter.SingleToInt32Bits(range.MaximumConservativeRadius) ||
                !BoundsBitwiseEqual(expectedBounds, range.Bounds))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species range '{speciesIndex}' does not match the instance payload");
            }
        }
    }

    private static void ValidatePage(CookedVegetationInstancePage page, string context)
    {
        if (page.Guid == Guid.Empty ||
            page.ClusterGuid == Guid.Empty ||
            !IsCanonicalPackageId(page.PackageId) ||
            page.GeneratedSchemaVersion != CurrentGeneratedSchemaVersion ||
            !IsCanonicalOrigin(page.Origin) ||
            page.Species == null ||
            page.Species.Count is < 1 or > MaximumSpeciesCount ||
            page.Instances == null ||
            page.Instances.Count is < 1 or > MaximumInstanceCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "identity, generated schema, origin, species count, or instance count is invalid");
        }

        for (int index = 0; index < page.Species.Count; index++)
        {
            CookedVegetationSpeciesReference species = page.Species[index];
            if (species.Guid == Guid.Empty ||
                !IsCanonicalPackageId(species.PackageId) ||
                (index > 0 && CompareSpecies(page.Species[index - 1], species) >= 0))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"species '{index}' is empty, duplicated, or not in canonical order");
            }
        }

        var usedSpecies = new bool[page.Species.Count];
        ulong previousStableKey = 0;
        for (int index = 0; index < page.Instances.Count; index++)
        {
            CookedVegetationInstance instance = page.Instances[index];
            if (instance.StableKey == 0 ||
                (index > 0 && instance.StableKey <= previousStableKey) ||
                instance.SpeciesIndex >= page.Species.Count ||
                !float.IsFinite(instance.LocalPosition.X) ||
                !float.IsFinite(instance.LocalPosition.Y) ||
                !float.IsFinite(instance.LocalPosition.Z) ||
                !IsCanonicalPositive(instance.UniformScale) ||
                !IsCanonicalPositive(instance.ConservativeRadius))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"instance '{index}' key, species index, transform, scale, or radius is invalid");
            }

            _ = PackQuaternion(instance.Orientation, context, $"instance {index} orientation");
            usedSpecies[checked((int)instance.SpeciesIndex)] = true;
            previousStableKey = instance.StableKey;
        }

        if (usedSpecies.Any(used => !used))
        {
            throw VegetationCookedContainer.Invalid(context, "species table contains an unused entry");
        }

        WorldBounds expectedBounds = ComputeBounds(page.Origin, page.Instances, context);
        if (!page.Bounds.IsValid ||
            !IsCanonicalBounds(page.Bounds) ||
            !BoundsBitwiseEqual(page.Bounds, expectedBounds))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "world bounds are invalid or do not bitwise match the instance payload");
        }

        if (page.Acceleration != null)
        {
            ValidateAcceleration(page, page.Acceleration, context);
        }
    }

    private static WorldBounds ComputeBounds(
        WorldPosition origin,
        IReadOnlyList<CookedVegetationInstance> instances,
        string context)
    {
        if (!origin.IsFinite || instances.Count == 0)
        {
            throw VegetationCookedContainer.Invalid(context, "cannot derive bounds from an empty or invalid page");
        }

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double minZ = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;
        double maxZ = double.NegativeInfinity;
        for (int index = 0; index < instances.Count; index++)
        {
            CookedVegetationInstance instance = instances[index];
            double radius = instance.ConservativeRadius;
            double x = origin.X + instance.LocalPosition.X;
            double y = origin.Y + instance.LocalPosition.Y;
            double z = origin.Z + instance.LocalPosition.Z;
            double instanceMinX = x - radius;
            double instanceMinY = y - radius;
            double instanceMinZ = z - radius;
            double instanceMaxX = x + radius;
            double instanceMaxY = y + radius;
            double instanceMaxZ = z + radius;
            if (!double.IsFinite(instanceMinX) ||
                !double.IsFinite(instanceMinY) ||
                !double.IsFinite(instanceMinZ) ||
                !double.IsFinite(instanceMaxX) ||
                !double.IsFinite(instanceMaxY) ||
                !double.IsFinite(instanceMaxZ))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"instance '{index}' produces non-finite world bounds");
            }

            minX = Math.Min(minX, instanceMinX);
            minY = Math.Min(minY, instanceMinY);
            minZ = Math.Min(minZ, instanceMinZ);
            maxX = Math.Max(maxX, instanceMaxX);
            maxY = Math.Max(maxY, instanceMaxY);
            maxZ = Math.Max(maxZ, instanceMaxZ);
        }

        var bounds = new WorldBounds(
            new WorldPosition(
                CanonicalizeZero(minX),
                CanonicalizeZero(minY),
                CanonicalizeZero(minZ)),
            new WorldPosition(
                CanonicalizeZero(maxX),
                CanonicalizeZero(maxY),
                CanonicalizeZero(maxZ)));
        if (!bounds.IsValid)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "instance radii are not representable at the page world origin");
        }

        return bounds;
    }

    private static void ValidateCookedDatabaseOwnership(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePage page,
        string context)
    {
        ValidateDatabaseAsset(
            assetDatabase,
            page.Guid,
            page.PackageId,
            VegetationAssetTypes.InstancePage,
            context);
        ValidateDatabaseAsset(
            assetDatabase,
            page.ClusterGuid,
            page.PackageId,
            VegetationAssetTypes.Cluster,
            $"{context} parent cluster");
        ValidateCookedSpeciesOwnership(assetDatabase, page, context);
    }

    private static void ValidateCookedSpeciesOwnership(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePage page,
        string context)
    {
        for (int index = 0; index < page.Species.Count; index++)
        {
            CookedVegetationSpeciesReference species = page.Species[index];
            ValidateDatabaseAsset(
                assetDatabase,
                species.Guid,
                species.PackageId,
                VegetationAssetTypes.Species,
                $"{context} species {index}");
        }
    }

    private static void ValidateCookedSpeciesAvailability(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePage page,
        string context)
    {
        for (int index = 0; index < page.Species.Count; index++)
        {
            CookedVegetationSpeciesReference species = page.Species[index];
            if (!VegetationSpeciesAssetCooker.TryLoadCooked(
                    assetDatabase,
                    species.ToAssetRef(),
                    out _,
                    out string diagnostic))
            {
                throw new InvalidOperationException(
                    $"{context} species {index} '{species.Guid:D}' is unavailable or invalid: " +
                    diagnostic);
            }
        }
    }

    private static void ValidateDatabaseAsset(
        IAssetDatabase assetDatabase,
        Guid guid,
        string packageId,
        string assetType,
        string context)
    {
        if (guid == Guid.Empty ||
            !assetDatabase.TryGetAssetDescriptor(guid, out AssetDescriptor descriptor))
        {
            throw new InvalidOperationException(
                $"{context} '{guid:D}' is missing from the asset database.");
        }

        if (descriptor.Guid != guid ||
            !string.Equals(descriptor.PackageId, packageId, StringComparison.Ordinal) ||
            !string.Equals(descriptor.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{context} '{guid:D}' resolves to '{descriptor.PackageId}:{descriptor.AssetType}', " +
                $"expected '{packageId}:{assetType}'.");
        }
    }

    private static string[] BuildCanonicalStrings(CookedVegetationInstancePage page)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal)
        {
            page.PackageId
        };
        foreach (CookedVegetationSpeciesReference species in page.Species)
        {
            strings.Add(species.PackageId);
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

    internal static VegetationCookedInstanceInput CanonicalizeInstanceInput(
        VegetationCookedInstanceInput input,
        string context,
        string field)
    {
        ArgumentNullException.ThrowIfNull(input);
        Vector3 localPosition = CanonicalizePosition(
            input.LocalPosition,
            context,
            $"{field} local position");
        float uniformScale = CanonicalizePositiveFloat(
            input.UniformScale,
            context,
            $"{field} uniform scale");
        float conservativeRadius = CanonicalizePositiveFloat(
            input.ConservativeRadius,
            context,
            $"{field} conservative radius");
        PackedQuaternion packedOrientation = PackQuaternion(
            input.Orientation,
            context,
            $"{field} orientation");
        return input with
        {
            LocalPosition = localPosition,
            Orientation = DecodeQuaternion(packedOrientation),
            UniformScale = uniformScale,
            ConservativeRadius = conservativeRadius
        };
    }

    internal static void WriteCanonicalInstanceRecord(
        Span<byte> record,
        VegetationCookedInstanceInput input,
        string context,
        string orientationField)
    {
        ArgumentNullException.ThrowIfNull(input);
        WriteCanonicalInstanceRecord(
            record,
            input.StableKey,
            checked((uint)input.SpeciesIndex),
            input.LocalPosition,
            input.Orientation,
            input.UniformScale,
            input.ConservativeRadius,
            context,
            orientationField);
    }

    private static void WriteCanonicalInstanceRecord(
        Span<byte> record,
        ulong stableKey,
        uint speciesIndex,
        Vector3 localPosition,
        Quaternion orientationValue,
        float uniformScale,
        float conservativeRadius,
        string context,
        string orientationField)
    {
        if (record.Length != InstanceStride)
        {
            throw new ArgumentException(
                $"Vegetation instance record must be exactly {InstanceStride} bytes.",
                nameof(record));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(record, stableKey);
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], speciesIndex);
        WriteCanonicalFloat(record[12..], localPosition.X);
        WriteCanonicalFloat(record[16..], localPosition.Y);
        WriteCanonicalFloat(record[20..], localPosition.Z);
        PackedQuaternion orientation = PackQuaternion(
            orientationValue,
            context,
            orientationField);
        BinaryPrimitives.WriteInt16LittleEndian(record[24..], orientation.X);
        BinaryPrimitives.WriteInt16LittleEndian(record[26..], orientation.Y);
        BinaryPrimitives.WriteInt16LittleEndian(record[28..], orientation.Z);
        BinaryPrimitives.WriteInt16LittleEndian(record[30..], orientation.W);
        WriteCanonicalFloat(record[32..], uniformScale);
        WriteCanonicalFloat(record[36..], conservativeRadius);
    }

    private static Vector3 CanonicalizePosition(Vector3 value, string context, string field)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} is non-finite");
        }

        return new Vector3(CanonicalizeZero(value.X), CanonicalizeZero(value.Y), CanonicalizeZero(value.Z));
    }

    private static WorldPosition CanonicalizeOrigin(WorldPosition value) =>
        new(
            CanonicalizeZero(value.X),
            CanonicalizeZero(value.Y),
            CanonicalizeZero(value.Z));

    private static float CanonicalizePositiveFloat(float value, string context, string field)
    {
        if (!float.IsFinite(value) || value <= 0.0f)
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} must be finite and positive");
        }

        return value;
    }

    private static PackedQuaternion PackQuaternion(
        Quaternion value,
        string context,
        string field)
    {
        value = new Quaternion(
            CanonicalizeZero(value.X),
            CanonicalizeZero(value.Y),
            CanonicalizeZero(value.Z),
            CanonicalizeZero(value.W));
        if (!float.IsFinite(value.X) ||
            !float.IsFinite(value.Y) ||
            !float.IsFinite(value.Z) ||
            !float.IsFinite(value.W))
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} is non-finite");
        }

        if (TryGetExactPackedQuaternion(value, out PackedQuaternion exact))
        {
            return exact;
        }

        double lengthSquared =
            ((double)value.X * value.X) +
            ((double)value.Y * value.Y) +
            ((double)value.Z * value.Z) +
            ((double)value.W * value.W);
        if (!double.IsFinite(lengthSquared) || lengthSquared <= 0.0)
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} has zero or invalid length");
        }

        if (Math.Abs(lengthSquared - 1.0) > SourceQuaternionNormTolerance)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"{field} is materially non-unit before canonical normalization");
        }

        double inverseLength = 1.0 / Math.Sqrt(lengthSquared);
        var packed = new PackedQuaternion(
            QuantizeSnorm16(value.X * inverseLength),
            QuantizeSnorm16(value.Y * inverseLength),
            QuantizeSnorm16(value.Z * inverseLength),
            QuantizeSnorm16(value.W * inverseLength));
        packed = CanonicalizeHemisphere(packed);
        ValidatePackedQuaternion(packed, context, field);
        return packed;
    }

    private static bool TryGetExactPackedQuaternion(
        Quaternion value,
        out PackedQuaternion packed)
    {
        packed = default;
        if (!TryGetExactSnorm16(value.X, out short x) ||
            !TryGetExactSnorm16(value.Y, out short y) ||
            !TryGetExactSnorm16(value.Z, out short z) ||
            !TryGetExactSnorm16(value.W, out short w))
        {
            return false;
        }

        var candidate = new PackedQuaternion(x, y, z, w);
        if (!IsCanonicalHemisphere(candidate) || !HasValidPackedNorm(candidate))
        {
            return false;
        }

        packed = candidate;
        return true;
    }

    private static bool TryGetExactSnorm16(float value, out short packed)
    {
        packed = 0;
        if (!float.IsFinite(value) || value < -1.0f || value > 1.0f)
        {
            return false;
        }

        int rounded = checked((int)Math.Round(
            (double)value * short.MaxValue,
            MidpointRounding.AwayFromZero));
        if (rounded is < -short.MaxValue or > short.MaxValue)
        {
            return false;
        }

        packed = checked((short)rounded);
        float decoded = packed / (float)short.MaxValue;
        return BitConverter.SingleToInt32Bits(decoded) == BitConverter.SingleToInt32Bits(value);
    }

    private static short QuantizeSnorm16(double value)
    {
        double clamped = Math.Clamp(value, -1.0, 1.0);
        return checked((short)(int)Math.Round(
            clamped * short.MaxValue,
            MidpointRounding.AwayFromZero));
    }

    private static PackedQuaternion CanonicalizeHemisphere(PackedQuaternion value)
    {
        int leading = value.W != 0
            ? value.W
            : value.Z != 0
                ? value.Z
                : value.Y != 0
                    ? value.Y
                    : value.X;
        return leading < 0
            ? new PackedQuaternion(
                checked((short)-value.X),
                checked((short)-value.Y),
                checked((short)-value.Z),
                checked((short)-value.W))
            : value;
    }

    private static void ValidatePackedQuaternion(
        PackedQuaternion value,
        string context,
        string field)
    {
        if (value.X == short.MinValue ||
            value.Y == short.MinValue ||
            value.Z == short.MinValue ||
            value.W == short.MinValue ||
            !IsCanonicalHemisphere(value) ||
            !HasValidPackedNorm(value))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"{field} is not a canonical normalized SNORM16 quaternion");
        }
    }

    private static bool IsCanonicalHemisphere(PackedQuaternion value)
    {
        int leading = value.W != 0
            ? value.W
            : value.Z != 0
                ? value.Z
                : value.Y != 0
                    ? value.Y
                    : value.X;
        return leading > 0;
    }

    private static bool HasValidPackedNorm(PackedQuaternion value)
    {
        double inverse = 1.0 / short.MaxValue;
        double x = value.X * inverse;
        double y = value.Y * inverse;
        double z = value.Z * inverse;
        double w = value.W * inverse;
        double normSquared = (x * x) + (y * y) + (z * z) + (w * w);
        return Math.Abs(normSquared - 1.0) <= QuaternionNormTolerance;
    }

    private static Quaternion DecodeQuaternion(PackedQuaternion value)
    {
        return new Quaternion(
            value.X / (float)short.MaxValue,
            value.Y / (float)short.MaxValue,
            value.Z / (float)short.MaxValue,
            value.W / (float)short.MaxValue);
    }

    private static Vector3 ReadCanonicalPosition(
        ReadOnlySpan<byte> bytes,
        string context,
        int instanceIndex)
    {
        return new Vector3(
            ReadCanonicalFiniteFloat(bytes[..4], context, $"instance {instanceIndex} local X"),
            ReadCanonicalFiniteFloat(bytes[4..8], context, $"instance {instanceIndex} local Y"),
            ReadCanonicalFiniteFloat(bytes[8..12], context, $"instance {instanceIndex} local Z"));
    }

    private static float ReadCanonicalPositiveFloat(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        float value = ReadCanonicalFiniteFloat(bytes, context, field);
        if (value <= 0.0f)
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} is not positive");
        }

        return value;
    }

    private static float ReadCanonicalFiniteFloat(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        float value = BitConverter.UInt32BitsToSingle(bits);
        if (!float.IsFinite(value) || bits == 0x80000000u)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"{field} is non-finite or encoded as negative zero");
        }

        return value;
    }

    private static double ReadCanonicalDouble(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        double value = BitConverter.UInt64BitsToDouble(bits);
        if (!double.IsFinite(value) || bits == 0x8000000000000000UL)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"{field} is non-finite or encoded as negative zero");
        }

        return value;
    }

    private static void WriteCanonicalFloat(Span<byte> bytes, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes, CanonicalizeZero(value));
    }

    private static void WriteBounds(Span<byte> bytes, WorldBounds bounds)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, bounds.Min.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[8..], bounds.Min.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[16..], bounds.Min.Z);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[24..], bounds.Max.X);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[32..], bounds.Max.Y);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[40..], bounds.Max.Z);
    }

    private static WorldBounds ReadBounds(
        ReadOnlySpan<byte> bytes,
        string context,
        string field)
    {
        var bounds = new WorldBounds(
            new WorldPosition(
                ReadCanonicalDouble(bytes, context, $"{field} minimum X"),
                ReadCanonicalDouble(bytes[8..], context, $"{field} minimum Y"),
                ReadCanonicalDouble(bytes[16..], context, $"{field} minimum Z")),
            new WorldPosition(
                ReadCanonicalDouble(bytes[24..], context, $"{field} maximum X"),
                ReadCanonicalDouble(bytes[32..], context, $"{field} maximum Y"),
                ReadCanonicalDouble(bytes[40..], context, $"{field} maximum Z")));
        if (!bounds.IsValid)
        {
            throw VegetationCookedContainer.Invalid(context, $"{field} is invalid");
        }

        return bounds;
    }

    private static float CanonicalizeZero(float value) => value == 0.0f ? 0.0f : value;

    private static double CanonicalizeZero(double value) => value == 0.0 ? 0.0 : value;

    private static bool IsCanonicalOrigin(WorldPosition value) =>
        IsCanonicalFinite(value.X) &&
        IsCanonicalFinite(value.Y) &&
        IsCanonicalFinite(value.Z);

    private static bool IsCanonicalBounds(WorldBounds value) =>
        IsCanonicalOrigin(value.Min) && IsCanonicalOrigin(value.Max);

    private static bool IsCanonicalFinite(double value) =>
        double.IsFinite(value) &&
        BitConverter.DoubleToInt64Bits(value) != unchecked((long)0x8000000000000000UL);

    private static bool IsCanonicalPositive(float value) =>
        float.IsFinite(value) && value > 0.0f;

    private static bool BoundsBitwiseEqual(WorldBounds left, WorldBounds right) =>
        DoubleBitwiseEqual(left.Min.X, right.Min.X) &&
        DoubleBitwiseEqual(left.Min.Y, right.Min.Y) &&
        DoubleBitwiseEqual(left.Min.Z, right.Min.Z) &&
        DoubleBitwiseEqual(left.Max.X, right.Max.X) &&
        DoubleBitwiseEqual(left.Max.Y, right.Max.Y) &&
        DoubleBitwiseEqual(left.Max.Z, right.Max.Z);

    private static bool DoubleBitwiseEqual(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    private static int CompareIndexedSpecies(IndexedSpecies left, IndexedSpecies right) =>
        CompareSpecies(left.Species, right.Species);

    private static int CompareSpecies(
        CookedVegetationSpeciesReference left,
        CookedVegetationSpeciesReference right)
    {
        int result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        return result != 0 ? result : left.Guid.CompareTo(right.Guid);
    }

    private static int CompareDependencies(
        VegetationCookedAssetDependency left,
        VegetationCookedAssetDependency right)
    {
        int result = left.Guid.CompareTo(right.Guid);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.AssetType, right.AssetType);
        if (result != 0)
        {
            return result;
        }

        result = StringComparer.Ordinal.Compare(left.Variant, right.Variant);
        return result != 0 ? result : left.Required.CompareTo(right.Required);
    }

    private static bool IsCanonicalPackageId(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= VegetationSpeciesSourceAssetLoader.MaximumPackageIdLength &&
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

    private readonly record struct IndexedSpecies(
        int OriginalIndex,
        CookedVegetationSpeciesReference Species);

    private readonly record struct SpeciesKey(Guid Guid, string PackageId);

    private readonly record struct PackedQuaternion(short X, short Y, short Z, short W);
}

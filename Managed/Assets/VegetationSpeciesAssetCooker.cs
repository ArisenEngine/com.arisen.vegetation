using System.Buffers.Binary;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Vegetation.Assets;

internal enum CookedVegetationSpeciesSectionType : uint
{
    Metadata = 1,
    Strings = 2,
    Lods = 3
}

public static class VegetationSpeciesAssetCooker
{
    public const string RuntimeVariant = "runtime.vegetation-species.v1";
    public const string CookedExtension = ".arivegetationspecies";
    public const int CookedFormatVersion = 1;

    internal const int HeaderSize = 128;
    internal const int HashOffset = 64;
    internal const int MetadataStride = 64;
    internal const int LodStride = 64;

    private const int MaxCookedSpeciesBytes = 4 * 1024 * 1024;
    private const int MaxStringBytes = 16 * 1024;
    private const int MaxStringCount = 2 + (VegetationSpeciesSourceAssetLoader.MaximumLodCount * 2);
    private const string MeshAssetType = "Mesh";
    private const string MaterialAssetType = "Material";
    private const string MeshRuntimeVariant = "staticmesh.uint32";
    private const string MaterialRuntimeVariant = "material.runtime";

    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIVSPEC");
    private static readonly HashSet<uint> s_KnownSections =
    [
        (uint)CookedVegetationSpeciesSectionType.Metadata,
        (uint)CookedVegetationSpeciesSectionType.Strings,
        (uint)CookedVegetationSpeciesSectionType.Lods
    ];

    public static CookedVegetationSpeciesArtifact Cook(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationSpeciesSourceAsset> speciesRef)
    {
        using var zone = Profiler.Zone("Vegetation.CookSpecies");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        VegetationSpeciesSourceDescriptor source =
            VegetationSpeciesSourceAssetLoader.LoadSource(assetDatabase, speciesRef);
        ValidateSourceDependencies(assetDatabase, source);

        CookedVegetationSpecies species = BuildSpecies(source);
        byte[] payload = WritePayload(species);
        using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
            species.Guid,
            RuntimeVariant,
            CookedExtension);
        VegetationCookedContainer.WriteAtomicallyIfChanged(write.OutputPath, payload);
        CookedAssetRecord output = write.Commit(VegetationAssetTypes.Species);
        VegetationCookedAssetDependency[] dependencies = BuildDependencies(species);
        return new CookedVegetationSpeciesArtifact(
            species.Guid,
            RuntimeVariant,
            output.Path,
            species.Lods.Count,
            output.SizeInBytes,
            Array.AsReadOnly(dependencies));
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationSpeciesSourceAsset> speciesRef,
        out CookedVegetationSpecies species,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        species = null!;
        if (!speciesRef.IsValid)
        {
            diagnostic = "[VegetationSpeciesAssetCooker] Cooked vegetation species ref is empty.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(speciesRef.AssetType) &&
            !string.Equals(
                speciesRef.AssetType,
                VegetationAssetTypes.Species,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationSpeciesAssetCooker] Vegetation species '{speciesRef.Guid:D}' ref has asset type " +
                $"'{speciesRef.AssetType}', expected '{VegetationAssetTypes.Species}'.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(speciesRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(
                descriptor.AssetType,
                VegetationAssetTypes.Species,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationSpeciesAssetCooker] Vegetation species '{speciesRef.Guid:D}' has no cataloged " +
                $"'{VegetationAssetTypes.Species}' identity.";
            return false;
        }

        if (!IsCanonicalPackageId(descriptor.PackageId) ||
            (!string.IsNullOrEmpty(speciesRef.PackageId) &&
             !string.Equals(speciesRef.PackageId, descriptor.PackageId, StringComparison.Ordinal)))
        {
            diagnostic =
                $"[VegetationSpeciesAssetCooker] Vegetation species '{speciesRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{speciesRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                speciesRef.Guid,
                RuntimeVariant,
                VegetationAssetTypes.Species,
                out CookedAssetHandle handle))
        {
            diagnostic =
                $"[VegetationSpeciesAssetCooker] Vegetation species '{speciesRef.Guid:D}' variant " +
                $"'{RuntimeVariant}' is unavailable.";
            return false;
        }

        try
        {
            string diagnosticPath = assetDatabase.TryGetCookedArtifact(
                speciesRef.Guid,
                RuntimeVariant,
                out CookedAssetRecord? artifact)
                ? artifact.Path
                : $"{speciesRef.Guid:D}:{RuntimeVariant}";
            if (!TryReadPayload(
                    speciesRef.Guid,
                    descriptor.PackageId,
                    assetDatabase.GetCookedAssetBytes(handle).Span,
                    diagnosticPath,
                    out species,
                    out diagnostic))
            {
                return false;
            }

            try
            {
                ValidateCookedDependencies(assetDatabase, species);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                species = null!;
                diagnostic =
                    $"[VegetationSpeciesAssetCooker] Cooked vegetation species '{diagnosticPath}' " +
                    $"has an invalid dependency: {ex.Message}";
                return false;
            }
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static CookedVegetationSpecies LoadSourceForDependencyDiscovery(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationSpeciesSourceAsset> speciesRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        VegetationSpeciesSourceDescriptor source =
            VegetationSpeciesSourceAssetLoader.LoadSource(assetDatabase, speciesRef);
        ValidateSourceDependencies(assetDatabase, source);
        return BuildSpecies(source);
    }

    internal static byte[] WritePayload(CookedVegetationSpecies species)
    {
        ArgumentNullException.ThrowIfNull(species);
        ValidateSpecies(species, $"vegetation species '{species.Guid:D}'");
        string[] strings = BuildCanonicalStrings(species);
        Dictionary<string, uint> stringIndices = BuildStringIndices(strings);
        byte[] metadata = BuildMetadataSection(species, stringIndices);
        byte[] lods = BuildLodSection(species, stringIndices);
        VegetationCookedSectionPayload[] sections =
        [
            new(
                (uint)CookedVegetationSpeciesSectionType.Metadata,
                VegetationCookedSectionFlags.Required,
                1,
                MetadataStride,
                metadata),
            new(
                (uint)CookedVegetationSpeciesSectionType.Strings,
                VegetationCookedSectionFlags.Required,
                checked((uint)strings.Length),
                0,
                VegetationCookedContainer.BuildStringSection(strings)),
            new(
                (uint)CookedVegetationSpeciesSectionType.Lods,
                VegetationCookedSectionFlags.Required,
                checked((uint)species.Lods.Count),
                LodStride,
                lods)
        ];

        byte[] output = VegetationCookedContainer.Build(
            HeaderSize,
            MaxCookedSpeciesBytes,
            sections,
            out _);
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], VegetationCookedContainer.EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], species.SourceSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderSize);
        VegetationCookedContainer.WriteGuid(header[24..40], species.Guid);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], species.Lods.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[44..], sections.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], checked((ulong)output.Length));
        VegetationCookedContainer.FinalizeHash(output, HeaderSize, HashOffset);
        return output;
    }

    internal static bool TryReadPayload(
        Guid expectedSpeciesGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out CookedVegetationSpecies species,
        out string diagnostic)
    {
        using var zone = Profiler.Zone("Vegetation.ReadSpeciesPayload");
        try
        {
            species = ReadPayload(
                expectedSpeciesGuid,
                RequireCanonicalPackageId(expectedPackageId, "expected species package id"),
                bytes,
                diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or OverflowException or ArgumentException)
        {
            species = null!;
            diagnostic =
                $"[VegetationSpeciesAssetCooker] Cooked vegetation species '{diagnosticPath}' is invalid: " +
                ex.Message;
            return false;
        }
    }

    internal static VegetationCookedAssetDependency[] BuildDependencies(
        CookedVegetationSpecies species)
    {
        ArgumentNullException.ThrowIfNull(species);
        ValidateSpecies(species, $"vegetation species '{species.Guid:D}'");
        var dependencies = new Dictionary<DependencyKey, VegetationCookedAssetDependency>();
        foreach (CookedVegetationSpeciesLod lod in species.Lods)
        {
            AddDependency(
                dependencies,
                new VegetationCookedAssetDependency(
                    lod.Mesh.Guid,
                    lod.Mesh.PackageId,
                    MeshAssetType,
                    MeshRuntimeVariant,
                    Required: true));
            AddDependency(
                dependencies,
                new VegetationCookedAssetDependency(
                    lod.Material.Guid,
                    lod.Material.PackageId,
                    MaterialAssetType,
                    MaterialRuntimeVariant,
                    Required: true));
        }

        VegetationCookedAssetDependency[] output = dependencies.Values.ToArray();
        Array.Sort(output, CompareDependencies);
        return output;
    }

    private static CookedVegetationSpecies BuildSpecies(
        VegetationSpeciesSourceDescriptor source)
    {
        var lods = new CookedVegetationSpeciesLod[source.Lods.Count];
        for (int index = 0; index < source.Lods.Count; index++)
        {
            VegetationSpeciesLodDescriptor lod = source.Lods[index];
            lods[index] = new CookedVegetationSpeciesLod(
                new CookedVegetationMeshReference(
                    lod.Mesh.Guid,
                    RequireCanonicalPackageId(lod.Mesh.PackageId, $"LOD {index} mesh package id")),
                new CookedVegetationMaterialReference(
                    lod.Material.Guid,
                    RequireCanonicalPackageId(lod.Material.PackageId, $"LOD {index} material package id")),
                lod.MaximumDistance,
                lod.MaximumScreenError);
        }

        return new CookedVegetationSpecies(
            source.Guid,
            RequireCanonicalPackageId(source.PackageId, "species package id"),
            source.SourceSchemaVersion,
            source.Name,
            Array.AsReadOnly(lods),
            source.ShadowPolicy,
            source.ScaleRange,
            source.YawRangeDegrees,
            source.TiltRangeDegrees,
            source.CollisionPromotion,
            source.WindResponse);
    }

    private static CookedVegetationSpecies ReadPayload(
        Guid expectedSpeciesGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        string context = $"vegetation species '{diagnosticPath}'";
        if (bytes.Length < HeaderSize)
        {
            throw VegetationCookedContainer.Invalid(context, "header is truncated");
        }

        if (!bytes[..8].SequenceEqual(s_Magic))
        {
            throw VegetationCookedContainer.Invalid(context, "magic is not ARIVSPEC");
        }

        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int sourceVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        Guid speciesGuid = VegetationCookedContainer.ReadGuid(bytes[24..40]);
        int lodCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]);
        if (endian != VegetationCookedContainer.EndianMarker ||
            formatVersion != CookedFormatVersion ||
            sourceVersion is < VegetationSpeciesSourceAssetLoader.MinimumSourceSchemaVersion or
                > VegetationSpeciesSourceAssetLoader.CurrentSourceSchemaVersion ||
            headerSize != HeaderSize ||
            speciesGuid == Guid.Empty || speciesGuid != expectedSpeciesGuid ||
            lodCount is < VegetationSpeciesSourceAssetLoader.MinimumLodCount or
                > VegetationSpeciesSourceAssetLoader.MaximumLodCount ||
            declaredSize != checked((ulong)bytes.Length))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "header identity, version, counts, or exact size is invalid");
        }

        VegetationCookedContainer.EnsureZero(bytes[56..64], context, "reserved header bytes");
        VegetationCookedContainer.EnsureZero(bytes[96..HeaderSize], context, "reserved header bytes");
        Dictionary<uint, VegetationCookedSectionDescriptor> sections =
            VegetationCookedContainer.ReadDirectory(
                bytes,
                HeaderSize,
                HashOffset,
                sectionCount,
                MaxCookedSpeciesBytes,
                s_KnownSections,
                context);
        VegetationCookedSectionDescriptor metadataSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationSpeciesSectionType.Metadata,
            MetadataStride,
            1,
            1,
            context);
        VegetationCookedSectionDescriptor stringsSection =
            VegetationCookedContainer.RequireVariableSection(
                sections,
                (uint)CookedVegetationSpeciesSectionType.Strings,
                MaxStringCount,
                context);
        VegetationCookedSectionDescriptor lodSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationSpeciesSectionType.Lods,
            LodStride,
            checked((uint)lodCount),
            VegetationSpeciesSourceAssetLoader.MaximumLodCount,
            context);

        string[] strings = VegetationCookedContainer.ReadStrings(
            bytes,
            stringsSection,
            MaxStringBytes,
            context);
        ReadOnlySpan<byte> metadata = VegetationCookedContainer.GetSection(bytes, metadataSection);
        string packageId = VegetationCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata),
            "package id",
            context);
        string name = VegetationCookedContainer.ReadString(
            strings,
            BinaryPrimitives.ReadUInt32LittleEndian(metadata[4..]),
            "name",
            context);
        var shadowPolicy = (VegetationShadowPolicy)BinaryPrimitives.ReadUInt32LittleEndian(metadata[8..]);
        var collisionMode =
            (VegetationCollisionPromotionMode)BinaryPrimitives.ReadUInt32LittleEndian(metadata[12..]);
        var scaleRange = new VegetationValueRange(
            BinaryPrimitives.ReadSingleLittleEndian(metadata[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(metadata[20..]));
        var yawRange = new VegetationValueRange(
            BinaryPrimitives.ReadSingleLittleEndian(metadata[24..]),
            BinaryPrimitives.ReadSingleLittleEndian(metadata[28..]));
        var tiltRange = new VegetationValueRange(
            BinaryPrimitives.ReadSingleLittleEndian(metadata[32..]),
            BinaryPrimitives.ReadSingleLittleEndian(metadata[36..]));
        var collision = new VegetationCollisionPromotionDescriptor(
            collisionMode,
            BinaryPrimitives.ReadSingleLittleEndian(metadata[40..]),
            BinaryPrimitives.ReadSingleLittleEndian(metadata[44..]),
            BinaryPrimitives.ReadSingleLittleEndian(metadata[48..]));
        float windResponse = BinaryPrimitives.ReadSingleLittleEndian(metadata[52..]);
        VegetationCookedContainer.EnsureZero(metadata[56..], context, "metadata reserved bytes");

        ReadOnlySpan<byte> encodedLods = VegetationCookedContainer.GetSection(bytes, lodSection);
        var lods = new CookedVegetationSpeciesLod[lodCount];
        for (int index = 0; index < lodCount; index++)
        {
            ReadOnlySpan<byte> record = encodedLods.Slice(index * LodStride, LodStride);
            Guid meshGuid = VegetationCookedContainer.ReadGuid(record[..16]);
            Guid materialGuid = VegetationCookedContainer.ReadGuid(record[16..32]);
            string meshPackageId = VegetationCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record[32..]),
                $"LOD {index} mesh package id",
                context);
            string materialPackageId = VegetationCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record[36..]),
                $"LOD {index} material package id",
                context);
            lods[index] = new CookedVegetationSpeciesLod(
                new CookedVegetationMeshReference(meshGuid, meshPackageId),
                new CookedVegetationMaterialReference(materialGuid, materialPackageId),
                BinaryPrimitives.ReadSingleLittleEndian(record[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[44..]));
            VegetationCookedContainer.EnsureZero(
                record[48..],
                context,
                $"LOD {index} reserved bytes");
        }

        var species = new CookedVegetationSpecies(
            speciesGuid,
            packageId,
            sourceVersion,
            name,
            Array.AsReadOnly(lods),
            shadowPolicy,
            scaleRange,
            yawRange,
            tiltRange,
            collision,
            windResponse);
        ValidateSpecies(species, context);
        if (!string.Equals(species.PackageId, expectedPackageId, StringComparison.Ordinal))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"package identity '{species.PackageId}' does not match expected '{expectedPackageId}'");
        }

        ValidateCanonicalStrings(strings, BuildCanonicalStrings(species), context);
        return species;
    }

    private static byte[] BuildMetadataSection(
        CookedVegetationSpecies species,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[MetadataStride];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), stringIndices[species.PackageId]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), stringIndices[species.Name]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)species.ShadowPolicy);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)species.CollisionPromotion.Mode);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), species.ScaleRange.Minimum);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), species.ScaleRange.Maximum);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), species.YawRangeDegrees.Minimum);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28), species.YawRangeDegrees.Maximum);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(32), species.TiltRangeDegrees.Minimum);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(36), species.TiltRangeDegrees.Maximum);
        BinaryPrimitives.WriteSingleLittleEndian(
            bytes.AsSpan(40),
            species.CollisionPromotion.CapsuleRadius);
        BinaryPrimitives.WriteSingleLittleEndian(
            bytes.AsSpan(44),
            species.CollisionPromotion.CapsuleHalfHeight);
        BinaryPrimitives.WriteSingleLittleEndian(
            bytes.AsSpan(48),
            species.CollisionPromotion.MaximumDistance);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(52), species.WindResponse);
        return bytes;
    }

    private static byte[] BuildLodSection(
        CookedVegetationSpecies species,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[checked(species.Lods.Count * LodStride)];
        for (int index = 0; index < species.Lods.Count; index++)
        {
            CookedVegetationSpeciesLod lod = species.Lods[index];
            Span<byte> record = bytes.AsSpan(index * LodStride, LodStride);
            VegetationCookedContainer.WriteGuid(record[..16], lod.Mesh.Guid);
            VegetationCookedContainer.WriteGuid(record[16..32], lod.Material.Guid);
            BinaryPrimitives.WriteUInt32LittleEndian(record[32..], stringIndices[lod.Mesh.PackageId]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[36..], stringIndices[lod.Material.PackageId]);
            BinaryPrimitives.WriteSingleLittleEndian(record[40..], lod.MaximumDistance);
            BinaryPrimitives.WriteSingleLittleEndian(record[44..], lod.MaximumScreenError);
        }

        return bytes;
    }

    private static void ValidateSpecies(CookedVegetationSpecies species, string context)
    {
        ArgumentNullException.ThrowIfNull(species);
        if (species.Guid == Guid.Empty ||
            !IsCanonicalPackageId(species.PackageId) ||
            species.SourceSchemaVersion is < VegetationSpeciesSourceAssetLoader.MinimumSourceSchemaVersion or
                > VegetationSpeciesSourceAssetLoader.CurrentSourceSchemaVersion ||
            !IsCanonicalName(species.Name) ||
            species.Lods == null ||
            species.Lods.Count is < VegetationSpeciesSourceAssetLoader.MinimumLodCount or
                > VegetationSpeciesSourceAssetLoader.MaximumLodCount ||
            !Enum.IsDefined(species.ShadowPolicy) ||
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
                VegetationSpeciesSourceAssetLoader.MaximumTiltDegrees) ||
            !IsCollisionValid(species.CollisionPromotion) ||
            !IsBounded(
                species.WindResponse,
                VegetationSpeciesSourceAssetLoader.MinimumWindResponse,
                VegetationSpeciesSourceAssetLoader.MaximumWindResponse))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "identity, metadata, or authored ranges are invalid");
        }

        float previousDistance = float.NegativeInfinity;
        float previousScreenError = float.NegativeInfinity;
        for (int index = 0; index < species.Lods.Count; index++)
        {
            CookedVegetationSpeciesLod? lod = species.Lods[index];
            if (lod == null ||
                lod.Mesh.Guid == Guid.Empty ||
                lod.Material.Guid == Guid.Empty ||
                !IsCanonicalPackageId(lod.Mesh.PackageId) ||
                !IsCanonicalPackageId(lod.Material.PackageId) ||
                !IsBounded(
                    lod.MaximumDistance,
                    VegetationSpeciesSourceAssetLoader.MinimumMaximumDistance,
                    VegetationSpeciesSourceAssetLoader.MaximumMaximumDistance) ||
                !IsBounded(
                    lod.MaximumScreenError,
                    VegetationSpeciesSourceAssetLoader.MinimumMaximumScreenError,
                    VegetationSpeciesSourceAssetLoader.MaximumMaximumScreenError) ||
                lod.MaximumDistance <= previousDistance ||
                lod.MaximumScreenError <= previousScreenError)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"LOD '{index}' dependency identity, range, or ordering is invalid");
            }

            previousDistance = lod.MaximumDistance;
            previousScreenError = lod.MaximumScreenError;
        }
    }

    private static bool IsCollisionValid(VegetationCollisionPromotionDescriptor collision)
    {
        if (!Enum.IsDefined(collision.Mode))
        {
            return false;
        }

        if (collision.Mode == VegetationCollisionPromotionMode.None)
        {
            return collision.CapsuleRadius == 0.0f &&
                   collision.CapsuleHalfHeight == 0.0f &&
                   collision.MaximumDistance == 0.0f;
        }

        return collision.Mode == VegetationCollisionPromotionMode.Capsule &&
               IsBounded(
                   collision.CapsuleRadius,
                   VegetationSpeciesSourceAssetLoader.MinimumCapsuleRadius,
                   VegetationSpeciesSourceAssetLoader.MaximumCapsuleRadius) &&
               IsBounded(
                   collision.CapsuleHalfHeight,
                   VegetationSpeciesSourceAssetLoader.MinimumCapsuleHalfHeight,
                   VegetationSpeciesSourceAssetLoader.MaximumCapsuleHalfHeight) &&
               collision.CapsuleHalfHeight >= collision.CapsuleRadius &&
               IsBounded(
                   collision.MaximumDistance,
                   VegetationSpeciesSourceAssetLoader.MinimumCollisionDistance,
                   VegetationSpeciesSourceAssetLoader.MaximumCollisionDistance);
    }

    private static void ValidateSourceDependencies(
        IAssetDatabase assetDatabase,
        VegetationSpeciesSourceDescriptor source)
    {
        for (int index = 0; index < source.Lods.Count; index++)
        {
            VegetationSpeciesLodDescriptor lod = source.Lods[index];
            ValidateDatabaseDependency(
                assetDatabase,
                lod.Mesh.Guid,
                lod.Mesh.PackageId,
                MeshAssetType,
                $"vegetation species '{source.Guid:D}' LOD {index} mesh");
            ValidateDatabaseDependency(
                assetDatabase,
                lod.Material.Guid,
                lod.Material.PackageId,
                MaterialAssetType,
                $"vegetation species '{source.Guid:D}' LOD {index} material");
        }
    }

    private static void ValidateCookedDependencies(
        IAssetDatabase assetDatabase,
        CookedVegetationSpecies species)
    {
        for (int index = 0; index < species.Lods.Count; index++)
        {
            CookedVegetationSpeciesLod lod = species.Lods[index];
            ValidateDatabaseDependency(
                assetDatabase,
                lod.Mesh.Guid,
                lod.Mesh.PackageId,
                MeshAssetType,
                $"vegetation species '{species.Guid:D}' LOD {index} mesh");
            ValidateDatabaseDependency(
                assetDatabase,
                lod.Material.Guid,
                lod.Material.PackageId,
                MaterialAssetType,
                $"vegetation species '{species.Guid:D}' LOD {index} material");
        }
    }

    private static void ValidateDatabaseDependency(
        IAssetDatabase assetDatabase,
        Guid guid,
        string packageId,
        string assetType,
        string context)
    {
        if (guid == Guid.Empty ||
            !assetDatabase.TryGetAssetDescriptor(guid, out AssetDescriptor descriptor))
        {
            throw new InvalidOperationException($"{context} '{guid:D}' is missing from the asset database.");
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

    private static string[] BuildCanonicalStrings(CookedVegetationSpecies species)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal)
        {
            species.PackageId,
            species.Name
        };
        foreach (CookedVegetationSpeciesLod lod in species.Lods)
        {
            strings.Add(lod.Mesh.PackageId);
            strings.Add(lod.Material.PackageId);
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

    private static bool IsRange(VegetationValueRange range, float minimum, float maximum) =>
        IsBounded(range.Minimum, minimum, maximum) &&
        IsBounded(range.Maximum, minimum, maximum) &&
        range.Minimum <= range.Maximum;

    private static bool IsBounded(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool IsCanonicalName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= VegetationSpeciesSourceAssetLoader.MaximumNameLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

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

    private readonly record struct DependencyKey(
        Guid Guid,
        string PackageId,
        string AssetType,
        string Variant);
}

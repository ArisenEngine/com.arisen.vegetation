using System.Buffers.Binary;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Vegetation.Assets;

internal enum CookedVegetationBiomeSectionType : uint
{
    Metadata = 1,
    Strings = 2,
    Entries = 3,
    LayerWeightRules = 4
}

public static class VegetationBiomeAssetCooker
{
    public const string RuntimeVariant = "runtime.vegetation-biome.v1";
    public const string CookedExtension = ".arivegetationbiome";
    public const int CookedFormatVersion = 1;

    internal const int HeaderSize = 128;
    internal const int HashOffset = 64;
    internal const int MetadataStride = 24;
    internal const int EntryStride = 80;
    internal const int LayerWeightRuleStride = 16;

    private const int MaxCookedBiomeBytes = 8 * 1024 * 1024;
    private const int MaxStringBytes = 16 * 1024;
    private const int MaxLayerWeightRuleCount =
        VegetationBiomeSourceAssetLoader.MaximumEntryCount *
        VegetationBiomeSourceAssetLoader.MaximumLayerWeightRuleCount;
    private const int MaxStringCount =
        2 +
        (VegetationBiomeSourceAssetLoader.MaximumEntryCount *
         (2 + VegetationBiomeSourceAssetLoader.MaximumLayerWeightRuleCount));

    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARIVBIOM");
    private static readonly HashSet<uint> s_KnownSections =
    [
        (uint)CookedVegetationBiomeSectionType.Metadata,
        (uint)CookedVegetationBiomeSectionType.Strings,
        (uint)CookedVegetationBiomeSectionType.Entries,
        (uint)CookedVegetationBiomeSectionType.LayerWeightRules
    ];

    public static CookedVegetationBiomeArtifact Cook(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationBiomeSourceAsset> biomeRef)
    {
        using var zone = Profiler.Zone("Vegetation.CookBiome");
        ArgumentNullException.ThrowIfNull(assetDatabase);
        VegetationBiomeSourceDescriptor source =
            VegetationBiomeSourceAssetLoader.LoadSource(assetDatabase, biomeRef);
        ValidateSourceDependencies(assetDatabase, source);

        CookedVegetationBiome biome = BuildBiome(source);
        byte[] payload = WritePayload(biome);
        using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
            biome.Guid,
            RuntimeVariant,
            CookedExtension);
        VegetationCookedContainer.WriteAtomicallyIfChanged(write.OutputPath, payload);
        CookedAssetRecord output = write.Commit(VegetationAssetTypes.Biome);
        VegetationCookedAssetDependency[] dependencies = BuildDependencies(biome);
        return new CookedVegetationBiomeArtifact(
            biome.Guid,
            RuntimeVariant,
            output.Path,
            biome.Entries.Count,
            output.SizeInBytes,
            Array.AsReadOnly(dependencies));
    }

    public static bool TryLoadCooked(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationBiomeSourceAsset> biomeRef,
        out CookedVegetationBiome biome,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        biome = null!;
        if (!biomeRef.IsValid)
        {
            diagnostic = "[VegetationBiomeAssetCooker] Cooked vegetation biome ref is empty.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(biomeRef.AssetType) &&
            !string.Equals(
                biomeRef.AssetType,
                VegetationAssetTypes.Biome,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationBiomeAssetCooker] Vegetation biome '{biomeRef.Guid:D}' ref has asset type " +
                $"'{biomeRef.AssetType}', expected '{VegetationAssetTypes.Biome}'.";
            return false;
        }

        if (!assetDatabase.TryGetAssetDescriptor(biomeRef.Guid, out AssetDescriptor descriptor) ||
            !string.Equals(
                descriptor.AssetType,
                VegetationAssetTypes.Biome,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostic =
                $"[VegetationBiomeAssetCooker] Vegetation biome '{biomeRef.Guid:D}' has no cataloged " +
                $"'{VegetationAssetTypes.Biome}' identity.";
            return false;
        }

        if (!IsCanonicalPackageId(descriptor.PackageId) ||
            (!string.IsNullOrEmpty(biomeRef.PackageId) &&
             !string.Equals(biomeRef.PackageId, descriptor.PackageId, StringComparison.Ordinal)))
        {
            diagnostic =
                $"[VegetationBiomeAssetCooker] Vegetation biome '{biomeRef.Guid:D}' belongs to package " +
                $"'{descriptor.PackageId}', expected '{biomeRef.PackageId}'.";
            return false;
        }

        if (!assetDatabase.TryLoadCookedAsset(
                biomeRef.Guid,
                RuntimeVariant,
                VegetationAssetTypes.Biome,
                out CookedAssetHandle handle))
        {
            diagnostic =
                $"[VegetationBiomeAssetCooker] Vegetation biome '{biomeRef.Guid:D}' variant " +
                $"'{RuntimeVariant}' is unavailable.";
            return false;
        }

        try
        {
            string diagnosticPath = assetDatabase.TryGetCookedArtifact(
                biomeRef.Guid,
                RuntimeVariant,
                out CookedAssetRecord? artifact)
                ? artifact.Path
                : $"{biomeRef.Guid:D}:{RuntimeVariant}";
            if (!TryReadPayload(
                    biomeRef.Guid,
                    descriptor.PackageId,
                    assetDatabase.GetCookedAssetBytes(handle).Span,
                    diagnosticPath,
                    out biome,
                    out diagnostic))
            {
                return false;
            }

            try
            {
                ValidateCookedDependencies(assetDatabase, biome);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                biome = null!;
                diagnostic =
                    $"[VegetationBiomeAssetCooker] Cooked vegetation biome '{diagnosticPath}' " +
                    $"has an invalid dependency: {ex.Message}";
                return false;
            }
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    internal static byte[] WritePayload(CookedVegetationBiome biome)
    {
        ArgumentNullException.ThrowIfNull(biome);
        ValidateBiome(biome, $"vegetation biome '{biome.Guid:D}'");
        int ruleCount = CountLayerWeightRules(biome);
        string[] strings = BuildCanonicalStrings(biome);
        Dictionary<string, uint> stringIndices = BuildStringIndices(strings);
        byte[] metadata = BuildMetadataSection(biome, ruleCount, stringIndices);
        byte[] entries = BuildEntrySection(biome, stringIndices);
        byte[] rules = BuildLayerWeightRuleSection(biome, stringIndices);
        VegetationCookedSectionPayload[] sections =
        [
            new(
                (uint)CookedVegetationBiomeSectionType.Metadata,
                VegetationCookedSectionFlags.Required,
                1,
                MetadataStride,
                metadata),
            new(
                (uint)CookedVegetationBiomeSectionType.Strings,
                VegetationCookedSectionFlags.Required,
                checked((uint)strings.Length),
                0,
                VegetationCookedContainer.BuildStringSection(strings)),
            new(
                (uint)CookedVegetationBiomeSectionType.Entries,
                VegetationCookedSectionFlags.Required,
                checked((uint)biome.Entries.Count),
                EntryStride,
                entries),
            new(
                (uint)CookedVegetationBiomeSectionType.LayerWeightRules,
                VegetationCookedSectionFlags.Required,
                checked((uint)ruleCount),
                LayerWeightRuleStride,
                rules)
        ];

        byte[] output = VegetationCookedContainer.Build(
            HeaderSize,
            MaxCookedBiomeBytes,
            sections,
            out _);
        Span<byte> header = output.AsSpan(0, HeaderSize);
        s_Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], VegetationCookedContainer.EndianMarker);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], CookedFormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], biome.SourceSchemaVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], HeaderSize);
        VegetationCookedContainer.WriteGuid(header[24..40], biome.Guid);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], biome.Entries.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[44..], ruleCount);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..], sections.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header[56..], checked((ulong)output.Length));
        VegetationCookedContainer.FinalizeHash(output, HeaderSize, HashOffset);
        return output;
    }

    internal static bool TryReadPayload(
        Guid expectedBiomeGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath,
        out CookedVegetationBiome biome,
        out string diagnostic)
    {
        using var zone = Profiler.Zone("Vegetation.ReadBiomePayload");
        try
        {
            biome = ReadPayload(
                expectedBiomeGuid,
                RequireCanonicalPackageId(expectedPackageId, "expected biome package id"),
                bytes,
                diagnosticPath);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or OverflowException or ArgumentException)
        {
            biome = null!;
            diagnostic =
                $"[VegetationBiomeAssetCooker] Cooked vegetation biome '{diagnosticPath}' is invalid: " +
                ex.Message;
            return false;
        }
    }

    internal static VegetationCookedAssetDependency[] BuildDependencies(
        CookedVegetationBiome biome)
    {
        ArgumentNullException.ThrowIfNull(biome);
        ValidateBiome(biome, $"vegetation biome '{biome.Guid:D}'");
        var dependencies = new Dictionary<DependencyKey, VegetationCookedAssetDependency>();
        foreach (CookedVegetationBiomeEntry entry in biome.Entries)
        {
            var dependency = new VegetationCookedAssetDependency(
                entry.Species.Guid,
                entry.Species.PackageId,
                VegetationAssetTypes.Species,
                VegetationSpeciesAssetCooker.RuntimeVariant,
                Required: true);
            dependencies.TryAdd(
                new DependencyKey(
                    dependency.Guid,
                    dependency.PackageId,
                    dependency.AssetType,
                    dependency.Variant),
                dependency);
        }

        VegetationCookedAssetDependency[] output = dependencies.Values.ToArray();
        Array.Sort(output, CompareDependencies);
        return output;
    }

    private static CookedVegetationBiome BuildBiome(VegetationBiomeSourceDescriptor source)
    {
        var entries = new CookedVegetationBiomeEntry[source.Entries.Count];
        for (int entryIndex = 0; entryIndex < source.Entries.Count; entryIndex++)
        {
            VegetationBiomeEntryDescriptor entry = source.Entries[entryIndex];
            var rules = new CookedVegetationLayerWeightRule[entry.LayerWeightRules.Count];
            for (int ruleIndex = 0; ruleIndex < entry.LayerWeightRules.Count; ruleIndex++)
            {
                VegetationLayerWeightRuleDescriptor rule = entry.LayerWeightRules[ruleIndex];
                rules[ruleIndex] = new CookedVegetationLayerWeightRule(
                    rule.LayerId,
                    rule.WeightRange);
            }

            entries[entryIndex] = new CookedVegetationBiomeEntry(
                entry.EntryId,
                new CookedVegetationSpeciesReference(
                    entry.Species.Guid,
                    RequireCanonicalPackageId(
                        entry.Species.PackageId,
                        $"entry {entryIndex} species package id")),
                entry.Density,
                entry.SeedSalt,
                entry.AltitudeRange,
                entry.SlopeRangeDegrees,
                Array.AsReadOnly(rules),
                entry.MinimumSpacing,
                entry.ClusterSize,
                entry.ExclusionPolicy);
        }

        return new CookedVegetationBiome(
            source.Guid,
            RequireCanonicalPackageId(source.PackageId, "biome package id"),
            source.SourceSchemaVersion,
            source.Name,
            source.GlobalSeed,
            Array.AsReadOnly(entries));
    }

    private static CookedVegetationBiome ReadPayload(
        Guid expectedBiomeGuid,
        string expectedPackageId,
        ReadOnlySpan<byte> bytes,
        string diagnosticPath)
    {
        string context = $"vegetation biome '{diagnosticPath}'";
        if (bytes.Length < HeaderSize)
        {
            throw VegetationCookedContainer.Invalid(context, "header is truncated");
        }

        if (!bytes[..8].SequenceEqual(s_Magic))
        {
            throw VegetationCookedContainer.Invalid(context, "magic is not ARIVBIOM");
        }

        uint endian = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        int formatVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        int sourceVersion = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        Guid biomeGuid = VegetationCookedContainer.ReadGuid(bytes[24..40]);
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        int ruleCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        int sectionCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(bytes[52..]);
        ulong declaredSize = BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]);
        if (endian != VegetationCookedContainer.EndianMarker ||
            formatVersion != CookedFormatVersion ||
            sourceVersion is < VegetationBiomeSourceAssetLoader.MinimumSourceSchemaVersion or
                > VegetationBiomeSourceAssetLoader.CurrentSourceSchemaVersion ||
            headerSize != HeaderSize ||
            biomeGuid == Guid.Empty || biomeGuid != expectedBiomeGuid ||
            entryCount is < VegetationBiomeSourceAssetLoader.MinimumEntryCount or
                > VegetationBiomeSourceAssetLoader.MaximumEntryCount ||
            ruleCount is < 0 or > MaxLayerWeightRuleCount ||
            reserved != 0 ||
            declaredSize != checked((ulong)bytes.Length))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "header identity, version, counts, reserved value, or exact size is invalid");
        }

        VegetationCookedContainer.EnsureZero(bytes[96..HeaderSize], context, "reserved header bytes");
        Dictionary<uint, VegetationCookedSectionDescriptor> sections =
            VegetationCookedContainer.ReadDirectory(
                bytes,
                HeaderSize,
                HashOffset,
                sectionCount,
                MaxCookedBiomeBytes,
                s_KnownSections,
                context);
        VegetationCookedSectionDescriptor metadataSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationBiomeSectionType.Metadata,
            MetadataStride,
            1,
            1,
            context);
        VegetationCookedSectionDescriptor stringsSection =
            VegetationCookedContainer.RequireVariableSection(
                sections,
                (uint)CookedVegetationBiomeSectionType.Strings,
                MaxStringCount,
                context);
        VegetationCookedSectionDescriptor entriesSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationBiomeSectionType.Entries,
            EntryStride,
            checked((uint)entryCount),
            VegetationBiomeSourceAssetLoader.MaximumEntryCount,
            context);
        VegetationCookedSectionDescriptor rulesSection = VegetationCookedContainer.RequireSection(
            sections,
            (uint)CookedVegetationBiomeSectionType.LayerWeightRules,
            LayerWeightRuleStride,
            checked((uint)ruleCount),
            MaxLayerWeightRuleCount,
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
        ulong globalSeed = BinaryPrimitives.ReadUInt64LittleEndian(metadata[8..]);
        uint metadataEntryCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[16..]);
        uint metadataRuleCount = BinaryPrimitives.ReadUInt32LittleEndian(metadata[20..]);
        if (metadataEntryCount != checked((uint)entryCount) ||
            metadataRuleCount != checked((uint)ruleCount))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "metadata entry or layer-rule count does not match the header");
        }

        ReadOnlySpan<byte> encodedEntries = VegetationCookedContainer.GetSection(bytes, entriesSection);
        ReadOnlySpan<byte> encodedRules = VegetationCookedContainer.GetSection(bytes, rulesSection);
        var entries = new CookedVegetationBiomeEntry[entryCount];
        int ruleCursor = 0;
        for (int entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            ReadOnlySpan<byte> record = encodedEntries.Slice(entryIndex * EntryStride, EntryStride);
            string entryId = VegetationCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record),
                $"entry {entryIndex} id",
                context);
            string speciesPackageId = VegetationCookedContainer.ReadString(
                strings,
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                $"entry {entryIndex} species package id",
                context);
            Guid speciesGuid = VegetationCookedContainer.ReadGuid(record[8..24]);
            float density = BinaryPrimitives.ReadSingleLittleEndian(record[24..]);
            uint reservedBeforeSeed = BinaryPrimitives.ReadUInt32LittleEndian(record[28..]);
            ulong seedSalt = BinaryPrimitives.ReadUInt64LittleEndian(record[32..]);
            var altitudeRange = new VegetationValueRange(
                BinaryPrimitives.ReadSingleLittleEndian(record[40..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[44..]));
            var slopeRange = new VegetationValueRange(
                BinaryPrimitives.ReadSingleLittleEndian(record[48..]),
                BinaryPrimitives.ReadSingleLittleEndian(record[52..]));
            float minimumSpacing = BinaryPrimitives.ReadSingleLittleEndian(record[56..]);
            int clusterSize = BinaryPrimitives.ReadInt32LittleEndian(record[60..]);
            var exclusionPolicy =
                (VegetationExclusionPolicy)BinaryPrimitives.ReadUInt32LittleEndian(record[64..]);
            uint firstRule = BinaryPrimitives.ReadUInt32LittleEndian(record[68..]);
            uint entryRuleCount = BinaryPrimitives.ReadUInt32LittleEndian(record[72..]);
            uint reservedTail = BinaryPrimitives.ReadUInt32LittleEndian(record[76..]);
            if (reservedBeforeSeed != 0 ||
                reservedTail != 0 ||
                firstRule != checked((uint)ruleCursor) ||
                entryRuleCount > VegetationBiomeSourceAssetLoader.MaximumLayerWeightRuleCount ||
                checked((ulong)firstRule + entryRuleCount) > checked((ulong)ruleCount))
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"entry '{entryIndex}' rule range or reserved data is invalid");
            }

            var rules = new CookedVegetationLayerWeightRule[checked((int)entryRuleCount)];
            for (int localRuleIndex = 0; localRuleIndex < rules.Length; localRuleIndex++)
            {
                int ruleIndex = checked(ruleCursor + localRuleIndex);
                ReadOnlySpan<byte> ruleRecord = encodedRules.Slice(
                    ruleIndex * LayerWeightRuleStride,
                    LayerWeightRuleStride);
                string layerId = VegetationCookedContainer.ReadString(
                    strings,
                    BinaryPrimitives.ReadUInt32LittleEndian(ruleRecord),
                    $"entry {entryIndex} layer rule {localRuleIndex} id",
                    context);
                var weightRange = new VegetationValueRange(
                    BinaryPrimitives.ReadSingleLittleEndian(ruleRecord[4..]),
                    BinaryPrimitives.ReadSingleLittleEndian(ruleRecord[8..]));
                if (BinaryPrimitives.ReadUInt32LittleEndian(ruleRecord[12..]) != 0)
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"entry '{entryIndex}' layer rule '{localRuleIndex}' reserved data is nonzero");
                }

                rules[localRuleIndex] = new CookedVegetationLayerWeightRule(layerId, weightRange);
            }

            ruleCursor = checked(ruleCursor + rules.Length);
            entries[entryIndex] = new CookedVegetationBiomeEntry(
                entryId,
                new CookedVegetationSpeciesReference(speciesGuid, speciesPackageId),
                density,
                seedSalt,
                altitudeRange,
                slopeRange,
                Array.AsReadOnly(rules),
                minimumSpacing,
                clusterSize,
                exclusionPolicy);
        }

        if (ruleCursor != ruleCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "layer-rule records are not consumed exactly once in authored entry order");
        }

        var biome = new CookedVegetationBiome(
            biomeGuid,
            packageId,
            sourceVersion,
            name,
            globalSeed,
            Array.AsReadOnly(entries));
        ValidateBiome(biome, context);
        if (!string.Equals(biome.PackageId, expectedPackageId, StringComparison.Ordinal))
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"package identity '{biome.PackageId}' does not match expected '{expectedPackageId}'");
        }

        ValidateCanonicalStrings(strings, BuildCanonicalStrings(biome), context);
        return biome;
    }

    private static byte[] BuildMetadataSection(
        CookedVegetationBiome biome,
        int ruleCount,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[MetadataStride];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), stringIndices[biome.PackageId]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), stringIndices[biome.Name]);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), biome.GlobalSeed);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), checked((uint)biome.Entries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), checked((uint)ruleCount));
        return bytes;
    }

    private static byte[] BuildEntrySection(
        CookedVegetationBiome biome,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        byte[] bytes = new byte[checked(biome.Entries.Count * EntryStride)];
        int firstRule = 0;
        for (int index = 0; index < biome.Entries.Count; index++)
        {
            CookedVegetationBiomeEntry entry = biome.Entries[index];
            Span<byte> record = bytes.AsSpan(index * EntryStride, EntryStride);
            BinaryPrimitives.WriteUInt32LittleEndian(record, stringIndices[entry.EntryId]);
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], stringIndices[entry.Species.PackageId]);
            VegetationCookedContainer.WriteGuid(record[8..24], entry.Species.Guid);
            BinaryPrimitives.WriteSingleLittleEndian(record[24..], entry.Density);
            BinaryPrimitives.WriteUInt64LittleEndian(record[32..], entry.SeedSalt);
            BinaryPrimitives.WriteSingleLittleEndian(record[40..], entry.AltitudeRange.Minimum);
            BinaryPrimitives.WriteSingleLittleEndian(record[44..], entry.AltitudeRange.Maximum);
            BinaryPrimitives.WriteSingleLittleEndian(record[48..], entry.SlopeRangeDegrees.Minimum);
            BinaryPrimitives.WriteSingleLittleEndian(record[52..], entry.SlopeRangeDegrees.Maximum);
            BinaryPrimitives.WriteSingleLittleEndian(record[56..], entry.MinimumSpacing);
            BinaryPrimitives.WriteInt32LittleEndian(record[60..], entry.ClusterSize);
            BinaryPrimitives.WriteUInt32LittleEndian(record[64..], (uint)entry.ExclusionPolicy);
            BinaryPrimitives.WriteUInt32LittleEndian(record[68..], checked((uint)firstRule));
            BinaryPrimitives.WriteUInt32LittleEndian(
                record[72..],
                checked((uint)entry.LayerWeightRules.Count));
            firstRule = checked(firstRule + entry.LayerWeightRules.Count);
        }

        return bytes;
    }

    private static byte[] BuildLayerWeightRuleSection(
        CookedVegetationBiome biome,
        IReadOnlyDictionary<string, uint> stringIndices)
    {
        int ruleCount = CountLayerWeightRules(biome);
        byte[] bytes = new byte[checked(ruleCount * LayerWeightRuleStride)];
        int outputIndex = 0;
        foreach (CookedVegetationBiomeEntry entry in biome.Entries)
        {
            foreach (CookedVegetationLayerWeightRule rule in entry.LayerWeightRules)
            {
                Span<byte> record = bytes.AsSpan(
                    outputIndex * LayerWeightRuleStride,
                    LayerWeightRuleStride);
                BinaryPrimitives.WriteUInt32LittleEndian(record, stringIndices[rule.LayerId]);
                BinaryPrimitives.WriteSingleLittleEndian(record[4..], rule.WeightRange.Minimum);
                BinaryPrimitives.WriteSingleLittleEndian(record[8..], rule.WeightRange.Maximum);
                outputIndex++;
            }
        }

        return bytes;
    }

    private static int CountLayerWeightRules(CookedVegetationBiome biome)
    {
        int count = 0;
        foreach (CookedVegetationBiomeEntry entry in biome.Entries)
        {
            count = checked(count + entry.LayerWeightRules.Count);
        }

        return count;
    }

    private static void ValidateBiome(CookedVegetationBiome biome, string context)
    {
        ArgumentNullException.ThrowIfNull(biome);
        if (biome.Guid == Guid.Empty ||
            !IsCanonicalPackageId(biome.PackageId) ||
            biome.SourceSchemaVersion is < VegetationBiomeSourceAssetLoader.MinimumSourceSchemaVersion or
                > VegetationBiomeSourceAssetLoader.CurrentSourceSchemaVersion ||
            !IsCanonicalName(biome.Name) ||
            biome.Entries == null ||
            biome.Entries.Count is < VegetationBiomeSourceAssetLoader.MinimumEntryCount or
                > VegetationBiomeSourceAssetLoader.MaximumEntryCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                "identity, metadata, or entry count is invalid");
        }

        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        int totalRuleCount = 0;
        for (int entryIndex = 0; entryIndex < biome.Entries.Count; entryIndex++)
        {
            CookedVegetationBiomeEntry? entry = biome.Entries[entryIndex];
            if (entry == null ||
                !IsAsciiIdentifier(
                    entry.EntryId,
                    VegetationBiomeSourceAssetLoader.MaximumEntryIdLength) ||
                !entryIds.Add(entry.EntryId) ||
                entry.Species.Guid == Guid.Empty ||
                !IsCanonicalPackageId(entry.Species.PackageId) ||
                !IsBounded(
                    entry.Density,
                    VegetationBiomeSourceAssetLoader.MinimumDensity,
                    VegetationBiomeSourceAssetLoader.MaximumDensity) ||
                !IsRange(
                    entry.AltitudeRange,
                    VegetationBiomeSourceAssetLoader.MinimumAltitude,
                    VegetationBiomeSourceAssetLoader.MaximumAltitude) ||
                !IsRange(
                    entry.SlopeRangeDegrees,
                    VegetationBiomeSourceAssetLoader.MinimumSlopeDegrees,
                    VegetationBiomeSourceAssetLoader.MaximumSlopeDegrees) ||
                !IsBounded(
                    entry.MinimumSpacing,
                    VegetationBiomeSourceAssetLoader.MinimumSpacing,
                    VegetationBiomeSourceAssetLoader.MaximumSpacing) ||
                entry.ClusterSize is < VegetationBiomeSourceAssetLoader.MinimumClusterSize or
                    > VegetationBiomeSourceAssetLoader.MaximumClusterSize ||
                !Enum.IsDefined(entry.ExclusionPolicy) ||
                entry.LayerWeightRules == null ||
                entry.LayerWeightRules.Count >
                    VegetationBiomeSourceAssetLoader.MaximumLayerWeightRuleCount)
            {
                throw VegetationCookedContainer.Invalid(
                    context,
                    $"entry '{entryIndex}' identity, ranges, policy, or rule count is invalid");
            }

            var layerIds = new HashSet<string>(StringComparer.Ordinal);
            for (int ruleIndex = 0; ruleIndex < entry.LayerWeightRules.Count; ruleIndex++)
            {
                CookedVegetationLayerWeightRule? rule = entry.LayerWeightRules[ruleIndex];
                if (rule == null ||
                    !IsAsciiIdentifier(
                        rule.LayerId,
                        VegetationBiomeSourceAssetLoader.MaximumLayerIdLength) ||
                    !layerIds.Add(rule.LayerId) ||
                    !IsRange(
                        rule.WeightRange,
                        VegetationBiomeSourceAssetLoader.MinimumLayerWeight,
                        VegetationBiomeSourceAssetLoader.MaximumLayerWeight))
                {
                    throw VegetationCookedContainer.Invalid(
                        context,
                        $"entry '{entryIndex}' layer rule '{ruleIndex}' is duplicate or invalid");
                }
            }

            totalRuleCount = checked(totalRuleCount + entry.LayerWeightRules.Count);
        }

        if (totalRuleCount > MaxLayerWeightRuleCount)
        {
            throw VegetationCookedContainer.Invalid(
                context,
                $"layer-rule count '{totalRuleCount}' exceeds '{MaxLayerWeightRuleCount}'");
        }
    }

    private static void ValidateSourceDependencies(
        IAssetDatabase assetDatabase,
        VegetationBiomeSourceDescriptor source)
    {
        for (int index = 0; index < source.Entries.Count; index++)
        {
            AssetRef<VegetationSpeciesSourceAsset> species = source.Entries[index].Species;
            ValidateDatabaseDependency(
                assetDatabase,
                species.Guid,
                species.PackageId,
                $"vegetation biome '{source.Guid:D}' entry {index} species");
        }
    }

    private static void ValidateCookedDependencies(
        IAssetDatabase assetDatabase,
        CookedVegetationBiome biome)
    {
        for (int index = 0; index < biome.Entries.Count; index++)
        {
            CookedVegetationSpeciesReference species = biome.Entries[index].Species;
            ValidateDatabaseDependency(
                assetDatabase,
                species.Guid,
                species.PackageId,
                $"vegetation biome '{biome.Guid:D}' entry {index} species");
        }
    }

    private static void ValidateDatabaseDependency(
        IAssetDatabase assetDatabase,
        Guid guid,
        string packageId,
        string context)
    {
        if (guid == Guid.Empty ||
            !assetDatabase.TryGetAssetDescriptor(guid, out AssetDescriptor descriptor))
        {
            throw new InvalidOperationException($"{context} '{guid:D}' is missing from the asset database.");
        }

        if (descriptor.Guid != guid ||
            !string.Equals(descriptor.PackageId, packageId, StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.AssetType,
                VegetationAssetTypes.Species,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{context} '{guid:D}' resolves to '{descriptor.PackageId}:{descriptor.AssetType}', " +
                $"expected '{packageId}:{VegetationAssetTypes.Species}'.");
        }
    }

    private static string[] BuildCanonicalStrings(CookedVegetationBiome biome)
    {
        var strings = new SortedSet<string>(StringComparer.Ordinal)
        {
            biome.PackageId,
            biome.Name
        };
        foreach (CookedVegetationBiomeEntry entry in biome.Entries)
        {
            strings.Add(entry.EntryId);
            strings.Add(entry.Species.PackageId);
            foreach (CookedVegetationLayerWeightRule rule in entry.LayerWeightRules)
            {
                strings.Add(rule.LayerId);
            }
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
        value.Length <= VegetationBiomeSourceAssetLoader.MaximumNameLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsCanonicalPackageId(string value) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= VegetationBiomeSourceAssetLoader.MaximumPackageIdLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_');

    private static bool IsAsciiIdentifier(string value, int maximumLength) =>
        !string.IsNullOrEmpty(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or '.' or '-' or '_');

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

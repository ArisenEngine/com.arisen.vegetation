using ArisenEngine.Core.Assets;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArisenEngine.Vegetation.Assets;

public static class VegetationSpeciesSourceAssetLoader
{
    public const int MinimumSourceSchemaVersion = 1;
    public const int CurrentSourceSchemaVersion = 1;
    public const int MinimumLodCount = 1;
    public const int MaximumLodCount = 8;
    public const int MaximumNameLength = 512;
    public const int MaximumPackageIdLength = 128;
    public const float MinimumMaximumDistance = 0.001f;
    public const float MaximumMaximumDistance = 1_000_000.0f;
    public const float MinimumMaximumScreenError = 0.0f;
    public const float MaximumMaximumScreenError = 65_536.0f;
    public const float MinimumScale = 0.001f;
    public const float MaximumScale = 1_000.0f;
    public const float MinimumYawDegrees = -360.0f;
    public const float MaximumYawDegrees = 360.0f;
    public const float MinimumTiltDegrees = -90.0f;
    public const float MaximumTiltDegrees = 90.0f;
    public const float MinimumCapsuleRadius = 0.001f;
    public const float MaximumCapsuleRadius = 1_000.0f;
    public const float MinimumCapsuleHalfHeight = 0.001f;
    public const float MaximumCapsuleHalfHeight = 1_000.0f;
    public const float MinimumCollisionDistance = 0.001f;
    public const float MaximumCollisionDistance = 1_000_000.0f;
    public const float MinimumWindResponse = 0.0f;
    public const float MaximumWindResponse = 1.0f;

    public static VegetationSpeciesSourceDescriptor LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationSpeciesSourceAsset> speciesRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!speciesRef.IsValid)
        {
            throw new ArgumentException("Vegetation species asset ref is empty.", nameof(speciesRef));
        }

        string? expectedPackageId = null;
        if (speciesRef.PackageId is { Length: > 0 })
        {
            expectedPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
                speciesRef.PackageId,
                MaximumPackageIdLength,
                "vegetation species asset-ref package id");
        }

        if (!assetDatabase.TryGetAsset(speciesRef, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, VegetationAssetTypes.Species, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationSpeciesSourceAssetLoader] Vegetation species '{speciesRef.Guid:D}' is not indexed as " +
                $"'{VegetationAssetTypes.Species}'.");
        }

        string actualPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
            sourceAsset.PackageId,
            MaximumPackageIdLength,
            "vegetation species indexed package id");
        if (expectedPackageId != null &&
            !string.Equals(expectedPackageId, actualPackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationSpeciesSourceAssetLoader] Vegetation species '{speciesRef.Guid:D}' belongs to package " +
                $"'{actualPackageId}', expected '{expectedPackageId}'.");
        }

        return LoadSource(sourceAsset);
    }

    public static VegetationSpeciesSourceDescriptor LoadSource(AssetRecord sourceAsset)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        if (!string.Equals(sourceAsset.AssetType, VegetationAssetTypes.Species, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationSpeciesSourceAssetLoader] Asset '{sourceAsset.Guid:D}' has type " +
                $"'{sourceAsset.AssetType}', expected '{VegetationAssetTypes.Species}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new FileNotFoundException(
                $"[VegetationSpeciesSourceAssetLoader] Vegetation species source is missing: {sourceAsset.SourcePath}",
                sourceAsset.SourcePath);
        }

        return LoadSourceText(
            sourceAsset.Guid,
            sourceAsset.PackageId,
            sourceAsset.SourcePath,
            File.ReadAllText(sourceAsset.SourcePath));
    }

    public static VegetationSpeciesSourceDescriptor LoadSourceText(
        Guid expectedGuid,
        string packageId,
        string path,
        string text)
    {
        if (expectedGuid == Guid.Empty)
        {
            throw new ArgumentException("Vegetation species requires a stable asset GUID.", nameof(expectedGuid));
        }

        VegetationSourceValidation.RequireSourceArguments(path, text);
        string canonicalPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
            packageId,
            MaximumPackageIdLength,
            "vegetation species package id");
        VegetationSpeciesSourceDocument source = VegetationSourceYaml.DeserializeSpecies(path, text);

        if (source.Version is < MinimumSourceSchemaVersion or > CurrentSourceSchemaVersion)
        {
            throw Invalid(
                path,
                $"schema version '{source.Version}' is unsupported; expected " +
                $"{MinimumSourceSchemaVersion}..{CurrentSourceSchemaVersion}");
        }

        if (source.SpeciesGuid == Guid.Empty || source.SpeciesGuid != expectedGuid)
        {
            throw Invalid(
                path,
                $"declares GUID '{source.SpeciesGuid:D}', expected asset GUID '{expectedGuid:D}'");
        }

        string name = VegetationSourceValidation.NormalizeName(
            source.Name,
            path,
            "vegetation species",
            MaximumNameLength);
        if (source.Lods == null ||
            source.Lods.Count is < MinimumLodCount or > MaximumLodCount)
        {
            throw Invalid(path, $"must declare {MinimumLodCount}..{MaximumLodCount} ordered LODs");
        }

        var lods = new VegetationSpeciesLodDescriptor[source.Lods.Count];
        float previousDistance = float.NegativeInfinity;
        float previousScreenError = float.NegativeInfinity;
        for (int index = 0; index < source.Lods.Count; index++)
        {
            VegetationSpeciesLodSource lod = source.Lods[index];
            float maximumDistance = lod.MaximumDistance;
            float maximumScreenError = lod.MaximumScreenError;
            if (!VegetationSourceValidation.IsBounded(
                    maximumDistance,
                    MinimumMaximumDistance,
                    MaximumMaximumDistance) ||
                !VegetationSourceValidation.IsBounded(
                    maximumScreenError,
                    MinimumMaximumScreenError,
                    MaximumMaximumScreenError))
            {
                throw Invalid(path, $"Lods[{index}] has non-finite or out-of-range distance/error limits");
            }

            if (maximumDistance <= previousDistance || maximumScreenError <= previousScreenError)
            {
                throw Invalid(
                    path,
                    $"Lods[{index}] MaximumDistance and MaximumScreenError must both be strictly increasing");
            }

            lods[index] = new VegetationSpeciesLodDescriptor(
                CreateAssetRef<MeshSourceAsset>(lod.Mesh, "Mesh", path, $"Lods[{index}].Mesh"),
                CreateAssetRef<MaterialSourceAsset>(lod.Material, "Material", path, $"Lods[{index}].Material"),
                maximumDistance,
                maximumScreenError);
            previousDistance = maximumDistance;
            previousScreenError = maximumScreenError;
        }

        VegetationShadowPolicy shadowPolicy = ParseEnum<VegetationShadowPolicy>(
            source.ShadowPolicy,
            path,
            "ShadowPolicy");
        VegetationValueRange scaleRange = ValidateRange(
            source.ScaleRange,
            path,
            "ScaleRange",
            MinimumScale,
            MaximumScale);
        VegetationValueRange yawRange = ValidateRange(
            source.YawRangeDegrees,
            path,
            "YawRangeDegrees",
            MinimumYawDegrees,
            MaximumYawDegrees);
        VegetationValueRange tiltRange = ValidateRange(
            source.TiltRangeDegrees,
            path,
            "TiltRangeDegrees",
            MinimumTiltDegrees,
            MaximumTiltDegrees);
        VegetationCollisionPromotionDescriptor collision = ValidateCollision(
            source.CollisionPromotion,
            path);
        if (!VegetationSourceValidation.IsBounded(
                source.WindResponse,
                MinimumWindResponse,
                MaximumWindResponse))
        {
            throw Invalid(path, "WindResponse must be finite and within the supported range");
        }

        return new VegetationSpeciesSourceDescriptor(
            expectedGuid,
            canonicalPackageId,
            source.Version,
            name,
            Array.AsReadOnly(lods),
            shadowPolicy,
            scaleRange,
            yawRange,
            tiltRange,
            collision,
            source.WindResponse);
    }

    private static AssetRef<TAsset> CreateAssetRef<TAsset>(
        VegetationAssetReferenceSource? source,
        string assetType,
        string path,
        string context)
    {
        if (source == null || source.Guid == Guid.Empty)
        {
            throw Invalid(path, $"{context}.Guid must be non-empty");
        }

        string packageId = VegetationSourceValidation.RequireCanonicalPackageId(
            source.PackageId,
            MaximumPackageIdLength,
            $"{context} package id");
        return new AssetRef<TAsset>(source.Guid, assetType, packageId);
    }

    private static VegetationValueRange ValidateRange(
        VegetationRangeSource? source,
        string path,
        string context,
        float minimum,
        float maximum)
    {
        if (source == null ||
            !VegetationSourceValidation.IsBounded(source.Minimum, minimum, maximum) ||
            !VegetationSourceValidation.IsBounded(source.Maximum, minimum, maximum) ||
            source.Minimum > source.Maximum)
        {
            throw Invalid(path, $"{context} must be finite, ordered, and within {minimum}..{maximum}");
        }

        return new VegetationValueRange(source.Minimum, source.Maximum);
    }

    private static VegetationCollisionPromotionDescriptor ValidateCollision(
        VegetationCollisionPromotionSource? source,
        string path)
    {
        if (source == null)
        {
            throw Invalid(path, "requires CollisionPromotion");
        }

        VegetationCollisionPromotionMode mode = ParseEnum<VegetationCollisionPromotionMode>(
            source.Mode,
            path,
            "CollisionPromotion.Mode");
        if (mode == VegetationCollisionPromotionMode.None)
        {
            if (source.CapsuleRadius != 0.0f ||
                source.CapsuleHalfHeight != 0.0f ||
                source.MaximumDistance != 0.0f)
            {
                throw Invalid(
                    path,
                    "CollisionPromotion None requires zero CapsuleRadius, CapsuleHalfHeight, and MaximumDistance");
            }

            return new VegetationCollisionPromotionDescriptor(mode, 0.0f, 0.0f, 0.0f);
        }

        if (!VegetationSourceValidation.IsBounded(
                source.CapsuleRadius,
                MinimumCapsuleRadius,
                MaximumCapsuleRadius) ||
            !VegetationSourceValidation.IsBounded(
                source.CapsuleHalfHeight,
                MinimumCapsuleHalfHeight,
                MaximumCapsuleHalfHeight) ||
            source.CapsuleHalfHeight < source.CapsuleRadius ||
            !VegetationSourceValidation.IsBounded(
                source.MaximumDistance,
                MinimumCollisionDistance,
                MaximumCollisionDistance))
        {
            throw Invalid(path, "CollisionPromotion Capsule contains invalid shape or distance values");
        }

        return new VegetationCollisionPromotionDescriptor(
            mode,
            source.CapsuleRadius,
            source.CapsuleHalfHeight,
            source.MaximumDistance);
    }

    private static TEnum ParseEnum<TEnum>(string? value, string path, string field)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrEmpty(value) ||
            !Enum.TryParse(value, ignoreCase: false, out TEnum parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw Invalid(path, $"{field} value '{value}' is unsupported");
        }

        return parsed;
    }

    private static InvalidDataException Invalid(string path, string diagnostic) =>
        new($"[VegetationSpeciesSourceAssetLoader] Vegetation species '{path}' {diagnostic}.");
}

public static class VegetationBiomeSourceAssetLoader
{
    public const int MinimumSourceSchemaVersion = 1;
    public const int CurrentSourceSchemaVersion = 1;
    public const int MinimumEntryCount = 1;
    public const int MaximumEntryCount = 256;
    public const int MaximumLayerWeightRuleCount = 4;
    public const int MaximumEntryIdLength = 64;
    public const int MaximumLayerIdLength = 64;
    public const int MaximumNameLength = 512;
    public const int MaximumPackageIdLength = 128;
    public const float MinimumDensity = 0.0f;
    public const float MaximumDensity = 1_000_000.0f;
    public const float MinimumAltitude = -100_000.0f;
    public const float MaximumAltitude = 100_000.0f;
    public const float MinimumSlopeDegrees = 0.0f;
    public const float MaximumSlopeDegrees = 90.0f;
    public const float MinimumLayerWeight = 0.0f;
    public const float MaximumLayerWeight = 1.0f;
    public const float MinimumSpacing = 0.001f;
    public const float MaximumSpacing = 1_000_000.0f;
    public const int MinimumClusterSize = 1;
    public const int MaximumClusterSize = 4_096;

    public static VegetationBiomeSourceDescriptor LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationBiomeSourceAsset> biomeRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!biomeRef.IsValid)
        {
            throw new ArgumentException("Vegetation biome asset ref is empty.", nameof(biomeRef));
        }

        string? expectedPackageId = null;
        if (biomeRef.PackageId is { Length: > 0 })
        {
            expectedPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
                biomeRef.PackageId,
                MaximumPackageIdLength,
                "vegetation biome asset-ref package id");
        }

        if (!assetDatabase.TryGetAsset(biomeRef, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, VegetationAssetTypes.Biome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationBiomeSourceAssetLoader] Vegetation biome '{biomeRef.Guid:D}' is not indexed as " +
                $"'{VegetationAssetTypes.Biome}'.");
        }

        string actualPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
            sourceAsset.PackageId,
            MaximumPackageIdLength,
            "vegetation biome indexed package id");
        if (expectedPackageId != null &&
            !string.Equals(expectedPackageId, actualPackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationBiomeSourceAssetLoader] Vegetation biome '{biomeRef.Guid:D}' belongs to package " +
                $"'{actualPackageId}', expected '{expectedPackageId}'.");
        }

        return LoadSource(sourceAsset);
    }

    public static VegetationBiomeSourceDescriptor LoadSource(AssetRecord sourceAsset)
    {
        ArgumentNullException.ThrowIfNull(sourceAsset);
        if (!string.Equals(sourceAsset.AssetType, VegetationAssetTypes.Biome, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationBiomeSourceAssetLoader] Asset '{sourceAsset.Guid:D}' has type " +
                $"'{sourceAsset.AssetType}', expected '{VegetationAssetTypes.Biome}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new FileNotFoundException(
                $"[VegetationBiomeSourceAssetLoader] Vegetation biome source is missing: {sourceAsset.SourcePath}",
                sourceAsset.SourcePath);
        }

        return LoadSourceText(
            sourceAsset.Guid,
            sourceAsset.PackageId,
            sourceAsset.SourcePath,
            File.ReadAllText(sourceAsset.SourcePath));
    }

    public static VegetationBiomeSourceDescriptor LoadSourceText(
        Guid expectedGuid,
        string packageId,
        string path,
        string text)
    {
        if (expectedGuid == Guid.Empty)
        {
            throw new ArgumentException("Vegetation biome requires a stable asset GUID.", nameof(expectedGuid));
        }

        VegetationSourceValidation.RequireSourceArguments(path, text);
        string canonicalPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
            packageId,
            MaximumPackageIdLength,
            "vegetation biome package id");
        VegetationBiomeSourceDocument source = VegetationSourceYaml.DeserializeBiome(path, text);

        if (source.Version is < MinimumSourceSchemaVersion or > CurrentSourceSchemaVersion)
        {
            throw Invalid(
                path,
                $"schema version '{source.Version}' is unsupported; expected " +
                $"{MinimumSourceSchemaVersion}..{CurrentSourceSchemaVersion}");
        }

        if (source.BiomeGuid == Guid.Empty || source.BiomeGuid != expectedGuid)
        {
            throw Invalid(
                path,
                $"declares GUID '{source.BiomeGuid:D}', expected asset GUID '{expectedGuid:D}'");
        }

        string name = VegetationSourceValidation.NormalizeName(
            source.Name,
            path,
            "vegetation biome",
            MaximumNameLength);
        if (source.Entries == null ||
            source.Entries.Count is < MinimumEntryCount or > MaximumEntryCount)
        {
            throw Invalid(path, $"must declare {MinimumEntryCount}..{MaximumEntryCount} ordered entries");
        }

        var entries = new VegetationBiomeEntryDescriptor[source.Entries.Count];
        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < source.Entries.Count; index++)
        {
            VegetationBiomeEntrySource entry = source.Entries[index];
            string entryId = VegetationSourceValidation.RequireAsciiIdentifier(
                entry.EntryId,
                MaximumEntryIdLength,
                $"Vegetation biome '{path}' Entries[{index}].EntryId");
            if (!entryIds.Add(entryId))
            {
                throw Invalid(path, $"contains duplicate EntryId '{entryId}'");
            }

            AssetRef<VegetationSpeciesSourceAsset> species = CreateSpeciesRef(
                entry.Species,
                path,
                index);
            if (!VegetationSourceValidation.IsBounded(entry.Density, MinimumDensity, MaximumDensity))
            {
                throw Invalid(path, $"Entries[{index}].Density must be finite and within the supported range");
            }

            VegetationValueRange altitudeRange = ValidateRange(
                entry.AltitudeRange,
                path,
                index,
                "AltitudeRange",
                MinimumAltitude,
                MaximumAltitude);
            VegetationValueRange slopeRange = ValidateRange(
                entry.SlopeRangeDegrees,
                path,
                index,
                "SlopeRangeDegrees",
                MinimumSlopeDegrees,
                MaximumSlopeDegrees);
            IReadOnlyList<VegetationLayerWeightRuleDescriptor> layerRules = ValidateLayerRules(
                entry.LayerWeightRules,
                path,
                index);
            if (!VegetationSourceValidation.IsBounded(
                    entry.MinimumSpacing,
                    MinimumSpacing,
                    MaximumSpacing))
            {
                throw Invalid(path, $"Entries[{index}].MinimumSpacing must be finite and positive");
            }

            if (entry.ClusterSize is < MinimumClusterSize or > MaximumClusterSize)
            {
                throw Invalid(
                    path,
                    $"Entries[{index}].ClusterSize must be within {MinimumClusterSize}..{MaximumClusterSize}");
            }

            VegetationExclusionPolicy exclusionPolicy = ParseExclusionPolicy(
                entry.ExclusionPolicy,
                path,
                index);
            entries[index] = new VegetationBiomeEntryDescriptor(
                entryId,
                species,
                entry.Density,
                entry.SeedSalt,
                altitudeRange,
                slopeRange,
                layerRules,
                entry.MinimumSpacing,
                entry.ClusterSize,
                exclusionPolicy);
        }

        return new VegetationBiomeSourceDescriptor(
            expectedGuid,
            canonicalPackageId,
            source.Version,
            name,
            source.GlobalSeed,
            Array.AsReadOnly(entries));
    }

    private static AssetRef<VegetationSpeciesSourceAsset> CreateSpeciesRef(
        VegetationAssetReferenceSource? source,
        string path,
        int entryIndex)
    {
        if (source == null || source.Guid == Guid.Empty)
        {
            throw Invalid(path, $"Entries[{entryIndex}].Species.Guid must be non-empty");
        }

        string packageId = VegetationSourceValidation.RequireCanonicalPackageId(
            source.PackageId,
            MaximumPackageIdLength,
            $"Entries[{entryIndex}].Species package id");
        return new AssetRef<VegetationSpeciesSourceAsset>(
            source.Guid,
            VegetationAssetTypes.Species,
            packageId);
    }

    private static VegetationValueRange ValidateRange(
        VegetationRangeSource? source,
        string path,
        int entryIndex,
        string field,
        float minimum,
        float maximum)
    {
        if (source == null ||
            !VegetationSourceValidation.IsBounded(source.Minimum, minimum, maximum) ||
            !VegetationSourceValidation.IsBounded(source.Maximum, minimum, maximum) ||
            source.Minimum > source.Maximum)
        {
            throw Invalid(
                path,
                $"Entries[{entryIndex}].{field} must be finite, ordered, and within {minimum}..{maximum}");
        }

        return new VegetationValueRange(source.Minimum, source.Maximum);
    }

    private static IReadOnlyList<VegetationLayerWeightRuleDescriptor> ValidateLayerRules(
        List<VegetationLayerWeightRuleSource>? source,
        string path,
        int entryIndex)
    {
        if (source == null || source.Count > MaximumLayerWeightRuleCount)
        {
            throw Invalid(
                path,
                $"Entries[{entryIndex}].LayerWeightRules may contain at most {MaximumLayerWeightRuleCount} rules");
        }

        var rules = new VegetationLayerWeightRuleDescriptor[source.Count];
        var layerIds = new HashSet<string>(StringComparer.Ordinal);
        for (int ruleIndex = 0; ruleIndex < source.Count; ruleIndex++)
        {
            VegetationLayerWeightRuleSource rule = source[ruleIndex];
            string layerId = VegetationSourceValidation.RequireAsciiIdentifier(
                rule.LayerId,
                MaximumLayerIdLength,
                $"Vegetation biome '{path}' Entries[{entryIndex}].LayerWeightRules[{ruleIndex}].LayerId");
            if (!layerIds.Add(layerId))
            {
                throw Invalid(
                    path,
                    $"Entries[{entryIndex}] contains duplicate layer-weight rule '{layerId}'");
            }

            VegetationValueRange weightRange = ValidateRange(
                rule.WeightRange,
                path,
                entryIndex,
                $"LayerWeightRules[{ruleIndex}].WeightRange",
                MinimumLayerWeight,
                MaximumLayerWeight);
            rules[ruleIndex] = new VegetationLayerWeightRuleDescriptor(layerId, weightRange);
        }

        return Array.AsReadOnly(rules);
    }

    private static VegetationExclusionPolicy ParseExclusionPolicy(
        string? value,
        string path,
        int entryIndex)
    {
        if (string.IsNullOrEmpty(value) ||
            !Enum.TryParse(value, ignoreCase: false, out VegetationExclusionPolicy parsed) ||
            !Enum.IsDefined(parsed))
        {
            throw Invalid(path, $"Entries[{entryIndex}].ExclusionPolicy value '{value}' is unsupported");
        }

        return parsed;
    }

    private static InvalidDataException Invalid(string path, string diagnostic) =>
        new($"[VegetationBiomeSourceAssetLoader] Vegetation biome '{path}' {diagnostic}.");
}

internal static class VegetationSourceValidation
{
    public static void RequireSourceArguments(string path, string text)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Vegetation source path must be non-empty.", nameof(path));
        }

        ArgumentNullException.ThrowIfNull(text);
    }

    public static string RequireCanonicalPackageId(
        string? packageId,
        int maximumLength,
        string context)
    {
        if (string.IsNullOrEmpty(packageId) ||
            packageId.Length > maximumLength ||
            packageId.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= '0' and <= '9') &&
                character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException(
                $"{context} '{packageId}' must be canonical lowercase ASCII using only letters, digits, '.', '-', or '_'.");
        }

        return packageId;
    }

    public static string NormalizeName(
        string? name,
        string path,
        string context,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException($"{context} '{path}' requires a non-empty name.");
        }

        string normalized = name.Trim();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new InvalidDataException(
                $"{context} '{path}' requires a name no longer than {maximumLength} characters without controls.");
        }

        return normalized;
    }

    public static string RequireAsciiIdentifier(string? value, int maximumLength, string context)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > maximumLength ||
            value.Any(character =>
                !(character is >= 'a' and <= 'z') &&
                !(character is >= 'A' and <= 'Z') &&
                !(character is >= '0' and <= '9') &&
                character is not '.' and not '-' and not '_'))
        {
            throw new InvalidDataException(
                $"{context} '{value}' must use 1..{maximumLength} ASCII letters, digits, '.', '-', or '_'.");
        }

        return value;
    }

    public static bool IsBounded(float value, float minimum, float maximum) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;
}

internal static class VegetationSourceYaml
{
    public static VegetationSpeciesSourceDocument DeserializeSpecies(string path, string text)
    {
        YamlMappingNode root = ParseRoot(path, text, "vegetation species");
        ValidateMapping(
            root,
            path,
            "vegetation species",
            "Version",
            "SpeciesGuid",
            "Name",
            "Lods",
            "ShadowPolicy",
            "ScaleRange",
            "YawRangeDegrees",
            "TiltRangeDegrees",
            "CollisionPromotion",
            "WindResponse");
        RequireScalar(root, "Version", path, "vegetation species");
        RequireScalar(root, "SpeciesGuid", path, "vegetation species");
        RequireScalar(root, "Name", path, "vegetation species");
        RequireScalar(root, "ShadowPolicy", path, "vegetation species");
        RequireScalar(root, "WindResponse", path, "vegetation species");
        ValidateRangeMapping(root, "ScaleRange", path, "species scale range");
        ValidateRangeMapping(root, "YawRangeDegrees", path, "species yaw range");
        ValidateRangeMapping(root, "TiltRangeDegrees", path, "species tilt range");

        YamlSequenceNode lods = RequireSequence(root, "Lods", path, "species LODs");
        for (int index = 0; index < lods.Children.Count; index++)
        {
            YamlMappingNode lod = RequireSequenceMapping(lods, index, path, "species LODs");
            ValidateMapping(lod, path, $"species Lods[{index}]", "Mesh", "Material", "MaximumDistance", "MaximumScreenError");
            ValidateAssetReference(lod, "Mesh", path, $"species Lods[{index}] mesh");
            ValidateAssetReference(lod, "Material", path, $"species Lods[{index}] material");
            RequireScalar(lod, "MaximumDistance", path, $"species Lods[{index}]");
            RequireScalar(lod, "MaximumScreenError", path, $"species Lods[{index}]");
        }

        YamlMappingNode collision = RequireMapping(
            root,
            "CollisionPromotion",
            path,
            "species collision promotion");
        ValidateMapping(
            collision,
            path,
            "species collision promotion",
            "Mode",
            "CapsuleRadius",
            "CapsuleHalfHeight",
            "MaximumDistance");
        RequireScalar(collision, "Mode", path, "species collision promotion");
        RequireScalar(collision, "CapsuleRadius", path, "species collision promotion");
        RequireScalar(collision, "CapsuleHalfHeight", path, "species collision promotion");
        RequireScalar(collision, "MaximumDistance", path, "species collision promotion");
        return Deserialize<VegetationSpeciesSourceDocument>(path, text, "vegetation species");
    }

    public static VegetationBiomeSourceDocument DeserializeBiome(string path, string text)
    {
        YamlMappingNode root = ParseRoot(path, text, "vegetation biome");
        ValidateMapping(root, path, "vegetation biome", "Version", "BiomeGuid", "Name", "GlobalSeed", "Entries");
        RequireScalar(root, "Version", path, "vegetation biome");
        RequireScalar(root, "BiomeGuid", path, "vegetation biome");
        RequireScalar(root, "Name", path, "vegetation biome");
        RequireScalar(root, "GlobalSeed", path, "vegetation biome");

        YamlSequenceNode entries = RequireSequence(root, "Entries", path, "biome entries");
        for (int entryIndex = 0; entryIndex < entries.Children.Count; entryIndex++)
        {
            YamlMappingNode entry = RequireSequenceMapping(entries, entryIndex, path, "biome entries");
            ValidateMapping(
                entry,
                path,
                $"biome Entries[{entryIndex}]",
                "EntryId",
                "Species",
                "Density",
                "SeedSalt",
                "AltitudeRange",
                "SlopeRangeDegrees",
                "LayerWeightRules",
                "MinimumSpacing",
                "ClusterSize",
                "ExclusionPolicy");
            RequireScalar(entry, "EntryId", path, $"biome Entries[{entryIndex}]");
            ValidateAssetReference(entry, "Species", path, $"biome Entries[{entryIndex}] species");
            RequireScalar(entry, "Density", path, $"biome Entries[{entryIndex}]");
            RequireScalar(entry, "SeedSalt", path, $"biome Entries[{entryIndex}]");
            ValidateRangeMapping(entry, "AltitudeRange", path, $"biome Entries[{entryIndex}] altitude range");
            ValidateRangeMapping(entry, "SlopeRangeDegrees", path, $"biome Entries[{entryIndex}] slope range");
            RequireScalar(entry, "MinimumSpacing", path, $"biome Entries[{entryIndex}]");
            RequireScalar(entry, "ClusterSize", path, $"biome Entries[{entryIndex}]");
            RequireScalar(entry, "ExclusionPolicy", path, $"biome Entries[{entryIndex}]");

            YamlSequenceNode rules = RequireSequence(
                entry,
                "LayerWeightRules",
                path,
                $"biome Entries[{entryIndex}] layer-weight rules");
            for (int ruleIndex = 0; ruleIndex < rules.Children.Count; ruleIndex++)
            {
                YamlMappingNode rule = RequireSequenceMapping(
                    rules,
                    ruleIndex,
                    path,
                    $"biome Entries[{entryIndex}] layer-weight rules");
                ValidateMapping(
                    rule,
                    path,
                    $"biome Entries[{entryIndex}].LayerWeightRules[{ruleIndex}]",
                    "LayerId",
                    "WeightRange");
                RequireScalar(
                    rule,
                    "LayerId",
                    path,
                    $"biome Entries[{entryIndex}].LayerWeightRules[{ruleIndex}]");
                ValidateRangeMapping(
                    rule,
                    "WeightRange",
                    path,
                    $"biome Entries[{entryIndex}].LayerWeightRules[{ruleIndex}] weight range");
            }
        }

        return Deserialize<VegetationBiomeSourceDocument>(path, text, "vegetation biome");
    }

    private static void ValidateAssetReference(
        YamlMappingNode parent,
        string key,
        string path,
        string context)
    {
        YamlMappingNode reference = RequireMapping(parent, key, path, context);
        ValidateMapping(reference, path, context, "Guid", "PackageId");
        RequireScalar(reference, "Guid", path, context);
        RequireScalar(reference, "PackageId", path, context);
    }

    private static void ValidateRangeMapping(
        YamlMappingNode parent,
        string key,
        string path,
        string context)
    {
        YamlMappingNode range = RequireMapping(parent, key, path, context);
        ValidateMapping(range, path, context, "Minimum", "Maximum");
        RequireScalar(range, "Minimum", path, context);
        RequireScalar(range, "Maximum", path, context);
    }

    private static YamlMappingNode ParseRoot(string path, string text, string context)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw new InvalidDataException(
                    $"{context} '{path}' must contain one YAML mapping document.");
            }

            return root;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to parse {context} '{path}': {ex.Message}", ex);
        }
    }

    private static T Deserialize<T>(string path, string text, string context)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<T>(text)
                ?? throw new InvalidDataException($"{context} '{path}' deserialized to an empty document.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"Failed to deserialize {context} '{path}': {ex.Message}", ex);
        }
    }

    private static YamlMappingNode RequireMapping(
        YamlMappingNode parent,
        string key,
        string path,
        string context)
    {
        if (!TryGetNode(parent, key, out YamlNode node) || node is not YamlMappingNode mapping)
        {
            throw new InvalidDataException(
                $"Vegetation source '{path}' requires mapping '{key}' for {context}.");
        }

        return mapping;
    }

    private static YamlSequenceNode RequireSequence(
        YamlMappingNode parent,
        string key,
        string path,
        string context)
    {
        if (!TryGetNode(parent, key, out YamlNode node) || node is not YamlSequenceNode sequence)
        {
            throw new InvalidDataException(
                $"Vegetation source '{path}' requires sequence '{key}' for {context}.");
        }

        return sequence;
    }

    private static YamlMappingNode RequireSequenceMapping(
        YamlSequenceNode sequence,
        int index,
        string path,
        string context)
    {
        if (sequence.Children[index] is not YamlMappingNode mapping)
        {
            throw new InvalidDataException(
                $"Vegetation source '{path}' {context}[{index}] must be a mapping.");
        }

        return mapping;
    }

    private static void RequireScalar(
        YamlMappingNode parent,
        string key,
        string path,
        string context)
    {
        if (!TryGetNode(parent, key, out YamlNode node) || node is not YamlScalarNode)
        {
            throw new InvalidDataException(
                $"Vegetation source '{path}' requires scalar '{key}' for {context}.");
        }
    }

    private static void ValidateMapping(
        YamlMappingNode mapping,
        string path,
        string context,
        params string[] allowed)
    {
        var allowedKeys = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var observedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((YamlNode keyNode, _) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Vegetation source '{path}' {context} contains a non-scalar key.");
            }

            if (!observedKeys.Add(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Vegetation source '{path}' {context} contains duplicate key '{scalar.Value}'.");
            }

            if (!allowedKeys.Contains(scalar.Value))
            {
                throw new InvalidDataException(
                    $"Vegetation source '{path}' {context} contains unknown field '{scalar.Value}'.");
            }
        }
    }

    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode node)
    {
        foreach ((YamlNode keyNode, YamlNode value) in mapping.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase))
            {
                node = value;
                return true;
            }
        }

        node = null!;
        return false;
    }
}

internal sealed class VegetationSpeciesSourceDocument
{
    public int Version { get; set; }
    public Guid SpeciesGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<VegetationSpeciesLodSource> Lods { get; set; } = new();
    public string ShadowPolicy { get; set; } = string.Empty;
    public VegetationRangeSource? ScaleRange { get; set; }
    public VegetationRangeSource? YawRangeDegrees { get; set; }
    public VegetationRangeSource? TiltRangeDegrees { get; set; }
    public VegetationCollisionPromotionSource? CollisionPromotion { get; set; }
    public float WindResponse { get; set; }
}

internal sealed class VegetationSpeciesLodSource
{
    public VegetationAssetReferenceSource? Mesh { get; set; }
    public VegetationAssetReferenceSource? Material { get; set; }
    public float MaximumDistance { get; set; }
    public float MaximumScreenError { get; set; }
}

internal sealed class VegetationCollisionPromotionSource
{
    public string Mode { get; set; } = string.Empty;
    public float CapsuleRadius { get; set; }
    public float CapsuleHalfHeight { get; set; }
    public float MaximumDistance { get; set; }
}

internal sealed class VegetationBiomeSourceDocument
{
    public int Version { get; set; }
    public Guid BiomeGuid { get; set; }
    public string Name { get; set; } = string.Empty;
    public ulong GlobalSeed { get; set; }
    public List<VegetationBiomeEntrySource> Entries { get; set; } = new();
}

internal sealed class VegetationBiomeEntrySource
{
    public string EntryId { get; set; } = string.Empty;
    public VegetationAssetReferenceSource? Species { get; set; }
    public float Density { get; set; }
    public ulong SeedSalt { get; set; }
    public VegetationRangeSource? AltitudeRange { get; set; }
    public VegetationRangeSource? SlopeRangeDegrees { get; set; }
    public List<VegetationLayerWeightRuleSource> LayerWeightRules { get; set; } = new();
    public float MinimumSpacing { get; set; }
    public int ClusterSize { get; set; }
    public string ExclusionPolicy { get; set; } = string.Empty;
}

internal sealed class VegetationLayerWeightRuleSource
{
    public string LayerId { get; set; } = string.Empty;
    public VegetationRangeSource? WeightRange { get; set; }
}

internal sealed class VegetationRangeSource
{
    public float Minimum { get; set; }
    public float Maximum { get; set; }
}

internal sealed class VegetationAssetReferenceSource
{
    public Guid Guid { get; set; }
    public string PackageId { get; set; } = string.Empty;
}

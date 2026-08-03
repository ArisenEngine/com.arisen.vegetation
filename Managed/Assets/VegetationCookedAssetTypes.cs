using ArisenEngine.Core.Assets;

namespace ArisenEngine.Vegetation.Assets;

public readonly record struct VegetationCookedAssetDependency(
    Guid Guid,
    string PackageId,
    string AssetType,
    string Variant,
    bool Required);

public sealed record CookedVegetationSpeciesArtifact(
    Guid SpeciesGuid,
    string Variant,
    string Path,
    int LodCount,
    long SizeInBytes,
    IReadOnlyList<VegetationCookedAssetDependency> Dependencies);

public sealed record CookedVegetationBiomeArtifact(
    Guid BiomeGuid,
    string Variant,
    string Path,
    int EntryCount,
    long SizeInBytes,
    IReadOnlyList<VegetationCookedAssetDependency> Dependencies);

public readonly record struct CookedVegetationMeshReference(
    Guid Guid,
    string PackageId)
{
    public AssetRef<MeshSourceAsset> ToAssetRef() =>
        new(Guid, "Mesh", PackageId);
}

public readonly record struct CookedVegetationMaterialReference(
    Guid Guid,
    string PackageId)
{
    public AssetRef<MaterialSourceAsset> ToAssetRef() =>
        new(Guid, "Material", PackageId);
}

public readonly record struct CookedVegetationSpeciesReference(
    Guid Guid,
    string PackageId)
{
    public AssetRef<VegetationSpeciesSourceAsset> ToAssetRef() =>
        new(Guid, VegetationAssetTypes.Species, PackageId);
}

public sealed record CookedVegetationSpeciesLod(
    CookedVegetationMeshReference Mesh,
    CookedVegetationMaterialReference Material,
    float MaximumDistance,
    float MaximumScreenError);

public sealed record CookedVegetationSpecies(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    IReadOnlyList<CookedVegetationSpeciesLod> Lods,
    VegetationShadowPolicy ShadowPolicy,
    VegetationValueRange ScaleRange,
    VegetationValueRange YawRangeDegrees,
    VegetationValueRange TiltRangeDegrees,
    VegetationCollisionPromotionDescriptor CollisionPromotion,
    float WindResponse);

public sealed record CookedVegetationLayerWeightRule(
    string LayerId,
    VegetationValueRange WeightRange);

public sealed record CookedVegetationBiomeEntry(
    string EntryId,
    CookedVegetationSpeciesReference Species,
    float Density,
    ulong SeedSalt,
    VegetationValueRange AltitudeRange,
    VegetationValueRange SlopeRangeDegrees,
    IReadOnlyList<CookedVegetationLayerWeightRule> LayerWeightRules,
    float MinimumSpacing,
    int ClusterSize,
    VegetationExclusionPolicy ExclusionPolicy);

public sealed record CookedVegetationBiome(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    ulong GlobalSeed,
    IReadOnlyList<CookedVegetationBiomeEntry> Entries);

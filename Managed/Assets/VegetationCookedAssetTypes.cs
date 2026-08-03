using System.Numerics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;

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

public sealed record CookedVegetationInstancePageArtifact(
    Guid PageGuid,
    Guid ClusterGuid,
    string PackageId,
    string Variant,
    string Path,
    int InstanceCount,
    WorldPosition Origin,
    WorldBounds Bounds,
    long SizeInBytes,
    byte[] ContentHash,
    IReadOnlyList<CookedVegetationSpeciesReference> Species,
    IReadOnlyList<VegetationCookedAssetDependency> Dependencies);

public sealed record CookedVegetationClusterArtifact(
    Guid ClusterGuid,
    string PackageId,
    string Variant,
    string Path,
    int PageCount,
    int InstanceCount,
    WorldBounds Bounds,
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

public readonly record struct CookedVegetationBiomeReference(
    Guid Guid,
    string PackageId)
{
    public AssetRef<VegetationBiomeSourceAsset> ToAssetRef() =>
        new(Guid, VegetationAssetTypes.Biome, PackageId);
}

public sealed record VegetationCookedInstanceInput(
    ulong StableKey,
    int SpeciesIndex,
    Vector3 LocalPosition,
    Quaternion Orientation,
    float UniformScale,
    float ConservativeRadius);

public sealed record VegetationInstancePageCookDescriptor(
    Guid Guid,
    Guid ClusterGuid,
    string PackageId,
    int GeneratedSchemaVersion,
    WorldPosition Origin,
    IReadOnlyList<CookedVegetationSpeciesReference> Species,
    IReadOnlyList<VegetationCookedInstanceInput> Instances);

public sealed record VegetationClusterCookDescriptor(
    Guid Guid,
    string PackageId,
    int GeneratedSchemaVersion,
    CookedVegetationBiomeReference Biome,
    IReadOnlyList<VegetationInstancePageCookDescriptor> Pages);

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

public readonly record struct CookedVegetationInstance(
    ulong StableKey,
    uint SpeciesIndex,
    Vector3 LocalPosition,
    Quaternion Orientation,
    float UniformScale,
    float ConservativeRadius);

public sealed record CookedVegetationInstancePage(
    Guid Guid,
    Guid ClusterGuid,
    string PackageId,
    int GeneratedSchemaVersion,
    WorldPosition Origin,
    WorldBounds Bounds,
    IReadOnlyList<CookedVegetationSpeciesReference> Species,
    IReadOnlyList<CookedVegetationInstance> Instances);

public sealed record CookedVegetationInstancePageReference(
    Guid Guid,
    string PackageId,
    int InstanceCount,
    WorldPosition Origin,
    WorldBounds Bounds,
    long SizeInBytes,
    byte[] ContentHash);

public sealed record CookedVegetationCluster(
    Guid Guid,
    string PackageId,
    int GeneratedSchemaVersion,
    CookedVegetationBiomeReference Biome,
    WorldBounds Bounds,
    IReadOnlyList<CookedVegetationSpeciesReference> Species,
    IReadOnlyList<CookedVegetationInstancePageReference> Pages,
    int InstanceCount);

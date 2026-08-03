using ArisenEngine.Core.Assets;

namespace ArisenEngine.Vegetation.Assets;

public static class VegetationAssetTypes
{
    public const string Species = "VegetationSpecies";
    public const string Biome = "VegetationBiome";
    public const string Cluster = "VegetationCluster";
    public const string InstancePage = "VegetationInstancePage";
}

public sealed class VegetationSpeciesSourceAsset
{
    private VegetationSpeciesSourceAsset() { }
}

public sealed class VegetationBiomeSourceAsset
{
    private VegetationBiomeSourceAsset() { }
}

public sealed class VegetationClusterSourceAsset
{
    private VegetationClusterSourceAsset() { }
}

public sealed class VegetationInstancePageSourceAsset
{
    private VegetationInstancePageSourceAsset() { }
}

public enum VegetationShadowPolicy
{
    None = 0,
    Cast = 1
}

public enum VegetationCollisionPromotionMode
{
    None = 0,
    Capsule = 1
}

public enum VegetationExclusionPolicy
{
    Respect = 0,
    IgnoreSoft = 1
}

public readonly record struct VegetationValueRange(float Minimum, float Maximum);

public sealed record VegetationSpeciesLodDescriptor(
    AssetRef<MeshSourceAsset> Mesh,
    AssetRef<MaterialSourceAsset> Material,
    float MaximumDistance,
    float MaximumScreenError);

public sealed record VegetationCollisionPromotionDescriptor(
    VegetationCollisionPromotionMode Mode,
    float CapsuleRadius,
    float CapsuleHalfHeight,
    float MaximumDistance);

public sealed record VegetationSpeciesSourceDescriptor(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    IReadOnlyList<VegetationSpeciesLodDescriptor> Lods,
    VegetationShadowPolicy ShadowPolicy,
    VegetationValueRange ScaleRange,
    VegetationValueRange YawRangeDegrees,
    VegetationValueRange TiltRangeDegrees,
    VegetationCollisionPromotionDescriptor CollisionPromotion,
    float WindResponse);

public sealed record VegetationLayerWeightRuleDescriptor(
    string LayerId,
    VegetationValueRange WeightRange);

public sealed record VegetationBiomeEntryDescriptor(
    string EntryId,
    AssetRef<VegetationSpeciesSourceAsset> Species,
    float Density,
    ulong SeedSalt,
    VegetationValueRange AltitudeRange,
    VegetationValueRange SlopeRangeDegrees,
    IReadOnlyList<VegetationLayerWeightRuleDescriptor> LayerWeightRules,
    float MinimumSpacing,
    int ClusterSize,
    VegetationExclusionPolicy ExclusionPolicy);

public sealed record VegetationBiomeSourceDescriptor(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    string Name,
    ulong GlobalSeed,
    IReadOnlyList<VegetationBiomeEntryDescriptor> Entries);

using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Vegetation.Assets;

public sealed class VegetationRuntimeAssetCooker : IRuntimeAssetCooker
{
    private readonly IAssetDatabase m_AssetDatabase;

    public VegetationRuntimeAssetCooker(IAssetDatabase assetDatabase)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
    }

    public string ProviderId => "com.arisen.vegetation.runtime-asset-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } =
    [
        VegetationAssetTypes.Species,
        VegetationAssetTypes.Biome
    ];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Vegetation.CookAsset");
        ArgumentNullException.ThrowIfNull(context);
        return request.AssetType switch
        {
            VegetationAssetTypes.Species => CookSpecies(context, request),
            VegetationAssetTypes.Biome => CookBiome(context, request),
            _ => throw new InvalidOperationException(
                $"[VegetationRuntimeAssetCooker] Unsupported asset type '{request.AssetType}'.")
        };
    }

    private RuntimeAssetCookerOutput CookSpecies(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Vegetation.CookSpeciesAsset");
        ValidateVariant(request, VegetationSpeciesAssetCooker.RuntimeVariant);
        ValidateOwnership(request, VegetationAssetTypes.Species);
        InvalidateIfForced(context, request.Guid, VegetationSpeciesAssetCooker.RuntimeVariant);
        var speciesRef = new AssetRef<VegetationSpeciesSourceAsset>(
            request.Guid,
            VegetationAssetTypes.Species,
            request.PackageId);
        CookedVegetationSpeciesArtifact cooked = VegetationSpeciesAssetCooker.Cook(
            m_AssetDatabase,
            speciesRef);
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            BuildOutputRelativePath(
                request.PackageId,
                request.Guid,
                cooked.Variant,
                VegetationSpeciesAssetCooker.CookedExtension),
            cooked.Path,
            VegetationSpeciesAssetCooker.CookedFormatVersion,
            ToRuntimeDependencies(cooked.Dependencies));
    }

    private RuntimeAssetCookerOutput CookBiome(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Vegetation.CookBiomeAsset");
        ValidateVariant(request, VegetationBiomeAssetCooker.RuntimeVariant);
        ValidateOwnership(request, VegetationAssetTypes.Biome);
        InvalidateIfForced(context, request.Guid, VegetationBiomeAssetCooker.RuntimeVariant);
        var biomeRef = new AssetRef<VegetationBiomeSourceAsset>(
            request.Guid,
            VegetationAssetTypes.Biome,
            request.PackageId);
        CookedVegetationBiomeArtifact cooked = VegetationBiomeAssetCooker.Cook(
            m_AssetDatabase,
            biomeRef);
        return RuntimeAssetCookerOutput.FromFile(
            request,
            cooked.Variant,
            BuildOutputRelativePath(
                request.PackageId,
                request.Guid,
                cooked.Variant,
                VegetationBiomeAssetCooker.CookedExtension),
            cooked.Path,
            VegetationBiomeAssetCooker.CookedFormatVersion,
            ToRuntimeDependencies(cooked.Dependencies));
    }

    private void ValidateOwnership(RuntimeAssetCookRequest request, string expectedAssetType)
    {
        if (request.Guid == Guid.Empty ||
            !m_AssetDatabase.TryGetAssetDescriptor(request.Guid, out AssetDescriptor descriptor) ||
            descriptor.Guid != request.Guid ||
            !string.Equals(
                descriptor.AssetType,
                expectedAssetType,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(descriptor.PackageId, request.PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationRuntimeAssetCooker] {expectedAssetType} '{request.Guid:D}' is not owned by " +
                $"package '{request.PackageId}'.");
        }
    }

    private static void ValidateVariant(
        RuntimeAssetCookRequest request,
        string expectedVariant)
    {
        if (!string.IsNullOrEmpty(request.Variant) &&
            !string.Equals(request.Variant, expectedVariant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationRuntimeAssetCooker] Asset '{request.Guid:D}' variant '{request.Variant}' is " +
                $"unsupported; expected '{expectedVariant}'.");
        }
    }

    private void InvalidateIfForced(
        RuntimeAssetCookContext context,
        Guid guid,
        string variant)
    {
        if (context.ForceRebuild)
        {
            m_AssetDatabase.InvalidateCookedAssets(guid, variant);
        }
    }

    private static RuntimeAssetCookDependencyRequest[] ToRuntimeDependencies(
        IReadOnlyList<VegetationCookedAssetDependency> dependencies)
    {
        var output = new RuntimeAssetCookDependencyRequest[dependencies.Count];
        for (int index = 0; index < dependencies.Count; index++)
        {
            VegetationCookedAssetDependency dependency = dependencies[index];
            output[index] = new RuntimeAssetCookDependencyRequest(
                dependency.Guid,
                dependency.PackageId,
                dependency.AssetType,
                dependency.Variant,
                dependency.Required);
        }

        return output;
    }

    private static string BuildOutputRelativePath(
        string packageId,
        Guid guid,
        string variant,
        string extension)
    {
        return $"{packageId}/{guid:N}/{variant}{extension}";
    }
}

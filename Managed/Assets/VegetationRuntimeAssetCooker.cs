using System.Security.Cryptography;
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
        VegetationAssetTypes.Biome,
        VegetationAssetTypes.Cluster,
        VegetationAssetTypes.InstancePage
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
            VegetationAssetTypes.Cluster => CookCluster(context, request),
            VegetationAssetTypes.InstancePage => CookInstancePage(context, request),
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

    private RuntimeAssetCookerOutput CookCluster(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Vegetation.CookClusterAsset");
        ValidateVariant(request, VegetationClusterAssetCooker.RuntimeVariant);
        ValidateOwnership(request, VegetationAssetTypes.Cluster);
        var clusterRef = new AssetRef<VegetationClusterSourceAsset>(
            request.Guid,
            VegetationAssetTypes.Cluster,
            request.PackageId);
        lock (VegetationInstancePageAssetCooker.PublicationGate)
        {
            if (!VegetationClusterAssetCooker.TryLoadCookedForRuntimeCooking(
                    m_AssetDatabase,
                    clusterRef,
                    out CookedVegetationCluster cluster,
                    out string diagnostic))
            {
                throw new InvalidOperationException(diagnostic);
            }

            VegetationClusterAssetCooker.ValidateAuthoredBiomeSpecies(
                m_AssetDatabase,
                cluster);
            CookedAssetRecord artifact = GetCurrentArtifact(
                request.Guid,
                VegetationAssetTypes.Cluster,
                VegetationClusterAssetCooker.RuntimeVariant);
            string snapshotPath = PinGeneratedArtifact(
                context,
                artifact,
                VegetationClusterAssetCooker.CookedExtension);
            return RuntimeAssetCookerOutput.FromFile(
                request,
                VegetationClusterAssetCooker.RuntimeVariant,
                BuildOutputRelativePath(
                    request.PackageId,
                    request.Guid,
                    VegetationClusterAssetCooker.RuntimeVariant,
                    VegetationClusterAssetCooker.CookedExtension),
                snapshotPath,
                VegetationClusterAssetCooker.CookedFormatVersion,
                ToRuntimeDependencies(VegetationClusterAssetCooker.BuildDependencies(cluster)));
        }
    }

    private RuntimeAssetCookerOutput CookInstancePage(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        using var zone = Profiler.Zone("Vegetation.CookInstancePageAsset");
        ValidateVariant(request, VegetationInstancePageAssetCooker.RuntimeVariant);
        ValidateOwnership(request, VegetationAssetTypes.InstancePage);
        var pageRef = new AssetRef<VegetationInstancePageSourceAsset>(
            request.Guid,
            VegetationAssetTypes.InstancePage,
            request.PackageId);
        lock (VegetationInstancePageAssetCooker.PublicationGate)
        {
            if (!VegetationInstancePageAssetCooker.TryLoadCookedForRuntimeCooking(
                    m_AssetDatabase,
                    pageRef,
                    out CookedVegetationInstancePage page,
                    out string diagnostic))
            {
                throw new InvalidOperationException(diagnostic);
            }

            CookedAssetRecord artifact = GetCurrentArtifact(
                request.Guid,
                VegetationAssetTypes.InstancePage,
                VegetationInstancePageAssetCooker.RuntimeVariant);
            string snapshotPath = PinGeneratedArtifact(
                context,
                artifact,
                VegetationInstancePageAssetCooker.CookedExtension);
            return RuntimeAssetCookerOutput.FromFile(
                request,
                VegetationInstancePageAssetCooker.RuntimeVariant,
                BuildOutputRelativePath(
                    request.PackageId,
                    request.Guid,
                    VegetationInstancePageAssetCooker.RuntimeVariant,
                    VegetationInstancePageAssetCooker.CookedExtension),
                snapshotPath,
                VegetationInstancePageAssetCooker.CookedFormatVersion,
                ToRuntimeDependencies(VegetationInstancePageAssetCooker.BuildDependencies(page)));
        }
    }

    private CookedAssetRecord GetCurrentArtifact(
        Guid guid,
        string expectedAssetType,
        string variant)
    {
        if (!m_AssetDatabase.TryGetCookedArtifact(guid, variant, out CookedAssetRecord artifact) ||
            artifact.Guid != guid ||
            !string.Equals(
                artifact.AssetType,
                expectedAssetType,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.Variant, variant, StringComparison.Ordinal) ||
            artifact.SizeInBytes <= 0 ||
            string.IsNullOrWhiteSpace(artifact.Path))
        {
            throw new InvalidOperationException(
                $"[VegetationRuntimeAssetCooker] {expectedAssetType} '{guid:D}' has no exact " +
                $"registered '{variant}' artifact.");
        }

        return artifact;
    }

    private static string PinGeneratedArtifact(
        RuntimeAssetCookContext context,
        CookedAssetRecord artifact,
        string extension)
    {
        byte[] bytes = File.ReadAllBytes(artifact.Path);
        if (bytes.LongLength != artifact.SizeInBytes)
        {
            throw new InvalidOperationException(
                $"[VegetationRuntimeAssetCooker] Generated artifact '{artifact.Path}' changed size " +
                "while it was being snapshotted.");
        }

        string contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        string snapshotPath = Path.Combine(
            Path.GetFullPath(context.StagingRoot),
            "VegetationGeneratedSnapshots",
            artifact.Guid.ToString("N"),
            $"{artifact.Variant}.{contentHash}{extension}");
        VegetationCookedContainer.WriteAtomicallyIfChanged(snapshotPath, bytes);
        return snapshotPath;
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

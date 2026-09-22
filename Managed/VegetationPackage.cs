using ArisenEngine.Core.Assets;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Vegetation.Assets;
using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;
using ArisenKernel.Services;

namespace ArisenEngine.Vegetation;

public sealed class VegetationPackage : IPackageEntry
{
    private VegetationRuntimeAssetCooker? m_RuntimeAssetCooker;
    private IRuntimeAssetCookerRegistry? m_RuntimeAssetCookerRegistry;
    private VegetationClusterSceneComponentCodec? m_SceneComponentCodec;
    private ISceneComponentExtensionRegistry? m_SceneComponentRegistry;
    private IRuntimeSceneService? m_RuntimeSceneService;
    private VegetationClusterRenderSource? m_RenderSource;
    private VegetationRuntimeDataStore? m_RuntimeData;
    private VegetationQueryService? m_QueryService;
    private VegetationDiagnosticsService? m_Diagnostics;
    private VegetationAuthoringPreviewService? m_AuthoringPreviews;
    private VegetationWindService? m_Wind;
    private bool m_RuntimeAssetCookerRegistered;
    private bool m_SceneComponentRegistered;

    public bool HasPendingOwnership =>
        m_RuntimeAssetCooker != null ||
        m_RuntimeAssetCookerRegistry != null ||
        m_SceneComponentCodec != null ||
        m_SceneComponentRegistry != null ||
        m_RuntimeSceneService != null ||
        m_RenderSource != null ||
        m_RuntimeData != null ||
        m_QueryService != null ||
        m_Diagnostics != null ||
        m_AuthoringPreviews != null ||
        m_Wind != null ||
        m_RuntimeAssetCookerRegistered ||
        m_SceneComponentRegistered;

    public void OnLoad(IServiceRegistry services)
    {
        if (HasPendingOwnership)
        {
            throw new InvalidOperationException(
                "Vegetation package still owns state from a prior load attempt.");
        }

        IAssetDatabase assetDatabase = services.GetService<IAssetDatabase>();
        IRuntimeAssetCookerRegistry cookerRegistry =
            services.GetService<IRuntimeAssetCookerRegistry>();
        ISceneComponentExtensionRegistry sceneComponentRegistry =
            services.GetService<ISceneComponentExtensionRegistry>();
        IRuntimeSceneService runtimeSceneService =
            services.GetService<IRuntimeSceneService>();

        if (assetDatabase.SourceAccessMode == AssetSourceAccessMode.RuntimeAssetCook)
        {
            IReadOnlyList<VegetationScatterRecipeGenerationResult> generated =
                VegetationScatterRecipeGenerator.GenerateAll(
                    assetDatabase,
                    services.GetService<IAssetSourceIndex>());
            VegetationScatterRecipeReport.Write(assetDatabase, generated);
        }

        m_RuntimeAssetCooker = new VegetationRuntimeAssetCooker(assetDatabase);
        m_RuntimeAssetCookerRegistry = cookerRegistry;
        m_SceneComponentCodec = new VegetationClusterSceneComponentCodec();
        m_SceneComponentRegistry = sceneComponentRegistry;
        m_RuntimeSceneService = runtimeSceneService;
        Func<EntityManager?> entityManagerProvider =
            () => runtimeSceneService.ActiveScene?.EntityManager;
        m_RenderSource = new VegetationClusterRenderSource(entityManagerProvider);
        m_RuntimeData = new VegetationRuntimeDataStore();
        m_QueryService = new VegetationQueryService(
            m_RuntimeData,
            entityManagerProvider);
        m_Diagnostics = new VegetationDiagnosticsService();
        m_AuthoringPreviews = new VegetationAuthoringPreviewService();
        m_Wind = new VegetationWindService();
        m_RuntimeAssetCookerRegistered = false;
        m_SceneComponentRegistered = false;

        try
        {
            m_RuntimeAssetCookerRegistered = true;
            cookerRegistry.RegisterCooker(m_RuntimeAssetCooker);
            m_SceneComponentRegistered = true;
            sceneComponentRegistry.Register(m_SceneComponentCodec);

            services.RegisterService<IVegetationClusterDataSource>(m_RuntimeData);
            services.RegisterService<IVegetationClusterRenderSource>(m_RenderSource);
            services.RegisterService<IVegetationRuntimeDataStore>(m_RuntimeData);
            services.RegisterService<IVegetationQueryService>(m_QueryService);
            services.RegisterService<IVegetationDiagnostics>(m_Diagnostics);
            services.RegisterService<IVegetationDiagnosticsPublisher>(m_Diagnostics);
            services.RegisterService<IVegetationAuthoringPreviewService>(m_AuthoringPreviews);
            services.RegisterService<IVegetationWindSource>(m_Wind);
            services.RegisterService<IVegetationWindClockControl>(m_Wind);
            KernelLog.Info("[Vegetation] Runtime package loaded.");
        }
        catch (Exception loadError)
        {
            var rollbackErrors = new List<Exception>();
            CleanupOwnedState(rollbackErrors);
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, loadError);
                throw new AggregateException(
                    "Vegetation package load failed and ownership rollback reported additional errors.",
                    rollbackErrors);
            }

            throw;
        }
    }

    public void OnUnload(IServiceRegistry services)
    {
        var failures = new List<Exception>();
        try
        {
            KernelLog.Info("[Vegetation] Runtime package unloading.");
        }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException(
                "Vegetation package failed to publish its unload diagnostic.",
                error));
        }

        CleanupOwnedState(failures);
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Vegetation package teardown failed.",
                failures);
        }
    }

    private void CleanupOwnedState(ICollection<Exception> failures)
    {
        if (m_SceneComponentRegistered &&
            m_SceneComponentRegistry != null &&
            m_SceneComponentCodec != null)
        {
            AttemptCleanup(
                "scene-component codec unregister",
                () => m_SceneComponentRegistry.Unregister(m_SceneComponentCodec),
                () => m_SceneComponentRegistered = false,
                failures);
        }

        if (!m_SceneComponentRegistered)
        {
            m_SceneComponentCodec = null;
            m_SceneComponentRegistry = null;
        }

        if (m_Wind != null)
        {
            AttemptCleanup(
                "wind clear",
                m_Wind.Clear,
                () => m_Wind = null,
                failures);
        }

        if (m_AuthoringPreviews != null)
        {
            AttemptCleanup(
                "authoring-preview clear",
                m_AuthoringPreviews.Clear,
                () => m_AuthoringPreviews = null,
                failures);
        }

        if (m_Diagnostics != null)
        {
            AttemptCleanup(
                "diagnostics clear",
                m_Diagnostics.Clear,
                () => m_Diagnostics = null,
                failures);
        }

        if (m_RuntimeData != null)
        {
            AttemptCleanup(
                "runtime-data clear",
                m_RuntimeData.Clear,
                () => m_RuntimeData = null,
                failures);
        }

        if (m_RuntimeData == null)
        {
            m_QueryService = null;
        }

        m_RenderSource = null;
        if (m_QueryService == null)
        {
            m_RuntimeSceneService = null;
        }

        if (m_RuntimeAssetCookerRegistered &&
            m_RuntimeAssetCookerRegistry != null &&
            m_RuntimeAssetCooker != null)
        {
            AttemptCleanup(
                "runtime asset cooker unregister",
                () => m_RuntimeAssetCookerRegistry.UnregisterCooker(m_RuntimeAssetCooker),
                () => m_RuntimeAssetCookerRegistered = false,
                failures);
        }

        if (!m_RuntimeAssetCookerRegistered)
        {
            m_RuntimeAssetCookerRegistry = null;
            m_RuntimeAssetCooker = null;
        }
    }

    private static void AttemptCleanup(
        string stage,
        Action cleanup,
        Action releaseOwnership,
        ICollection<Exception> failures)
    {
        try
        {
            cleanup();
            releaseOwnership();
        }
        catch (Exception error)
        {
            failures.Add(new InvalidOperationException(
                $"Vegetation package failed to complete {stage}.",
                error));
        }
    }
}

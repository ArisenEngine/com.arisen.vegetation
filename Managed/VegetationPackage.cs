using ArisenEngine.Core.Assets;
using ArisenEngine.Vegetation.Assets;
using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;
using ArisenKernel.Services;

namespace ArisenEngine.Vegetation;

public sealed class VegetationPackage : IPackageEntry
{
    private VegetationRuntimeAssetCooker? m_RuntimeAssetCooker;
    private VegetationRuntimeDataStore? m_RuntimeData;
    private VegetationQueryService? m_QueryService;
    private VegetationDiagnosticsService? m_Diagnostics;
    private VegetationAuthoringPreviewService? m_AuthoringPreviews;

    public void OnLoad(IServiceRegistry services)
    {
        IAssetDatabase assetDatabase = services.GetService<IAssetDatabase>();
        m_RuntimeAssetCooker = new VegetationRuntimeAssetCooker(assetDatabase);
        services.GetService<IRuntimeAssetCookerRegistry>().RegisterCooker(m_RuntimeAssetCooker);
        var runtimeData = new VegetationRuntimeDataStore();
        var queryService = new VegetationQueryService(runtimeData);
        var diagnostics = new VegetationDiagnosticsService();
        var authoringPreviews = new VegetationAuthoringPreviewService();

        services.RegisterService<IVegetationClusterDataSource>(runtimeData);
        services.RegisterService<IVegetationQueryService>(queryService);
        services.RegisterService<IVegetationDiagnostics>(diagnostics);
        services.RegisterService<IVegetationDiagnosticsPublisher>(diagnostics);
        services.RegisterService<IVegetationAuthoringPreviewService>(authoringPreviews);

        m_RuntimeData = runtimeData;
        m_QueryService = queryService;
        m_Diagnostics = diagnostics;
        m_AuthoringPreviews = authoringPreviews;
        KernelLog.Info("[Vegetation] Runtime package loaded.");
    }

    public void OnUnload(IServiceRegistry services)
    {
        m_AuthoringPreviews?.Clear();
        m_Diagnostics?.Clear();
        m_RuntimeData?.Clear();
        m_AuthoringPreviews = null;
        m_Diagnostics = null;
        m_QueryService = null;
        m_RuntimeData = null;
        m_RuntimeAssetCooker = null;
        KernelLog.Info("[Vegetation] Runtime package unloaded.");
    }
}

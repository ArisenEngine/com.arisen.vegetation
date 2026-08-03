using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;
using ArisenKernel.Services;

namespace ArisenEngine.Vegetation;

public sealed class VegetationPackage : IPackageEntry
{
    private VegetationRuntimeDataStore? m_RuntimeData;
    private VegetationQueryService? m_QueryService;
    private VegetationDiagnosticsService? m_Diagnostics;
    private VegetationAuthoringPreviewService? m_AuthoringPreviews;

    public void OnLoad(IServiceRegistry services)
    {
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
        KernelLog.Info("[Vegetation] Runtime package unloaded.");
    }
}

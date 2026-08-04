using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.ECS;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Vegetation.Assets;
using YamlDotNet.RepresentationModel;

namespace ArisenEngine.Vegetation;

[Flags]
public enum VegetationClusterFlags : uint
{
    None = 0,
    Visible = 1 << 0,
    CastShadows = 1 << 1,
    ReceiveShadows = 1 << 2
}

[StructLayout(LayoutKind.Sequential)]
public struct VegetationClusterComponent : IComponent
{
    public Guid ClusterGuid;
    public Guid BiomeGuid;
    public Guid SpeciesGuid;
    public Guid WorldGuid;
    public Guid OwningCellGuid;
    public int CellX;
    public int CellY;
    public int CellZ;
    public double OriginX;
    public double OriginY;
    public double OriginZ;
    public double BoundsMinX;
    public double BoundsMinY;
    public double BoundsMinZ;
    public double BoundsMaxX;
    public double BoundsMaxY;
    public double BoundsMaxZ;
    public VegetationClusterFlags Flags;
    public int QualityGroup;
    public int PageCount;
    public int InstanceCount;
}

public sealed class VegetationClusterSceneComponentCodec :
    ISceneComponentExtensionCodec,
    ISceneComponentExtensionActivationValidator
{
    public const uint TypeId = 0x56454743;
    public const int CurrentVersion = 1;

    private const int HeaderSize = 200;
    private const int MaxPackageIdBytes = 128;
    private const int MaxCellLayerBytes = 64;
    private const int MaxCellCoordinateMagnitude = 1_000_000;
    private const int MaxQualityGroup = 7;
    private const int MaxPageCount = 4_096;
    private const int MaxInstanceCount = 1_048_576;
    private const string MeshAssetType = "Mesh";
    private const string MaterialAssetType = "Material";
    private const string MeshRuntimeVariant = "staticmesh.uint32";
    private const string MaterialRuntimeVariant = "material.runtime";
    private const VegetationClusterFlags SupportedFlags =
        VegetationClusterFlags.Visible |
        VegetationClusterFlags.CastShadows |
        VegetationClusterFlags.ReceiveShadows;

    private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);

    public SceneComponentSchemaInfo Schema { get; } = new(
        TypeId,
        "VegetationCluster",
        CurrentVersion,
        Required: true);

    public bool TryReadSource(
        in SceneComponentReadContext context,
        YamlMappingNode source,
        out object component,
        out string diagnostic)
    {
        try
        {
            ValidateFields(
                source,
                [
                    "Cluster",
                    "Biome",
                    "Species",
                    "WorldGuid",
                    "OwningCellGuid",
                    "Cell",
                    "Origin",
                    "Bounds",
                    "Visible",
                    "CastShadows",
                    "ReceiveShadows",
                    "QualityGroup",
                    "PageCount",
                    "InstanceCount"
                ],
                "VegetationCluster");

            YamlMappingNode cluster = ReadMapping(source, "Cluster");
            YamlMappingNode biome = ReadMapping(source, "Biome");
            YamlMappingNode species = ReadMapping(source, "Species");
            YamlMappingNode cell = ReadMapping(source, "Cell");
            YamlMappingNode origin = ReadMapping(source, "Origin");
            YamlMappingNode bounds = ReadMapping(source, "Bounds");
            YamlMappingNode boundsMin = ReadMapping(bounds, "Min");
            YamlMappingNode boundsMax = ReadMapping(bounds, "Max");
            ValidateFields(cluster, ["Guid", "PackageId"], "VegetationCluster.Cluster");
            ValidateFields(biome, ["Guid", "PackageId"], "VegetationCluster.Biome");
            ValidateFields(species, ["Guid", "PackageId"], "VegetationCluster.Species");
            ValidateFields(cell, ["X", "Y", "Z", "Layer"], "VegetationCluster.Cell");
            ValidateFields(origin, ["X", "Y", "Z"], "VegetationCluster.Origin");
            ValidateFields(bounds, ["Min", "Max"], "VegetationCluster.Bounds");
            ValidateFields(boundsMin, ["X", "Y", "Z"], "VegetationCluster.Bounds.Min");
            ValidateFields(boundsMax, ["X", "Y", "Z"], "VegetationCluster.Bounds.Max");

            var value = new VegetationClusterSceneInput(
                RequireCanonicalPackageId(ReadString(cluster, "PackageId")),
                RequireCanonicalPackageId(ReadString(biome, "PackageId")),
                RequireCanonicalPackageId(ReadString(species, "PackageId")),
                RequireCanonicalCellLayer(ReadString(cell, "Layer")),
                new VegetationClusterComponent
                {
                    ClusterGuid = ReadGuid(cluster, "Guid"),
                    BiomeGuid = ReadGuid(biome, "Guid"),
                    SpeciesGuid = ReadGuid(species, "Guid"),
                    WorldGuid = ReadGuid(source, "WorldGuid"),
                    OwningCellGuid = ReadGuid(source, "OwningCellGuid"),
                    CellX = ReadInt32(cell, "X"),
                    CellY = ReadInt32(cell, "Y"),
                    CellZ = ReadInt32(cell, "Z"),
                    OriginX = ReadDouble(origin, "X"),
                    OriginY = ReadDouble(origin, "Y"),
                    OriginZ = ReadDouble(origin, "Z"),
                    BoundsMinX = ReadDouble(boundsMin, "X"),
                    BoundsMinY = ReadDouble(boundsMin, "Y"),
                    BoundsMinZ = ReadDouble(boundsMin, "Z"),
                    BoundsMaxX = ReadDouble(boundsMax, "X"),
                    BoundsMaxY = ReadDouble(boundsMax, "Y"),
                    BoundsMaxZ = ReadDouble(boundsMax, "Z"),
                    Flags = BuildFlags(source),
                    QualityGroup = ReadInt32(source, "QualityGroup"),
                    PageCount = ReadInt32(source, "PageCount"),
                    InstanceCount = ReadInt32(source, "InstanceCount")
                });

            component = ResolveValue(
                context,
                value,
                loadSpeciesFromSource: true);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or InvalidOperationException or OverflowException or ArgumentException)
        {
            component = null!;
            diagnostic = Invalid(context, ex.Message);
            return false;
        }
    }

    public byte[] WriteCooked(object component)
    {
        VegetationClusterSceneValue value = RequireValue(component);
        ValidateBasic(value.Input);
        byte[] clusterPackage = EncodePackageId(value.Input.ClusterPackageId);
        byte[] biomePackage = EncodePackageId(value.Input.BiomePackageId);
        byte[] speciesPackage = EncodePackageId(value.Input.SpeciesPackageId);
        byte[] cellLayer = EncodeCellLayer(value.Input.CellLayer);
        byte[] output = new byte[checked(
            HeaderSize + clusterPackage.Length + biomePackage.Length + speciesPackage.Length +
            cellLayer.Length)];
        Span<byte> bytes = output;
        VegetationClusterComponent data = value.Input.Component;

        WriteGuid(bytes[..16], data.ClusterGuid);
        WriteGuid(bytes[16..32], data.BiomeGuid);
        WriteGuid(bytes[32..48], data.SpeciesGuid);
        WriteGuid(bytes[48..64], data.WorldGuid);
        WriteGuid(bytes[64..80], data.OwningCellGuid);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[80..], data.CellX);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[84..], data.CellY);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[88..], data.CellZ);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[92..], 0);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[96..], data.OriginX);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[104..], data.OriginY);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[112..], data.OriginZ);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[120..], data.BoundsMinX);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[128..], data.BoundsMinY);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[136..], data.BoundsMinZ);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[144..], data.BoundsMaxX);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[152..], data.BoundsMaxY);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[160..], data.BoundsMaxZ);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[168..], (uint)data.Flags);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[172..], data.QualityGroup);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[176..], data.PageCount);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[180..], data.InstanceCount);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[184..], checked((uint)clusterPackage.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[188..], checked((uint)biomePackage.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[192..], checked((uint)speciesPackage.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[196..], checked((uint)cellLayer.Length));

        int offset = HeaderSize;
        clusterPackage.CopyTo(bytes[offset..]);
        offset = checked(offset + clusterPackage.Length);
        biomePackage.CopyTo(bytes[offset..]);
        offset = checked(offset + biomePackage.Length);
        speciesPackage.CopyTo(bytes[offset..]);
        offset = checked(offset + speciesPackage.Length);
        cellLayer.CopyTo(bytes[offset..]);
        return output;
    }

    public bool TryReadCooked(
        in SceneComponentReadContext context,
        ReadOnlySpan<byte> payload,
        out object component,
        out string diagnostic)
    {
        try
        {
            if (payload.Length < HeaderSize)
            {
                throw new InvalidDataException("cooked payload header is truncated");
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(payload[92..]) != 0)
            {
                throw new InvalidDataException("cooked payload reserved fields must be zero");
            }

            int clusterPackageLength = ReadPackageLength(payload[184..], "cluster");
            int biomePackageLength = ReadPackageLength(payload[188..], "biome");
            int speciesPackageLength = ReadPackageLength(payload[192..], "species");
            int cellLayerLength = ReadCellLayerLength(payload[196..]);
            int expectedLength = checked(
                HeaderSize + clusterPackageLength + biomePackageLength + speciesPackageLength +
                cellLayerLength);
            if (payload.Length != expectedLength)
            {
                throw new InvalidDataException("cooked package lengths or total payload size are invalid");
            }

            int offset = HeaderSize;
            string clusterPackage = DecodePackageId(payload.Slice(offset, clusterPackageLength));
            offset = checked(offset + clusterPackageLength);
            string biomePackage = DecodePackageId(payload.Slice(offset, biomePackageLength));
            offset = checked(offset + biomePackageLength);
            string speciesPackage = DecodePackageId(payload.Slice(offset, speciesPackageLength));
            offset = checked(offset + speciesPackageLength);
            string cellLayer = DecodeCellLayer(payload.Slice(offset, cellLayerLength));

            var value = new VegetationClusterSceneInput(
                clusterPackage,
                biomePackage,
                speciesPackage,
                cellLayer,
                new VegetationClusterComponent
                {
                    ClusterGuid = ReadGuid(payload[..16]),
                    BiomeGuid = ReadGuid(payload[16..32]),
                    SpeciesGuid = ReadGuid(payload[32..48]),
                    WorldGuid = ReadGuid(payload[48..64]),
                    OwningCellGuid = ReadGuid(payload[64..80]),
                    CellX = BinaryPrimitives.ReadInt32LittleEndian(payload[80..]),
                    CellY = BinaryPrimitives.ReadInt32LittleEndian(payload[84..]),
                    CellZ = BinaryPrimitives.ReadInt32LittleEndian(payload[88..]),
                    OriginX = BinaryPrimitives.ReadDoubleLittleEndian(payload[96..]),
                    OriginY = BinaryPrimitives.ReadDoubleLittleEndian(payload[104..]),
                    OriginZ = BinaryPrimitives.ReadDoubleLittleEndian(payload[112..]),
                    BoundsMinX = BinaryPrimitives.ReadDoubleLittleEndian(payload[120..]),
                    BoundsMinY = BinaryPrimitives.ReadDoubleLittleEndian(payload[128..]),
                    BoundsMinZ = BinaryPrimitives.ReadDoubleLittleEndian(payload[136..]),
                    BoundsMaxX = BinaryPrimitives.ReadDoubleLittleEndian(payload[144..]),
                    BoundsMaxY = BinaryPrimitives.ReadDoubleLittleEndian(payload[152..]),
                    BoundsMaxZ = BinaryPrimitives.ReadDoubleLittleEndian(payload[160..]),
                    Flags = (VegetationClusterFlags)BinaryPrimitives.ReadUInt32LittleEndian(payload[168..]),
                    QualityGroup = BinaryPrimitives.ReadInt32LittleEndian(payload[172..]),
                    PageCount = BinaryPrimitives.ReadInt32LittleEndian(payload[176..]),
                    InstanceCount = BinaryPrimitives.ReadInt32LittleEndian(payload[180..])
                });

            component = ResolveValue(
                context,
                value,
                loadSpeciesFromSource: false);
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (
            ex is InvalidDataException or InvalidOperationException or OverflowException or ArgumentException)
        {
            component = null!;
            diagnostic = Invalid(context, ex.Message);
            return false;
        }
    }

    public IReadOnlyList<CookedSceneDependency> GetDependencies(object component)
    {
        return RequireValue(component).Dependencies;
    }

    public Guid GetExclusiveOwnershipId(object component)
    {
        return RequireValue(component).Input.Component.ClusterGuid;
    }

    public bool TryValidateActivation(
        in SceneComponentActivationContext context,
        object component,
        out string diagnostic)
    {
        VegetationClusterComponent value;
        try
        {
            value = RequireValue(component).Input.Component;
        }
        catch (ArgumentException ex)
        {
            diagnostic = ex.Message;
            return false;
        }

        if (!context.HasWorldCell)
        {
            diagnostic = "vegetation clusters require a world-cell activation context";
            return false;
        }

        if (context.WorldCellId.Value != value.OwningCellGuid)
        {
            diagnostic =
                $"owning cell '{value.OwningCellGuid:D}' does not match activation cell " +
                $"'{context.WorldCellId.Value:D}'";
            return false;
        }

        var expectedOrigin = new WorldPosition(value.OriginX, value.OriginY, value.OriginZ);
        if (!PositionBitwiseEqual(context.CellOrigin, expectedOrigin))
        {
            diagnostic =
                $"cluster origin {expectedOrigin} does not match activation cell origin " +
                $"{context.CellOrigin}";
            return false;
        }

        var clusterBounds = new WorldBounds(
            new WorldPosition(value.BoundsMinX, value.BoundsMinY, value.BoundsMinZ),
            new WorldPosition(value.BoundsMaxX, value.BoundsMaxY, value.BoundsMaxZ));
        if (!context.CellBounds.IsValid || !Overlaps(clusterBounds, context.CellBounds))
        {
            diagnostic = "cluster bounds do not intersect the activation cell bounds";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    public void AddToEntity(EntityManager entityManager, Entity entity, object component)
    {
        ArgumentNullException.ThrowIfNull(entityManager);
        entityManager.AddComponent(entity, RequireValue(component).Input.Component);
    }

    private static VegetationClusterSceneValue ResolveValue(
        in SceneComponentReadContext context,
        VegetationClusterSceneInput value,
        bool loadSpeciesFromSource)
    {
        ValidateBasic(value);
        IAssetDatabase database = context.AssetDatabase;
        if (!database.TryGetAssetDescriptor(value.Component.ClusterGuid, out AssetDescriptor descriptor) ||
            !string.Equals(
                descriptor.AssetType,
                VegetationAssetTypes.Cluster,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(descriptor.PackageId, value.ClusterPackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "vegetation-cluster asset identity or package ownership is invalid");
        }

        var clusterReference = new AssetRef<VegetationClusterSourceAsset>(
            value.Component.ClusterGuid,
            VegetationAssetTypes.Cluster,
            value.ClusterPackageId);
        bool clusterLoaded = loadSpeciesFromSource
            ? VegetationClusterAssetCooker.TryLoadCookedForRuntimeCooking(
                database,
                clusterReference,
                out CookedVegetationCluster cluster,
                out string clusterDiagnostic)
            : VegetationClusterAssetCooker.TryLoadCooked(
                database,
                clusterReference,
                out cluster,
                out clusterDiagnostic);
        if (!clusterLoaded)
        {
            throw new InvalidDataException(clusterDiagnostic);
        }

        ValidateClusterClosure(value, cluster);
        CookedVegetationSpeciesReference selectedSpecies = cluster.Species[0];
        var uniqueSpecies = new Dictionary<SpeciesKey, CookedVegetationSpeciesReference>();
        bool selectedSpeciesDeclared = false;
        foreach (CookedVegetationSpeciesReference speciesReference in LoadBiomeSpeciesReferences(
                     database,
                     cluster,
                     loadSpeciesFromSource))
        {
            var key = new SpeciesKey(speciesReference.Guid, speciesReference.PackageId);
            uniqueSpecies.TryAdd(key, speciesReference);
            selectedSpeciesDeclared |= key ==
                new SpeciesKey(selectedSpecies.Guid, selectedSpecies.PackageId);
        }

        if (!selectedSpeciesDeclared)
        {
            throw new InvalidDataException(
                loadSpeciesFromSource
                    ? "cluster species is not declared by authored biome closure"
                    : "cluster species is not declared by the exact cooked biome closure");
        }

        CookedVegetationSpecies[] biomeSpecies = uniqueSpecies.Values
            .OrderBy(species => species.Guid)
            .ThenBy(species => species.PackageId, StringComparer.Ordinal)
            .Select(speciesReference => LoadBiomeSpecies(
                database,
                speciesReference,
                loadSpeciesFromSource))
            .ToArray();

        IReadOnlyList<CookedSceneDependency> dependencies =
            BuildDependencies(cluster, biomeSpecies);
        return new VegetationClusterSceneValue(value, dependencies);
    }

    private static IReadOnlyList<CookedVegetationSpeciesReference> LoadBiomeSpeciesReferences(
        IAssetDatabase database,
        CookedVegetationCluster cluster,
        bool loadFromSource)
    {
        if (loadFromSource)
        {
            VegetationBiomeSourceDescriptor biome = VegetationBiomeSourceAssetLoader.LoadSource(
                database,
                cluster.Biome.ToAssetRef());
            return biome.Entries
                .Select(entry => new CookedVegetationSpeciesReference(
                    entry.Species.Guid,
                    entry.Species.PackageId))
                .ToArray();
        }

        if (!VegetationBiomeAssetCooker.TryLoadCooked(
                database,
                cluster.Biome.ToAssetRef(),
                out CookedVegetationBiome cooked,
                out string diagnostic))
        {
            throw new InvalidDataException(diagnostic);
        }

        return cooked.Entries
            .Select(entry => entry.Species)
            .ToArray();
    }

    private static CookedVegetationSpecies LoadBiomeSpecies(
        IAssetDatabase database,
        CookedVegetationSpeciesReference speciesReference,
        bool loadFromSource)
    {
        AssetRef<VegetationSpeciesSourceAsset> species = speciesReference.ToAssetRef();
        if (loadFromSource)
        {
            return VegetationSpeciesAssetCooker.LoadSourceForDependencyDiscovery(
                database,
                species);
        }

        if (!VegetationSpeciesAssetCooker.TryLoadCooked(
                database,
                species,
                out CookedVegetationSpecies cooked,
                out string diagnostic))
        {
            throw new InvalidDataException(diagnostic);
        }

        return cooked;
    }

    private static void ValidateClusterClosure(
        VegetationClusterSceneInput value,
        CookedVegetationCluster cluster)
    {
        VegetationClusterComponent component = value.Component;
        if (cluster.Guid != component.ClusterGuid ||
            !string.Equals(cluster.PackageId, value.ClusterPackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("cluster identity does not match the exact cooked closure");
        }

        if (cluster.Biome.Guid != component.BiomeGuid ||
            !string.Equals(cluster.Biome.PackageId, value.BiomePackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("biome identity does not match the exact cooked cluster closure");
        }

        if (cluster.Species.Count != 1)
        {
            throw new InvalidDataException(
                "vegetation scene clusters must contain exactly one canonical species");
        }

        CookedVegetationSpeciesReference species = cluster.Species[0];
        if (species.Guid != component.SpeciesGuid ||
            !string.Equals(species.PackageId, value.SpeciesPackageId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("species identity does not match the exact cooked cluster closure");
        }

        if (cluster.Pages.Count != component.PageCount ||
            cluster.InstanceCount != component.InstanceCount ||
            !BoundsBitwiseEqual(cluster.Bounds, GetBounds(component)))
        {
            throw new InvalidDataException(
                "page count, instance count, or bounds do not match the exact cooked cluster closure");
        }

        WorldPosition origin = GetOrigin(component);
        for (int index = 0; index < cluster.Pages.Count; index++)
        {
            if (!PositionBitwiseEqual(cluster.Pages[index].Origin, origin))
            {
                throw new InvalidDataException(
                    "all cooked cluster pages must share the component's exact canonical origin");
            }
        }
    }

    private static IReadOnlyList<CookedSceneDependency> BuildDependencies(
        CookedVegetationCluster cluster,
        IReadOnlyList<CookedVegetationSpecies> biomeSpecies)
    {
        var dependencies = new Dictionary<DependencyKey, CookedSceneDependency>();
        AddDependency(
            dependencies,
            new CookedSceneDependency(
                cluster.Guid,
                cluster.PackageId,
                VegetationAssetTypes.Cluster,
                Required: true,
                VegetationClusterAssetCooker.RuntimeVariant));
        AddDependency(
            dependencies,
            new CookedSceneDependency(
                cluster.Biome.Guid,
                cluster.Biome.PackageId,
                VegetationAssetTypes.Biome,
                Required: true,
                VegetationBiomeAssetCooker.RuntimeVariant));

        foreach (CookedVegetationInstancePageReference page in cluster.Pages)
        {
            AddDependency(
                dependencies,
                new CookedSceneDependency(
                    page.Guid,
                    page.PackageId,
                    VegetationAssetTypes.InstancePage,
                    Required: true,
                    VegetationInstancePageAssetCooker.RuntimeVariant));
        }

        foreach (CookedVegetationSpecies species in biomeSpecies)
        {
            AddDependency(
                dependencies,
                new CookedSceneDependency(
                    species.Guid,
                    species.PackageId,
                    VegetationAssetTypes.Species,
                    Required: true,
                    VegetationSpeciesAssetCooker.RuntimeVariant));

            foreach (CookedVegetationSpeciesLod lod in species.Lods)
            {
                AddDependency(
                    dependencies,
                    new CookedSceneDependency(
                        lod.Mesh.Guid,
                        lod.Mesh.PackageId,
                        MeshAssetType,
                        Required: true,
                        MeshRuntimeVariant));
                AddDependency(
                    dependencies,
                    new CookedSceneDependency(
                        lod.Material.Guid,
                        lod.Material.PackageId,
                        MaterialAssetType,
                        Required: true,
                        MaterialRuntimeVariant));
            }
        }

        return Array.AsReadOnly(dependencies.Values
            .OrderBy(dependency => dependency.Guid)
            .ThenBy(dependency => dependency.PackageId, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.AssetType, StringComparer.Ordinal)
            .ThenBy(dependency => dependency.Variant, StringComparer.Ordinal)
            .ToArray());
    }

    private static void AddDependency(
        IDictionary<DependencyKey, CookedSceneDependency> dependencies,
        CookedSceneDependency dependency)
    {
        dependencies.TryAdd(
            new DependencyKey(
                dependency.Guid,
                dependency.PackageId,
                dependency.AssetType,
                dependency.Variant),
            dependency);
    }

    private static void ValidateBasic(VegetationClusterSceneInput value)
    {
        VegetationClusterComponent component = value.Component;
        if (component.ClusterGuid == Guid.Empty ||
            component.BiomeGuid == Guid.Empty ||
            component.SpeciesGuid == Guid.Empty ||
            component.WorldGuid == Guid.Empty ||
            component.OwningCellGuid == Guid.Empty)
        {
            throw new InvalidDataException("vegetation and world identities must be non-empty");
        }

        if (Math.Abs((long)component.CellX) > MaxCellCoordinateMagnitude ||
            Math.Abs((long)component.CellY) > MaxCellCoordinateMagnitude ||
            Math.Abs((long)component.CellZ) > MaxCellCoordinateMagnitude)
        {
            throw new InvalidDataException(
                $"world-cell coordinates must be within +/-{MaxCellCoordinateMagnitude}");
        }

        string cellLayer = RequireCanonicalCellLayer(value.CellLayer);
        var coordinate = new WorldCellCoordinate(
            component.CellX,
            component.CellY,
            component.CellZ);
        if (WorldCellIdentity.Create(component.WorldGuid, coordinate, cellLayer).Value !=
            component.OwningCellGuid)
        {
            throw new InvalidDataException(
                "owning cell identity does not match the world, coordinate, and layer");
        }

        if ((component.Flags & ~SupportedFlags) != 0 ||
            component.QualityGroup is < 0 or > MaxQualityGroup ||
            component.PageCount is < 1 or > MaxPageCount ||
            component.InstanceCount is < 1 or > MaxInstanceCount)
        {
            throw new InvalidDataException(
                "vegetation flags, quality group, page count, or instance count are invalid");
        }

        WorldPosition origin = GetOrigin(component);
        WorldBounds bounds = GetBounds(component);
        if (!IsCanonicalPosition(origin) ||
            !bounds.IsValid ||
            !IsCanonicalPosition(bounds.Min) ||
            !IsCanonicalPosition(bounds.Max))
        {
            throw new InvalidDataException(
                "vegetation origin and bounds must be finite, canonical, and ordered");
        }

        RequireCanonicalPackageId(value.ClusterPackageId);
        RequireCanonicalPackageId(value.BiomePackageId);
        RequireCanonicalPackageId(value.SpeciesPackageId);
    }

    private static VegetationClusterFlags BuildFlags(YamlMappingNode source)
    {
        VegetationClusterFlags flags = VegetationClusterFlags.None;
        if (ReadBoolean(source, "Visible")) flags |= VegetationClusterFlags.Visible;
        if (ReadBoolean(source, "CastShadows")) flags |= VegetationClusterFlags.CastShadows;
        if (ReadBoolean(source, "ReceiveShadows")) flags |= VegetationClusterFlags.ReceiveShadows;
        return flags;
    }

    private static VegetationClusterSceneValue RequireValue(object component)
    {
        return component as VegetationClusterSceneValue
            ?? throw new ArgumentException(
                $"Vegetation scene codec expected '{nameof(VegetationClusterSceneValue)}' staging data.",
                nameof(component));
    }

    private static int ReadPackageLength(ReadOnlySpan<byte> bytes, string role)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        if (length is <= 0 or > MaxPackageIdBytes)
        {
            throw new InvalidDataException(
                $"cooked {role} package id length must be within 1..{MaxPackageIdBytes}");
        }
        return length;
    }

    private static byte[] EncodePackageId(string packageId)
    {
        byte[] bytes = s_StrictUtf8.GetBytes(RequireCanonicalPackageId(packageId));
        if (bytes.Length is <= 0 or > MaxPackageIdBytes)
        {
            throw new InvalidDataException(
                $"vegetation package id UTF-8 length must be within 1..{MaxPackageIdBytes}");
        }
        return bytes;
    }

    private static int ReadCellLayerLength(ReadOnlySpan<byte> bytes)
    {
        int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        if (length is <= 0 or > MaxCellLayerBytes)
        {
            throw new InvalidDataException(
                $"cooked cell layer length must be within 1..{MaxCellLayerBytes}");
        }
        return length;
    }

    private static byte[] EncodeCellLayer(string cellLayer)
    {
        byte[] bytes = s_StrictUtf8.GetBytes(RequireCanonicalCellLayer(cellLayer));
        if (bytes.Length is <= 0 or > MaxCellLayerBytes)
        {
            throw new InvalidDataException(
                $"vegetation cell layer UTF-8 length must be within 1..{MaxCellLayerBytes}");
        }
        return bytes;
    }

    private static string DecodePackageId(ReadOnlySpan<byte> bytes)
    {
        return RequireCanonicalPackageId(s_StrictUtf8.GetString(bytes));
    }

    private static string DecodeCellLayer(ReadOnlySpan<byte> bytes)
    {
        return RequireCanonicalCellLayer(s_StrictUtf8.GetString(bytes));
    }

    private static string RequireCanonicalPackageId(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > MaxPackageIdBytes ||
            !value.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_'))
        {
            throw new InvalidDataException(
                "vegetation package id must be canonical lowercase ASCII");
        }
        return value;
    }

    private static string RequireCanonicalCellLayer(string value)
    {
        string canonical;
        try
        {
            canonical = WorldCellIdentity.NormalizeLayer(value);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        if (!string.Equals(value, canonical, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "vegetation cell layer must be canonical lowercase ASCII");
        }
        return value;
    }

    private static WorldPosition GetOrigin(in VegetationClusterComponent component) =>
        new(component.OriginX, component.OriginY, component.OriginZ);

    private static WorldBounds GetBounds(in VegetationClusterComponent component) =>
        new(
            new WorldPosition(component.BoundsMinX, component.BoundsMinY, component.BoundsMinZ),
            new WorldPosition(component.BoundsMaxX, component.BoundsMaxY, component.BoundsMaxZ));

    private static bool Overlaps(WorldBounds left, WorldBounds right) =>
        left.Min.X < right.Max.X && left.Max.X > right.Min.X &&
        left.Min.Y < right.Max.Y && left.Max.Y > right.Min.Y &&
        left.Min.Z < right.Max.Z && left.Max.Z > right.Min.Z;

    private static bool BoundsBitwiseEqual(WorldBounds left, WorldBounds right) =>
        PositionBitwiseEqual(left.Min, right.Min) &&
        PositionBitwiseEqual(left.Max, right.Max);

    private static bool PositionBitwiseEqual(WorldPosition left, WorldPosition right) =>
        BitConverter.DoubleToInt64Bits(left.X) == BitConverter.DoubleToInt64Bits(right.X) &&
        BitConverter.DoubleToInt64Bits(left.Y) == BitConverter.DoubleToInt64Bits(right.Y) &&
        BitConverter.DoubleToInt64Bits(left.Z) == BitConverter.DoubleToInt64Bits(right.Z);

    private static bool IsCanonicalPosition(WorldPosition value) =>
        IsCanonicalDouble(value.X) &&
        IsCanonicalDouble(value.Y) &&
        IsCanonicalDouble(value.Z);

    private static bool IsCanonicalDouble(double value) =>
        double.IsFinite(value) &&
        BitConverter.DoubleToInt64Bits(value) != unchecked((long)0x8000000000000000UL);

    private static void ValidateFields(
        YamlMappingNode mapping,
        IReadOnlyCollection<string> supported,
        string context)
    {
        foreach ((YamlNode keyNode, _) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode key ||
                string.IsNullOrWhiteSpace(key.Value) ||
                !supported.Contains(key.Value))
            {
                throw new InvalidDataException(
                    $"{context} contains unknown field " +
                    $"'{(keyNode as YamlScalarNode)?.Value ?? "<non-scalar>"}'");
            }
        }
    }

    private static YamlMappingNode ReadMapping(YamlMappingNode mapping, string key)
    {
        return ReadNode(mapping, key) as YamlMappingNode
            ?? throw new InvalidDataException($"{key} must be a mapping");
    }

    private static string ReadString(YamlMappingNode mapping, string key)
    {
        return (ReadNode(mapping, key) as YamlScalarNode)?.Value
            ?? throw new InvalidDataException($"{key} must be a scalar");
    }

    private static Guid ReadGuid(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!Guid.TryParse(text, out Guid value) || value == Guid.Empty)
        {
            throw new InvalidDataException($"{key} must be a non-empty GUID");
        }
        return value;
    }

    private static int ReadInt32(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            throw new InvalidDataException($"{key} must be a 32-bit integer");
        }
        return value;
    }

    private static double ReadDouble(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value))
        {
            throw new InvalidDataException($"{key} must be a finite number");
        }
        return value;
    }

    private static bool ReadBoolean(YamlMappingNode mapping, string key)
    {
        string text = ReadString(mapping, key);
        if (!bool.TryParse(text, out bool value))
        {
            throw new InvalidDataException($"{key} must be true or false");
        }
        return value;
    }

    private static YamlNode ReadNode(YamlMappingNode mapping, string key)
    {
        foreach ((YamlNode keyNode, YamlNode value) in mapping.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return value;
            }
        }

        throw new InvalidDataException($"required field '{key}' is missing");
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out int written) || written != 16)
        {
            throw new InvalidOperationException("Failed to write vegetation component GUID.");
        }
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source)
    {
        return new Guid(source, bigEndian: true);
    }

    private static string Invalid(in SceneComponentReadContext context, string message)
    {
        return $"[VegetationSceneComponent] Scene '{context.DiagnosticPath}' entity " +
               $"'{context.EntityGuid:D}' is invalid: {message}.";
    }

    private sealed record VegetationClusterSceneInput(
        string ClusterPackageId,
        string BiomePackageId,
        string SpeciesPackageId,
        string CellLayer,
        VegetationClusterComponent Component);

    private sealed record VegetationClusterSceneValue(
        VegetationClusterSceneInput Input,
        IReadOnlyList<CookedSceneDependency> Dependencies);

    private readonly record struct DependencyKey(
        Guid Guid,
        string PackageId,
        string AssetType,
        string Variant);

    private readonly record struct SpeciesKey(Guid Guid, string PackageId);
}

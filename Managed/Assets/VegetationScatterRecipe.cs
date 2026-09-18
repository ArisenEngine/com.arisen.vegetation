using System.Globalization;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;
using ArisenEngine.Terrain.Assets;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArisenEngine.Vegetation.Assets;

public sealed record VegetationScatterRecipeDescriptor(
    Guid Guid,
    string PackageId,
    int SourceSchemaVersion,
    AssetRef<WorldSourceAsset> World,
    AssetRef<VegetationBiomeSourceAsset> Biome,
    AssetRef<TerrainRootSourceAsset> TerrainRoot,
    WorldPartitionSettings Partition,
    WorldCellKey Cell,
    string EntryId,
    float UnscaledConservativeRadius,
    IReadOnlyList<VegetationScatterExclusion> Exclusions);

public sealed record VegetationScatterRecipeGenerationResult(
    Guid RecipeGuid,
    Guid ClusterGuid,
    IReadOnlyList<Guid> PageGuids,
    int InstanceCount,
    WorldPosition Origin,
    WorldBounds Bounds,
    CookedVegetationClusterArtifact Artifact);

public static class VegetationScatterRecipeSourceAssetLoader
{
    public const int CurrentSourceSchemaVersion = 1;
    public const int MaximumExclusionCount = 4_096;

    public static VegetationScatterRecipeDescriptor LoadSource(
        IAssetDatabase assetDatabase,
        AssetRef<VegetationScatterRecipeSourceAsset> recipeRef)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (!recipeRef.IsValid ||
            !assetDatabase.TryGetAsset(recipeRef, out AssetRecord? sourceAsset) ||
            !string.Equals(
                sourceAsset.AssetType,
                VegetationAssetTypes.ScatterRecipe,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeSourceAssetLoader] Recipe '{recipeRef.Guid:D}' is not " +
                $"indexed as '{VegetationAssetTypes.ScatterRecipe}'.");
        }

        if (!string.IsNullOrEmpty(recipeRef.PackageId) &&
            !string.Equals(recipeRef.PackageId, sourceAsset.PackageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeSourceAssetLoader] Recipe '{recipeRef.Guid:D}' belongs " +
                $"to package '{sourceAsset.PackageId}', expected '{recipeRef.PackageId}'.");
        }

        if (!File.Exists(sourceAsset.SourcePath))
        {
            throw new FileNotFoundException(
                "Vegetation scatter recipe source is missing.",
                sourceAsset.SourcePath);
        }

        return LoadSourceText(
            assetDatabase,
            sourceAsset.Guid,
            sourceAsset.PackageId,
            sourceAsset.SourcePath,
            File.ReadAllText(sourceAsset.SourcePath));
    }

    public static VegetationScatterRecipeDescriptor LoadSourceText(
        IAssetDatabase assetDatabase,
        Guid expectedGuid,
        string packageId,
        string path,
        string text)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (expectedGuid == Guid.Empty)
        {
            throw new ArgumentException("Scatter recipes require a stable asset GUID.", nameof(expectedGuid));
        }

        VegetationSourceValidation.RequireSourceArguments(path, text);
        string canonicalPackageId = VegetationSourceValidation.RequireCanonicalPackageId(
            packageId,
            VegetationBiomeSourceAssetLoader.MaximumPackageIdLength,
            "vegetation scatter recipe package id");
        ValidateSourceShape(path, text);
        VegetationScatterRecipeSourceDocument source = Deserialize(path, text);
        if (source.Version != CurrentSourceSchemaVersion)
        {
            throw Invalid(
                path,
                $"schema version '{source.Version}' is unsupported; expected " +
                $"'{CurrentSourceSchemaVersion}'");
        }

        if (source.RecipeGuid == Guid.Empty || source.RecipeGuid != expectedGuid)
        {
            throw Invalid(
                path,
                $"declares GUID '{source.RecipeGuid:D}', expected asset GUID '{expectedGuid:D}'");
        }

        AssetRef<WorldSourceAsset> world = CreateAssetRef<WorldSourceAsset>(
            source.World,
            "World",
            path,
            "World");
        AssetRef<VegetationBiomeSourceAsset> biome =
            CreateAssetRef<VegetationBiomeSourceAsset>(
                source.Biome,
                VegetationAssetTypes.Biome,
                path,
                "Biome");
        AssetRef<TerrainRootSourceAsset> terrainRoot = CreateAssetRef<TerrainRootSourceAsset>(
            source.TerrainRoot,
            TerrainAssetTypes.Root,
            path,
            "TerrainRoot");
        if (!string.Equals(biome.PackageId, canonicalPackageId, StringComparison.Ordinal))
        {
            throw Invalid(
                path,
                $"Biome.PackageId '{biome.PackageId}' must match recipe package " +
                $"'{canonicalPackageId}' so generated children retain package ownership");
        }

        ValidateAssetOwnership(assetDatabase, world.Guid, world.AssetType, world.PackageId!, path, "World");
        ValidateAssetOwnership(assetDatabase, biome.Guid, biome.AssetType, biome.PackageId!, path, "Biome");
        ValidateAssetOwnership(
            assetDatabase,
            terrainRoot.Guid,
            terrainRoot.AssetType,
            terrainRoot.PackageId!,
            path,
            "TerrainRoot");

        WorldCellKey cell = ReadWorldContext(assetDatabase, world, source.Cell, path, out WorldPartitionSettings partition);
        string entryId;
        try
        {
            entryId = VegetationScatterIdentity.RequireEntryId(source.EntryId);
        }
        catch (ArgumentException error)
        {
            throw Invalid(path, error.Message);
        }

        if (!float.IsFinite(source.UnscaledConservativeRadius) ||
            source.UnscaledConservativeRadius <= 0.0f ||
            source.UnscaledConservativeRadius > 1_000_000.0f)
        {
            throw Invalid(path, "UnscaledConservativeRadius must be finite and within (0, 1000000]");
        }

        if (source.Exclusions == null || source.Exclusions.Count > MaximumExclusionCount)
        {
            throw Invalid(
                path,
                $"Exclusions must contain at most {MaximumExclusionCount} entries");
        }

        var exclusions = new VegetationScatterExclusion[source.Exclusions.Count];
        for (int index = 0; index < exclusions.Length; index++)
        {
            VegetationScatterExclusionSource exclusion = source.Exclusions[index]
                ?? throw Invalid(path, $"Exclusions[{index}] is null");
            if (!Enum.TryParse(
                    exclusion.Kind,
                    ignoreCase: false,
                    out VegetationScatterExclusionKind kind) ||
                !Enum.IsDefined(kind) ||
                exclusion.Bounds?.Min == null ||
                exclusion.Bounds.Max == null)
            {
                throw Invalid(path, $"Exclusions[{index}] has an invalid kind or bounds");
            }

            var bounds = new WorldBounds(
                ToWorldPosition(exclusion.Bounds.Min),
                ToWorldPosition(exclusion.Bounds.Max));
            if (!bounds.IsValid)
            {
                throw Invalid(path, $"Exclusions[{index}].Bounds must be finite and ordered");
            }

            exclusions[index] = new VegetationScatterExclusion(bounds, kind);
        }

        return new VegetationScatterRecipeDescriptor(
            expectedGuid,
            canonicalPackageId,
            source.Version,
            world,
            biome,
            terrainRoot,
            partition,
            cell,
            entryId,
            source.UnscaledConservativeRadius,
            Array.AsReadOnly(exclusions));
    }

    private static WorldCellKey ReadWorldContext(
        IAssetDatabase assetDatabase,
        AssetRef<WorldSourceAsset> world,
        VegetationScatterCellSource? requestedCell,
        string recipePath,
        out WorldPartitionSettings partition)
    {
        if (requestedCell == null)
        {
            throw Invalid(recipePath, "Cell is required");
        }

        if (!assetDatabase.TryGetAsset(world, out AssetRecord? worldAsset) ||
            !File.Exists(worldAsset.SourcePath))
        {
            throw Invalid(recipePath, $"World '{world.Guid:D}' source is unavailable");
        }

        VegetationScatterWorldContextDocument context;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            context = deserializer.Deserialize<VegetationScatterWorldContextDocument>(
                File.ReadAllText(worldAsset.SourcePath))
                ?? throw new InvalidDataException("world source deserialized to an empty document");
        }
        catch (Exception error) when (error is not InvalidDataException)
        {
            throw Invalid(recipePath, $"World source could not be parsed: {error.Message}");
        }

        if (context.Version is < 1 or > WorldDescriptorLoader.CurrentSourceSchemaVersion ||
            context.WorldGuid != world.Guid ||
            context.Partition?.Origin == null ||
            context.Partition.CellSize == null)
        {
            throw Invalid(recipePath, "World identity, schema, or partition is invalid");
        }

        var origin = ToWorldPosition(context.Partition.Origin);
        var cellSize = ToWorldPosition(context.Partition.CellSize);
        partition = new WorldPartitionSettings(
            origin,
            cellSize,
            context.Partition.LoadRadius,
            context.Partition.UnloadHysteresis,
            context.Partition.MaxActiveCells);
        if (!origin.IsFinite ||
            !cellSize.IsFinite ||
            cellSize.X <= 0.0 ||
            cellSize.Y <= 0.0 ||
            cellSize.Z <= 0.0 ||
            partition.LoadRadius < 0 ||
            partition.UnloadHysteresis < 0 ||
            partition.MaxActiveCells <= 0)
        {
            throw Invalid(recipePath, "World partition contains invalid values");
        }

        var coordinate = new WorldCellCoordinate(
            requestedCell.X,
            requestedCell.Y,
            requestedCell.Z);
        string layer;
        try
        {
            layer = WorldCellIdentity.NormalizeLayer(requestedCell.Layer);
            _ = WorldCellIdentity.Create(world.Guid, coordinate, layer);
        }
        catch (ArgumentException error)
        {
            throw Invalid(recipePath, error.Message);
        }

        int matchingCells = context.Cells.Count(candidate =>
            candidate.Coordinate != null &&
            candidate.Coordinate.X == coordinate.X &&
            candidate.Coordinate.Y == coordinate.Y &&
            candidate.Coordinate.Z == coordinate.Z &&
            string.Equals(candidate.Layer, layer, StringComparison.OrdinalIgnoreCase));
        if (matchingCells != 1)
        {
            throw Invalid(
                recipePath,
                $"World must contain exactly one requested cell ({coordinate.X}, {coordinate.Y}, " +
                $"{coordinate.Z}, {layer}); found {matchingCells}");
        }

        return new WorldCellKey(coordinate, layer);
    }

    private static AssetRef<TAsset> CreateAssetRef<TAsset>(
        VegetationScatterAssetReferenceSource? source,
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
            VegetationBiomeSourceAssetLoader.MaximumPackageIdLength,
            $"{context}.PackageId");
        return new AssetRef<TAsset>(source.Guid, assetType, packageId);
    }

    private static void ValidateAssetOwnership(
        IAssetDatabase assetDatabase,
        Guid guid,
        string assetType,
        string packageId,
        string path,
        string context)
    {
        if (!assetDatabase.TryGetAssetDescriptor(guid, out AssetDescriptor descriptor) ||
            !string.Equals(descriptor.AssetType, assetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(descriptor.PackageId, packageId, StringComparison.Ordinal))
        {
            throw Invalid(
                path,
                $"{context} '{guid:D}' is not indexed as package '{packageId}' type '{assetType}'");
        }
    }

    private static WorldPosition ToWorldPosition(VegetationScatterPositionSource source) =>
        new(source.X, source.Y, source.Z);

    private static VegetationScatterRecipeSourceDocument Deserialize(string path, string text)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(PascalCaseNamingConvention.Instance)
                .Build();
            return deserializer.Deserialize<VegetationScatterRecipeSourceDocument>(text)
                ?? throw Invalid(path, "deserialized to an empty document");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw Invalid(path, $"could not be deserialized: {error.Message}");
        }
    }

    private static void ValidateSourceShape(string path, string text)
    {
        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(text);
            stream.Load(reader);
            if (stream.Documents.Count != 1 ||
                stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                throw Invalid(path, "must contain one YAML mapping document");
            }

            ValidateMapping(
                root,
                path,
                "recipe",
                "Version",
                "RecipeGuid",
                "World",
                "Biome",
                "TerrainRoot",
                "Cell",
                "EntryId",
                "UnscaledConservativeRadius",
                "Exclusions");
            ValidateReference(root, "World", path);
            ValidateReference(root, "Biome", path);
            ValidateReference(root, "TerrainRoot", path);
            ValidateMapping(
                RequireMapping(root, "Cell", path),
                path,
                "Cell",
                "X",
                "Y",
                "Z",
                "Layer");
            YamlSequenceNode exclusions = RequireSequence(root, "Exclusions", path);
            for (int index = 0; index < exclusions.Children.Count; index++)
            {
                if (exclusions.Children[index] is not YamlMappingNode exclusion)
                {
                    throw Invalid(path, $"Exclusions[{index}] must be a mapping");
                }

                ValidateMapping(exclusion, path, $"Exclusions[{index}]", "Kind", "Bounds");
                YamlMappingNode bounds = RequireMapping(exclusion, "Bounds", path);
                ValidateMapping(bounds, path, $"Exclusions[{index}].Bounds", "Min", "Max");
                ValidatePosition(RequireMapping(bounds, "Min", path), path, $"Exclusions[{index}].Bounds.Min");
                ValidatePosition(RequireMapping(bounds, "Max", path), path, $"Exclusions[{index}].Bounds.Max");
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw Invalid(path, $"could not be parsed: {error.Message}");
        }
    }

    private static void ValidateReference(YamlMappingNode root, string key, string path)
    {
        ValidateMapping(RequireMapping(root, key, path), path, key, "Guid", "PackageId");
    }

    private static void ValidatePosition(YamlMappingNode node, string path, string context)
    {
        ValidateMapping(node, path, context, "X", "Y", "Z");
    }

    private static void ValidateMapping(
        YamlMappingNode mapping,
        string path,
        string context,
        params string[] allowedFields)
    {
        var allowed = new HashSet<string>(allowedFields, StringComparer.OrdinalIgnoreCase);
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((YamlNode keyNode, _) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
            {
                throw Invalid(path, $"{context} contains a non-scalar key");
            }

            if (!observed.Add(scalar.Value))
            {
                throw Invalid(path, $"{context} duplicates field '{scalar.Value}'");
            }

            if (!allowed.Contains(scalar.Value))
            {
                throw Invalid(path, $"{context} contains unknown field '{scalar.Value}'");
            }
        }

        foreach (string required in allowedFields)
        {
            if (!observed.Contains(required))
            {
                throw Invalid(path, $"{context} requires field '{required}'");
            }
        }
    }

    private static YamlMappingNode RequireMapping(
        YamlMappingNode parent,
        string key,
        string path)
    {
        foreach ((YamlNode keyNode, YamlNode value) in parent.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase) &&
                value is YamlMappingNode mapping)
            {
                return mapping;
            }
        }

        throw Invalid(path, $"requires mapping '{key}'");
    }

    private static YamlSequenceNode RequireSequence(
        YamlMappingNode parent,
        string key,
        string path)
    {
        foreach ((YamlNode keyNode, YamlNode value) in parent.Children)
        {
            if (keyNode is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.OrdinalIgnoreCase) &&
                value is YamlSequenceNode sequence)
            {
                return sequence;
            }
        }

        throw Invalid(path, $"requires sequence '{key}'");
    }

    private static InvalidDataException Invalid(string path, string diagnostic) =>
        new($"[VegetationScatterRecipeSourceAssetLoader] Recipe '{path}' {diagnostic}.");
}

public static class VegetationScatterRecipeGenerator
{
    private const string GeneratedDirectoryName = "Generated";
    private const string GeneratedSourceExtension = ".arivegetationgenerated";

    public static IReadOnlyList<VegetationScatterRecipeGenerationResult> GenerateAll(
        IAssetDatabase assetDatabase,
        IAssetSourceIndex sourceIndex)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(sourceIndex);
        if (assetDatabase.SourceAccessMode != AssetSourceAccessMode.RuntimeAssetCook)
        {
            return Array.Empty<VegetationScatterRecipeGenerationResult>();
        }

        AssetRecord[] sources = assetDatabase.Assets
            .Where(asset => string.Equals(
                asset.AssetType,
                VegetationAssetTypes.ScatterRecipe,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(asset => asset.PackageId, StringComparer.Ordinal)
            .ThenBy(asset => asset.Guid)
            .ToArray();
        var prepared = new List<PreparedRecipe>(sources.Length);
        var generatedGuids = new HashSet<Guid>();
        var generatedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < sources.Length; index++)
        {
            AssetRecord source = sources[index];
            VegetationScatterRecipeDescriptor recipe =
                VegetationScatterRecipeSourceAssetLoader.LoadSource(
                    assetDatabase,
                    new AssetRef<VegetationScatterRecipeSourceAsset>(
                        source.Guid,
                        VegetationAssetTypes.ScatterRecipe,
                        source.PackageId));
            TerrainRootAssetCooker.Cook(assetDatabase, recipe.TerrainRoot);
            VegetationSpeciesAndBiomeClosure closure = CookSpeciesAndBiome(assetDatabase, recipe);
            VegetationScatterBakeResult bake = VegetationScatterBaker.Build(
                assetDatabase,
                new VegetationScatterCookRequest(
                    recipe.World.Guid,
                    recipe.Biome,
                    recipe.TerrainRoot,
                    recipe.Partition,
                    recipe.Cell,
                    recipe.EntryId,
                    recipe.UnscaledConservativeRadius,
                    recipe.Exclusions));
            string outputDirectory = GetGeneratedDirectory(source);
            if (!generatedDirectories.Add(outputDirectory))
            {
                throw new InvalidOperationException(
                    $"[VegetationScatterRecipeGenerator] Generated directory collision at " +
                    $"'{outputDirectory}'.");
            }

            if (bake.HasOutput)
            {
                VegetationClusterCookDescriptor cluster = bake.Cluster;
                if (cluster.Biome.Guid != closure.BiomeGuid ||
                    cluster.Pages.Any(page =>
                        page.Species.Count != 1 ||
                        page.Species[0].Guid != closure.SpeciesGuid))
                {
                    throw new InvalidOperationException(
                        $"[VegetationScatterRecipeGenerator] Recipe '{recipe.Guid:D}' produced a " +
                        "cluster outside its cooked biome/species closure.");
                }

                if (!generatedGuids.Add(bake.ClusterMetadata.Guid) ||
                    bake.PageMetadata.Any(metadata => !generatedGuids.Add(metadata.Guid)))
                {
                    throw new InvalidOperationException(
                        $"[VegetationScatterRecipeGenerator] Recipe '{recipe.Guid:D}' generated a " +
                        "duplicate cluster/page identity.");
                }
            }

            prepared.Add(new PreparedRecipe(source, recipe, bake, outputDirectory));
        }

        var results = new List<VegetationScatterRecipeGenerationResult>(prepared.Count);
        foreach (PreparedRecipe item in prepared)
        {
            VegetationScatterRecipeGenerationResult? result =
                PublishPreparedRecipe(assetDatabase, sourceIndex, item);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results.AsReadOnly();
    }

    private static VegetationSpeciesAndBiomeClosure CookSpeciesAndBiome(
        IAssetDatabase assetDatabase,
        VegetationScatterRecipeDescriptor recipe)
    {
        VegetationBiomeSourceDescriptor biome = VegetationBiomeSourceAssetLoader.LoadSource(
            assetDatabase,
            recipe.Biome);
        VegetationBiomeEntryDescriptor entry = biome.Entries.SingleOrDefault(candidate =>
            string.Equals(candidate.EntryId, recipe.EntryId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"[VegetationScatterRecipeGenerator] Recipe '{recipe.Guid:D}' biome " +
                $"'{biome.Guid:D}' has no entry '{recipe.EntryId}'.");
        VegetationSpeciesAssetCooker.Cook(assetDatabase, entry.Species);
        VegetationBiomeAssetCooker.Cook(assetDatabase, recipe.Biome);
        return new VegetationSpeciesAndBiomeClosure(biome.Guid, entry.Species.Guid);
    }

    private static string GetGeneratedDirectory(AssetRecord source)
    {
        string sourcePath = Path.GetFullPath(source.SourcePath);
        string? sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (sourceDirectory == null ||
            !sourcePath.StartsWith(
                sourceDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeGenerator] Recipe '{source.Guid:D}' has an invalid source path.");
        }

        string output = Path.GetFullPath(Path.Combine(
            sourceDirectory,
            GeneratedDirectoryName,
            source.Guid.ToString("N")));
        string expectedPrefix = Path.GetFullPath(Path.Combine(sourceDirectory, GeneratedDirectoryName)) +
            Path.DirectorySeparatorChar;
        if (!output.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeGenerator] Recipe '{source.Guid:D}' generated path escapes " +
                "its owned directory.");
        }

        return output;
    }

    private static VegetationScatterRecipeGenerationResult? PublishPreparedRecipe(
        IAssetDatabase assetDatabase,
        IAssetSourceIndex sourceIndex,
        PreparedRecipe prepared)
    {
        PreviousGeneratedClosure previous = CapturePreviousGeneratedClosure(
            prepared.OutputDirectory);
        using var transaction = GeneratedSourceTransaction.Stage(prepared);
        try
        {
            transaction.Commit();
            sourceIndex.RefreshSourceDirectory(
                prepared.OutputDirectory,
                prepared.Source.PackageId);
            if (prepared.Bake.HasOutput)
            {
                ValidateGeneratedSources(assetDatabase, prepared);
            }

            IReadOnlyList<CookedAssetRecord> published;
            if (!prepared.Bake.HasOutput)
            {
                assetDatabase.PublishCookedArtifactSet(
                    Array.Empty<CookedArtifactSetEntry>(),
                    previous.CookedIdentities);
                transaction.Accept();
                return null;
            }

            VegetationClusterCookPreparation preparation =
                VegetationClusterAssetCooker.Prepare(
                    assetDatabase,
                    prepared.Bake.Cluster);
            var entries = new List<CookedArtifactSetEntry>(
                preparation.Pages.Count + 1);
            for (int index = 0; index < preparation.Pages.Count; index++)
            {
                CookedVegetationInstancePage page = preparation.Pages[index];
                byte[] payload = preparation.PagePayloads[index];
                VegetationInstancePageAssetCooker.ValidatePublicationCompatibility(
                    assetDatabase,
                    page,
                    payload);
                entries.Add(new CookedArtifactSetEntry(
                    page.Guid,
                    VegetationAssetTypes.InstancePage,
                    VegetationInstancePageAssetCooker.RuntimeVariant,
                    VegetationInstancePageAssetCooker.CookedExtension,
                    payload));
            }

            entries.Add(new CookedArtifactSetEntry(
                preparation.Cluster.Guid,
                VegetationAssetTypes.Cluster,
                VegetationClusterAssetCooker.RuntimeVariant,
                VegetationClusterAssetCooker.CookedExtension,
                preparation.ClusterPayload));
            Guid[] currentIds = entries.Select(entry => entry.Guid).ToArray();
            HashSet<Guid> currentIdSet = currentIds.ToHashSet();
            CookedAssetIdentity[] stale = previous.CookedIdentities
                .Where(identity => !currentIdSet.Contains(identity.Guid))
                .ToArray();
            published = assetDatabase.PublishCookedArtifactSet(entries, stale);
            transaction.Accept();

            CookedAssetRecord clusterArtifact = published.Single(artifact =>
                artifact.Guid == preparation.Cluster.Guid &&
                string.Equals(
                    artifact.Variant,
                    VegetationClusterAssetCooker.RuntimeVariant,
                    StringComparison.Ordinal));
            return new VegetationScatterRecipeGenerationResult(
                prepared.Recipe.Guid,
                preparation.Cluster.Guid,
                Array.AsReadOnly(preparation.Cluster.Pages.Select(page => page.Guid).ToArray()),
                preparation.Cluster.InstanceCount,
                preparation.Pages[0].Origin,
                preparation.Cluster.Bounds,
                new CookedVegetationClusterArtifact(
                    preparation.Cluster.Guid,
                    preparation.Cluster.PackageId,
                    VegetationClusterAssetCooker.RuntimeVariant,
                    clusterArtifact.Path,
                    preparation.Cluster.Pages.Count,
                    preparation.Cluster.InstanceCount,
                    preparation.Cluster.Bounds,
                    clusterArtifact.SizeInBytes,
                    Array.AsReadOnly(
                        VegetationClusterAssetCooker.BuildDependencies(preparation.Cluster))));
        }
        catch (Exception publicationError)
        {
            var rollbackErrors = new List<Exception>();
            try
            {
                transaction.Rollback();
            }
            catch (Exception rollbackError)
            {
                rollbackErrors.Add(rollbackError);
            }

            try
            {
                sourceIndex.RefreshSourceDirectory(
                    prepared.OutputDirectory,
                    prepared.Source.PackageId);
            }
            catch (Exception refreshError)
            {
                rollbackErrors.Add(refreshError);
            }

            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, publicationError);
                throw new AggregateException(
                    "[VegetationScatterRecipeGenerator] Generated source/cooked publication failed " +
                    "and rollback was incomplete.",
                    rollbackErrors);
            }

            throw new InvalidOperationException(
                "[VegetationScatterRecipeGenerator] Generated source/cooked publication failed.",
                publicationError);
        }
    }

    private static PreviousGeneratedClosure CapturePreviousGeneratedClosure(
        string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return PreviousGeneratedClosure.Empty;
        }

        var identities = new List<CookedAssetIdentity>();
        foreach (string path in Directory.EnumerateFiles(
                     outputDirectory,
                     "*" + GeneratedSourceExtension,
                     SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            string metadataPath = path + ".meta";
            if (!File.Exists(metadataPath))
            {
                throw new InvalidDataException(
                    $"[VegetationScatterRecipeGenerator] Generated source '{path}' has no metadata.");
            }

            AssetMetadata metadata =
                ArisenEngine.Core.Serialization.SerializationUtil.Deserialize<AssetMetadata>(
                    metadataPath,
                    serializeIfNotExist: false);
            if (metadata.Generated == null ||
                (metadata.Generated.ChildKind != VegetationScatterIdentity.ClusterChildKind &&
                 metadata.Generated.ChildKind != VegetationScatterIdentity.PageChildKind))
            {
                continue;
            }

            string? variant = metadata.AssetType switch
            {
                VegetationAssetTypes.Cluster => VegetationClusterAssetCooker.RuntimeVariant,
                VegetationAssetTypes.InstancePage => VegetationInstancePageAssetCooker.RuntimeVariant,
                _ => null
            };
            if (variant == null)
            {
                continue;
            }

            identities.Add(new CookedAssetIdentity(metadata.Guid, variant));
        }

        return new PreviousGeneratedClosure(
            identities
                .Distinct()
                .OrderBy(identity => identity.Guid)
                .ThenBy(identity => identity.Variant, StringComparer.Ordinal)
                .ToArray());
    }

    private static void WriteGeneratedSources(PreparedRecipe prepared, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        WriteGeneratedSource(
            Path.Combine(outputDirectory, "cluster" + GeneratedSourceExtension),
            prepared.Recipe.Guid,
            prepared.Bake.ClusterMetadata,
            prepared.Bake.PlacementContentHash,
            pageIndex: null);
        for (int index = 0; index < prepared.Bake.PageMetadata.Count; index++)
        {
            WriteGeneratedSource(
                Path.Combine(
                    outputDirectory,
                    $"page-{index:D4}{GeneratedSourceExtension}"),
                prepared.Recipe.Guid,
                prepared.Bake.PageMetadata[index],
                prepared.Bake.PageContentHashes[index],
                index);
        }
    }

    private static void WriteGeneratedSource(
        string path,
        Guid recipeGuid,
        AssetMetadata metadata,
        ReadOnlySpan<byte> placementContentHash,
        int? pageIndex)
    {
        GeneratedAssetMetadata generated = metadata.Generated
            ?? throw new InvalidOperationException(
                $"Generated vegetation identity '{metadata.Guid:D}' has no provenance.");
        string role = pageIndex.HasValue
            ? string.Format(CultureInfo.InvariantCulture, "page-{0:D4}", pageIndex.Value)
            : "cluster";
        string source =
            $"Version: 1\nRecipeGuid: {recipeGuid:D}\nRole: {role}\n" +
            $"Guid: {metadata.Guid:D}\nAssetType: {metadata.AssetType}\n" +
            $"PlacementContentHash: {Convert.ToHexString(placementContentHash).ToLowerInvariant()}\n";
        string meta =
            $"Guid: {metadata.Guid:D}\nAssetType: {metadata.AssetType}\nImporter: {metadata.Importer}\n" +
            "Generated:\n" +
            $"  SourceGuid: {generated.SourceGuid:D}\n" +
            $"  SourcePackageId: {generated.SourcePackageId}\n" +
            $"  ChildKind: {generated.ChildKind}\n" +
            $"  ChildKey: {generated.ChildKey}\n" +
            $"  GeneratedByImporter: {generated.GeneratedByImporter}\n";
        VegetationCookedContainer.WriteAtomicallyIfChanged(path, Encoding.UTF8.GetBytes(source));
        VegetationCookedContainer.WriteAtomicallyIfChanged(
            path + ".meta",
            Encoding.UTF8.GetBytes(meta));
    }

    private static void ValidateGeneratedSources(
        IAssetDatabase assetDatabase,
        PreparedRecipe prepared)
    {
        ValidateGeneratedSource(
            assetDatabase,
            prepared.Bake.ClusterMetadata,
            prepared.Source.PackageId);
        foreach (AssetMetadata metadata in prepared.Bake.PageMetadata)
        {
            ValidateGeneratedSource(assetDatabase, metadata, prepared.Source.PackageId);
        }
    }

    private static void ValidateGeneratedSource(
        IAssetDatabase assetDatabase,
        AssetMetadata expected,
        string packageId)
    {
        if (!assetDatabase.TryGetAsset(expected.Guid, out AssetRecord? source) ||
            !string.Equals(source.AssetType, expected.AssetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(source.PackageId, packageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeGenerator] Generated identity '{expected.Guid:D}' was not " +
                "refreshed into the source catalog with exact ownership.");
        }

        AssetMetadata actual = ArisenEngine.Core.Serialization.SerializationUtil.Deserialize<AssetMetadata>(
            source.MetaPath,
            serializeIfNotExist: false);
        GeneratedAssetMetadata expectedGenerated = expected.Generated!;
        GeneratedAssetMetadata? actualGenerated = actual.Generated;
        if (actual.Guid != expected.Guid ||
            !string.Equals(actual.AssetType, expected.AssetType, StringComparison.Ordinal) ||
            !string.Equals(actual.Importer, expected.Importer, StringComparison.Ordinal) ||
            actualGenerated == null ||
            actualGenerated.SourceGuid != expectedGenerated.SourceGuid ||
            !string.Equals(
                actualGenerated.SourcePackageId,
                expectedGenerated.SourcePackageId,
                StringComparison.Ordinal) ||
            !string.Equals(actualGenerated.ChildKind, expectedGenerated.ChildKind, StringComparison.Ordinal) ||
            !string.Equals(actualGenerated.ChildKey, expectedGenerated.ChildKey, StringComparison.Ordinal) ||
            !string.Equals(
                actualGenerated.GeneratedByImporter,
                expectedGenerated.GeneratedByImporter,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeGenerator] Generated identity '{expected.Guid:D}' metadata " +
                "does not match the deterministic scatter result.");
        }
    }

    private sealed record PreparedRecipe(
        AssetRecord Source,
        VegetationScatterRecipeDescriptor Recipe,
        VegetationScatterBakeResult Bake,
        string OutputDirectory);

    private sealed record PreviousGeneratedClosure(
        IReadOnlyList<CookedAssetIdentity> CookedIdentities)
    {
        public static PreviousGeneratedClosure Empty { get; } = new(Array.Empty<CookedAssetIdentity>());
    }

    private sealed class GeneratedSourceTransaction : IDisposable
    {
        private readonly string m_OutputDirectory;
        private readonly string m_TransactionRoot;
        private readonly string m_StagedDirectory;
        private readonly string m_BackupDirectory;
        private readonly List<(string Original, string Backup)> m_Backups = new();
        private readonly List<string> m_Installed = new();
        private bool m_Committed;
        private bool m_Accepted;
        private bool m_RolledBack;

        private GeneratedSourceTransaction(
            string outputDirectory,
            string transactionRoot,
            string stagedDirectory,
            string backupDirectory)
        {
            m_OutputDirectory = outputDirectory;
            m_TransactionRoot = transactionRoot;
            m_StagedDirectory = stagedDirectory;
            m_BackupDirectory = backupDirectory;
        }

        public static GeneratedSourceTransaction Stage(PreparedRecipe prepared)
        {
            string outputDirectory = Path.GetFullPath(prepared.OutputDirectory);
            string parent = Path.GetDirectoryName(outputDirectory) ??
                throw new InvalidOperationException(
                    "[VegetationScatterRecipeGenerator] Generated output has no parent directory.");
            string transactionRoot = Path.Combine(
                parent,
                ".scatter-" + prepared.Recipe.Guid.ToString("N") + "-" +
                Guid.NewGuid().ToString("N"));
            string stagedDirectory = Path.Combine(transactionRoot, "staged");
            string backupDirectory = Path.Combine(transactionRoot, "backup");
            Directory.CreateDirectory(stagedDirectory);
            if (prepared.Bake.HasOutput)
            {
                VegetationScatterRecipeGenerator.WriteGeneratedSources(
                    prepared,
                    stagedDirectory);
            }
            return new GeneratedSourceTransaction(
                outputDirectory,
                transactionRoot,
                stagedDirectory,
                backupDirectory);
        }

        public void Commit()
        {
            if (m_Committed)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(m_OutputDirectory);
                var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(m_OutputDirectory))
                {
                    foreach (string path in Directory.EnumerateFiles(
                                 m_OutputDirectory,
                                 "*" + GeneratedSourceExtension,
                                 SearchOption.TopDirectoryOnly))
                    {
                        affected.Add(Path.GetFullPath(path));
                        affected.Add(Path.GetFullPath(path + ".meta"));
                    }
                }

                foreach (string staged in Directory.EnumerateFiles(
                             m_StagedDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    affected.Add(Path.Combine(
                        m_OutputDirectory,
                        Path.GetFileName(staged)));
                }

                foreach (string target in affected.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(target))
                    {
                        continue;
                    }

                    string relative = Path.GetFileName(target);
                    string backup = Path.Combine(m_BackupDirectory, relative);
                    Directory.CreateDirectory(m_BackupDirectory);
                    File.Move(target, backup);
                    m_Backups.Add((target, backup));
                }

                foreach (string staged in Directory.EnumerateFiles(
                             m_StagedDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly)
                             .Order(StringComparer.OrdinalIgnoreCase))
                {
                    string target = Path.Combine(
                        m_OutputDirectory,
                        Path.GetFileName(staged));
                    File.Move(staged, target);
                    m_Installed.Add(target);
                }

                m_Committed = true;
            }
            catch
            {
                Rollback();
                throw;
            }
        }

        public void Accept()
        {
            m_Accepted = true;
            m_Backups.Clear();
            m_Installed.Clear();
            TryDeleteDirectory(m_TransactionRoot);
        }

        public void Rollback()
        {
            if (m_RolledBack)
            {
                return;
            }

            m_RolledBack = true;
            if (!m_Committed && m_Backups.Count == 0 && m_Installed.Count == 0)
            {
                TryDeleteDirectory(m_TransactionRoot);
                return;
            }

            var errors = new List<Exception>();
            for (int index = m_Installed.Count - 1; index >= 0; index--)
            {
                try
                {
                    if (File.Exists(m_Installed[index]))
                    {
                        File.Delete(m_Installed[index]);
                    }
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            for (int index = m_Backups.Count - 1; index >= 0; index--)
            {
                (string original, string backup) = m_Backups[index];
                if (!File.Exists(backup))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                    File.Move(backup, original, overwrite: true);
                }
                catch (Exception error)
                {
                    errors.Add(error);
                }
            }

            m_Installed.Clear();
            m_Backups.Clear();
            TryDeleteDirectory(m_TransactionRoot);
            if (errors.Count > 0)
            {
                throw new AggregateException(
                    "[VegetationScatterRecipeGenerator] Generated source rollback failed.",
                    errors);
            }
        }

        public void Dispose()
        {
            if (!m_Accepted)
            {
                Rollback();
            }
            else
            {
                TryDeleteDirectory(m_TransactionRoot);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // A committed transaction can leave only its private staging/backup
                // directory behind; source and registry publication are already complete.
            }
        }
    }

    private readonly record struct VegetationSpeciesAndBiomeClosure(
        Guid BiomeGuid,
        Guid SpeciesGuid);
}

internal sealed class VegetationScatterRecipeSourceDocument
{
    public int Version { get; set; }
    public Guid RecipeGuid { get; set; }
    public VegetationScatterAssetReferenceSource? World { get; set; }
    public VegetationScatterAssetReferenceSource? Biome { get; set; }
    public VegetationScatterAssetReferenceSource? TerrainRoot { get; set; }
    public VegetationScatterCellSource? Cell { get; set; }
    public string EntryId { get; set; } = string.Empty;
    public float UnscaledConservativeRadius { get; set; }
    public List<VegetationScatterExclusionSource> Exclusions { get; set; } = new();
}

internal sealed class VegetationScatterAssetReferenceSource
{
    public Guid Guid { get; set; }
    public string PackageId { get; set; } = string.Empty;
}

internal sealed class VegetationScatterCellSource
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    public string Layer { get; set; } = string.Empty;
}

internal sealed class VegetationScatterExclusionSource
{
    public string Kind { get; set; } = string.Empty;
    public VegetationScatterBoundsSource? Bounds { get; set; }
}

internal sealed class VegetationScatterBoundsSource
{
    public VegetationScatterPositionSource? Min { get; set; }
    public VegetationScatterPositionSource? Max { get; set; }
}

internal sealed class VegetationScatterPositionSource
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
}

internal sealed class VegetationScatterWorldContextDocument
{
    public int Version { get; set; }
    public Guid WorldGuid { get; set; }
    public VegetationScatterWorldPartitionSource? Partition { get; set; }
    public List<VegetationScatterWorldCellSource> Cells { get; set; } = new();
}

internal sealed class VegetationScatterWorldPartitionSource
{
    public VegetationScatterPositionSource? Origin { get; set; }
    public VegetationScatterPositionSource? CellSize { get; set; }
    public int LoadRadius { get; set; }
    public int UnloadHysteresis { get; set; }
    public int MaxActiveCells { get; set; }
}

internal sealed class VegetationScatterWorldCellSource
{
    public VegetationScatterCellCoordinateSource? Coordinate { get; set; }
    public string Layer { get; set; } = string.Empty;
}

internal sealed class VegetationScatterCellCoordinateSource
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
}

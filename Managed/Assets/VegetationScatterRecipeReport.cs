using System.Buffers;
using System.Text.Json;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Vegetation.Assets;

/// <summary>
/// Cook-time report of every vegetation cluster the authored scatter recipes baked.
/// </summary>
/// <remarks>
/// A cell scene binds a cluster through a <c>VegetationCluster</c> component whose page count,
/// instance count, and bounds have to match the cooked closure bit for bit, but those facts only
/// exist once the scatter bake has run. The bake itself is deterministic, so the generator that
/// authors cell scenes reads this report instead of guessing: the report is the published result of
/// the runtime cook, and re-cooking unchanged inputs rewrites it byte for byte.
/// </remarks>
public static class VegetationScatterRecipeReport
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "vegetation-clusters.json";
    private const string ReportKind = "vegetation-scatter-recipes";

    /// <summary>
    /// Report path: the workspace cook cache that also owns the cooked-asset store. The report
    /// describes cook output, so it must live with the cache rather than with authored sources.
    /// </summary>
    public static string GetPath(IAssetDatabase assetDatabase)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        if (string.IsNullOrWhiteSpace(assetDatabase.CookedRoot))
        {
            throw new InvalidOperationException(
                "[VegetationScatterRecipeReport] The asset database has no cooked root to report beside.");
        }

        string cookedRoot = Path.GetFullPath(assetDatabase.CookedRoot);
        string? cacheRoot = Path.GetDirectoryName(
            cookedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(cacheRoot))
        {
            throw new InvalidOperationException(
                $"[VegetationScatterRecipeReport] Cooked root '{cookedRoot}' has no cache directory.");
        }

        return Path.Combine(cacheRoot, FileName);
    }

    public static bool Write(
        IAssetDatabase assetDatabase,
        IReadOnlyList<VegetationScatterRecipeGenerationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        byte[] payload = Serialize(results);
        return VegetationCookedContainer.WriteAtomicallyIfChanged(GetPath(assetDatabase), payload);
    }

    public static byte[] Serialize(
        IReadOnlyList<VegetationScatterRecipeGenerationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        VegetationScatterRecipeGenerationResult[] ordered = results
            .OrderBy(result => result.Recipe.World.Guid)
            .ThenBy(result => result.Recipe.Cell.Coordinate.X)
            .ThenBy(result => result.Recipe.Cell.Coordinate.Z)
            .ThenBy(result => result.Recipe.Cell.Coordinate.Y)
            .ThenBy(result => result.Recipe.Cell.Layer, StringComparer.Ordinal)
            .ThenBy(result => result.Recipe.EntryId, StringComparer.Ordinal)
            .ToArray();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", CurrentSchemaVersion);
            writer.WriteString("report", ReportKind);
            writer.WriteStartArray("clusters");
            foreach (VegetationScatterRecipeGenerationResult result in ordered)
            {
                WriteCluster(writer, result);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCluster(
        Utf8JsonWriter writer,
        VegetationScatterRecipeGenerationResult result)
    {
        VegetationScatterRecipeDescriptor recipe = result.Recipe;
        writer.WriteStartObject();
        writer.WriteString("worldGuid", Format(recipe.World.Guid));
        writer.WriteString("worldPackageId", recipe.World.PackageId);
        writer.WriteString("terrainRootGuid", Format(recipe.TerrainRoot.Guid));
        writer.WriteString("terrainRootPackageId", recipe.TerrainRoot.PackageId);
        writer.WriteString("entryId", recipe.EntryId);
        writer.WriteString("recipeGuid", Format(result.RecipeGuid));
        writer.WriteString("recipePackageId", recipe.PackageId);
        writer.WriteString("clusterGuid", Format(result.ClusterGuid));
        writer.WriteString("clusterPackageId", result.ClusterPackageId);
        writer.WriteString("biomeGuid", Format(recipe.Biome.Guid));
        writer.WriteString("biomePackageId", recipe.Biome.PackageId);
        writer.WriteString("speciesGuid", Format(result.Species.Guid));
        writer.WriteString("speciesPackageId", result.Species.PackageId);
        writer.WriteStartObject("cell");
        writer.WriteNumber("x", recipe.Cell.Coordinate.X);
        writer.WriteNumber("y", recipe.Cell.Coordinate.Y);
        writer.WriteNumber("z", recipe.Cell.Coordinate.Z);
        writer.WriteString("layer", recipe.Cell.Layer);
        writer.WriteEndObject();
        WritePosition(writer, "origin", result.Origin);
        writer.WriteStartObject("bounds");
        WritePosition(writer, "min", result.Bounds.Min);
        WritePosition(writer, "max", result.Bounds.Max);
        writer.WriteEndObject();
        writer.WriteNumber("pageCount", result.PageGuids.Count);
        writer.WriteNumber("instanceCount", result.InstanceCount);
        writer.WriteEndObject();
    }

    private static void WritePosition(Utf8JsonWriter writer, string name, WorldPosition position)
    {
        writer.WriteStartObject(name);
        writer.WriteNumber("x", position.X);
        writer.WriteNumber("y", position.Y);
        writer.WriteNumber("z", position.Z);
        writer.WriteEndObject();
    }

    private static string Format(Guid value) => value.ToString("D");
}

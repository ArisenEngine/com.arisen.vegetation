using System.Security.Cryptography;
using ArisenEngine.Core.Assets;
using ArisenEngine.Resources.Serialization;

namespace ArisenEngine.Vegetation.Assets;

public static class VegetationResidentAssetLoader
{
    public static bool TryLoadCluster(
        IAssetDatabase assetDatabase,
        in RuntimeAssetPreparationClaim claim,
        out CookedVegetationCluster cluster,
        out string diagnostic)
    {
        if (!TryGetPayload(
                assetDatabase,
                claim,
                VegetationAssetTypes.Cluster,
                VegetationClusterAssetCooker.RuntimeVariant,
                out ReadOnlyMemory<byte> payload,
                out string diagnosticPath,
                out diagnostic))
        {
            cluster = null!;
            return false;
        }

        return VegetationClusterAssetCooker.TryReadPayload(
            claim.Key.Guid,
            claim.Key.PackageId,
            payload.Span,
            diagnosticPath,
            out cluster,
            out diagnostic);
    }

    public static bool TryLoadPage(
        IAssetDatabase assetDatabase,
        in RuntimeAssetPreparationClaim claim,
        out CookedVegetationInstancePage page,
        out VegetationInstancePagePayloadIdentity payloadIdentity,
        out string diagnostic)
    {
        payloadIdentity = default;
        if (!TryGetPayload(
                assetDatabase,
                claim,
                VegetationAssetTypes.InstancePage,
                VegetationInstancePageAssetCooker.RuntimeVariant,
                out ReadOnlyMemory<byte> payload,
                out string diagnosticPath,
                out diagnostic))
        {
            page = null!;
            return false;
        }

        if (payload.Length < 56)
        {
            page = null!;
            diagnostic =
                $"[VegetationResidentAssetLoader] Cooked instance page '{diagnosticPath}' " +
                "is truncated before its parent cluster identity.";
            return false;
        }

        Guid clusterGuid = VegetationCookedContainer.ReadGuid(payload.Span[40..56]);
        if (clusterGuid == Guid.Empty ||
            !VegetationInstancePageAssetCooker.TryReadPayload(
                claim.Key.Guid,
                clusterGuid,
                claim.Key.PackageId,
                payload.Span,
                diagnosticPath,
                out page,
                out diagnostic))
        {
            page = null!;
            if (clusterGuid == Guid.Empty)
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Cooked instance page '{diagnosticPath}' " +
                    "has an empty parent cluster identity.";
            }
            return false;
        }

        payloadIdentity = new VegetationInstancePagePayloadIdentity(
            page.Guid,
            payload.Length,
            SHA256.HashData(payload.Span));
        return true;
    }

    public static bool TryLoadSpecies(
        IAssetDatabase assetDatabase,
        in RuntimeAssetPreparationClaim claim,
        out CookedVegetationSpecies species,
        out string diagnostic)
    {
        if (!TryGetPayload(
                assetDatabase,
                claim,
                VegetationAssetTypes.Species,
                VegetationSpeciesAssetCooker.RuntimeVariant,
                out ReadOnlyMemory<byte> payload,
                out string diagnosticPath,
                out diagnostic))
        {
            species = null!;
            return false;
        }

        return VegetationSpeciesAssetCooker.TryReadPayload(
            claim.Key.Guid,
            claim.Key.PackageId,
            payload.Span,
            diagnosticPath,
            out species,
            out diagnostic);
    }

    public static bool TryLoadBiome(
        IAssetDatabase assetDatabase,
        in RuntimeAssetPreparationClaim claim,
        out CookedVegetationBiome biome,
        out string diagnostic)
    {
        if (!TryGetPayload(
                assetDatabase,
                claim,
                VegetationAssetTypes.Biome,
                VegetationBiomeAssetCooker.RuntimeVariant,
                out ReadOnlyMemory<byte> payload,
                out string diagnosticPath,
                out diagnostic))
        {
            biome = null!;
            return false;
        }

        return VegetationBiomeAssetCooker.TryReadPayload(
            claim.Key.Guid,
            claim.Key.PackageId,
            payload.Span,
            diagnosticPath,
            out biome,
            out diagnostic);
    }

    public static RuntimeAssetResidencyKey[] GetRequiredResidencyKeys(
        CookedVegetationCluster cluster)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        return BuildRequiredResidencyKeys(
            VegetationClusterAssetCooker.BuildDependencies(cluster));
    }

    public static RuntimeAssetResidencyKey[] GetRequiredResidencyKeys(
        CookedVegetationInstancePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return BuildRequiredResidencyKeys(
            VegetationInstancePageAssetCooker.BuildDependencies(page),
            new RuntimeAssetResidencyKey(
                page.ClusterGuid,
                page.PackageId,
                VegetationAssetTypes.Cluster,
                VegetationClusterAssetCooker.RuntimeVariant));
    }

    public static RuntimeAssetResidencyKey[] GetRequiredResidencyKeys(
        CookedVegetationSpecies species)
    {
        ArgumentNullException.ThrowIfNull(species);
        return BuildRequiredResidencyKeys(
            VegetationSpeciesAssetCooker.BuildDependencies(species));
    }

    public static RuntimeAssetResidencyKey[] GetRequiredResidencyKeys(
        CookedVegetationBiome biome)
    {
        ArgumentNullException.ThrowIfNull(biome);
        return BuildRequiredResidencyKeys(
            VegetationBiomeAssetCooker.BuildDependencies(biome));
    }

    public static bool TryValidateClusterClosure(
        IAssetDatabase assetDatabase,
        CookedVegetationCluster cluster,
        IReadOnlyList<RuntimeAssetPreparationClaim> exactRequiredClaims,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(cluster);
        RuntimeAssetResidencyKey[] requiredKeys = GetRequiredResidencyKeys(cluster);
        if (!TryValidateExactClaims(
                $"vegetation cluster '{cluster.Guid:D}'",
                requiredKeys,
                exactRequiredClaims,
                out diagnostic))
        {
            return false;
        }

        RuntimeAssetResidencyKey biomeKey = ToResidencyKey(
            cluster.Biome.Guid,
            cluster.Biome.PackageId,
            VegetationAssetTypes.Biome,
            VegetationBiomeAssetCooker.RuntimeVariant);
        if (!TryFindExactClaim(
                requiredKeys,
                exactRequiredClaims,
                biomeKey,
                out RuntimeAssetPreparationClaim biomeClaim) ||
            !TryLoadBiome(
                assetDatabase,
                biomeClaim,
                out CookedVegetationBiome biome,
                out diagnostic))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' exact held biome " +
                $"closure is invalid: {diagnostic}";
            return false;
        }

        var biomeSpecies = new HashSet<SpeciesIdentity>();
        foreach (CookedVegetationBiomeEntry entry in biome.Entries)
        {
            biomeSpecies.Add(new SpeciesIdentity(
                entry.Species.Guid,
                entry.Species.PackageId));
        }

        foreach (CookedVegetationSpeciesReference speciesReference in cluster.Species)
        {
            if (!biomeSpecies.Contains(new SpeciesIdentity(
                    speciesReference.Guid,
                    speciesReference.PackageId)))
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' species " +
                    $"'{speciesReference.PackageId}:{speciesReference.Guid:D}' is not declared " +
                    $"by its exact held biome '{biome.PackageId}:{biome.Guid:D}'.";
                return false;
            }

            RuntimeAssetResidencyKey speciesKey = ToResidencyKey(
                speciesReference.Guid,
                speciesReference.PackageId,
                VegetationAssetTypes.Species,
                VegetationSpeciesAssetCooker.RuntimeVariant);
            if (!TryFindExactClaim(
                    requiredKeys,
                    exactRequiredClaims,
                    speciesKey,
                    out RuntimeAssetPreparationClaim speciesClaim) ||
                !TryLoadSpecies(assetDatabase, speciesClaim, out _, out diagnostic))
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' exact held " +
                    $"species closure is invalid: {diagnostic}";
                return false;
            }
        }

        var loadedPages = new CookedVegetationInstancePage[cluster.Pages.Count];
        for (int index = 0; index < cluster.Pages.Count; index++)
        {
            CookedVegetationInstancePageReference reference = cluster.Pages[index];
            RuntimeAssetResidencyKey pageKey = ToResidencyKey(
                reference.Guid,
                reference.PackageId,
                VegetationAssetTypes.InstancePage,
                VegetationInstancePageAssetCooker.RuntimeVariant);
            if (!TryFindExactClaim(
                    requiredKeys,
                    exactRequiredClaims,
                    pageKey,
                    out RuntimeAssetPreparationClaim pageClaim) ||
                !TryLoadPage(
                    assetDatabase,
                    pageClaim,
                    out CookedVegetationInstancePage page,
                    out VegetationInstancePagePayloadIdentity payloadIdentity,
                    out diagnostic))
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' exact held page " +
                    $"closure is invalid: {diagnostic}";
                return false;
            }

            if (!TryValidatePageReference(
                    cluster,
                    reference,
                    page,
                    payloadIdentity,
                    out diagnostic))
            {
                return false;
            }

            loadedPages[index] = page;
        }

        if (!TryBuildCanonicalSpecies(
                cluster,
                loadedPages,
                out CookedVegetationSpeciesReference[] pageSpecies,
                out diagnostic) ||
            !pageSpecies.SequenceEqual(cluster.Species))
        {
            diagnostic = diagnostic.Length > 0
                ? diagnostic
                : $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' species table " +
                  "does not exactly equal the canonical species union of its held pages.";
            return false;
        }

        if (!TryCountInstancesAndValidateStableKeys(
                cluster,
                loadedPages,
                out int instanceCount,
                out diagnostic))
        {
            return false;
        }

        if (instanceCount != cluster.InstanceCount)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' held page instance " +
                $"count '{instanceCount}' does not match root count '{cluster.InstanceCount}'.";
            return false;
        }

        if (!BoundsBitwiseEqual(ComputeBounds(loadedPages), cluster.Bounds))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' held page bounds " +
                "do not exactly equal the root bounds.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    public static bool TryValidatePageClosure(
        IAssetDatabase assetDatabase,
        CookedVegetationInstancePage page,
        in VegetationInstancePagePayloadIdentity payloadIdentity,
        IReadOnlyList<RuntimeAssetPreparationClaim> exactRequiredClaims,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(page);
        RuntimeAssetResidencyKey[] requiredKeys = GetRequiredResidencyKeys(page);
        if (!TryValidateExactClaims(
                $"vegetation instance page '{page.Guid:D}'",
                requiredKeys,
                exactRequiredClaims,
                out diagnostic))
        {
            return false;
        }

        RuntimeAssetResidencyKey parentKey = ToResidencyKey(
            page.ClusterGuid,
            page.PackageId,
            VegetationAssetTypes.Cluster,
            VegetationClusterAssetCooker.RuntimeVariant);
        if (!TryFindExactClaim(
                requiredKeys,
                exactRequiredClaims,
                parentKey,
                out RuntimeAssetPreparationClaim parentClaim) ||
            !TryLoadCluster(
                assetDatabase,
                parentClaim,
                out CookedVegetationCluster parent,
                out diagnostic))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' exact held parent cluster " +
                $"is invalid: {diagnostic}";
            return false;
        }

        CookedVegetationInstancePageReference? parentReference = null;
        foreach (CookedVegetationInstancePageReference candidate in parent.Pages)
        {
            if (candidate.Guid == page.Guid &&
                string.Equals(candidate.PackageId, page.PackageId, StringComparison.Ordinal))
            {
                parentReference = candidate;
                break;
            }
        }

        if (parentReference == null)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' is not an exact reciprocal " +
                $"member of held parent cluster '{parent.Guid:D}'.";
            return false;
        }

        if (!TryValidatePageReference(
                parent,
                parentReference,
                page,
                payloadIdentity,
                out diagnostic))
        {
            return false;
        }

        var parentSpecies = new HashSet<SpeciesIdentity>(
            parent.Species.Select(reference => new SpeciesIdentity(
                reference.Guid,
                reference.PackageId)));
        foreach (CookedVegetationSpeciesReference speciesReference in page.Species)
        {
            if (!parentSpecies.Contains(new SpeciesIdentity(
                    speciesReference.Guid,
                    speciesReference.PackageId)))
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' species " +
                    $"'{speciesReference.PackageId}:{speciesReference.Guid:D}' is absent from " +
                    $"held parent cluster '{parent.Guid:D}'.";
                return false;
            }

            RuntimeAssetResidencyKey speciesKey = ToResidencyKey(
                speciesReference.Guid,
                speciesReference.PackageId,
                VegetationAssetTypes.Species,
                VegetationSpeciesAssetCooker.RuntimeVariant);
            if (!TryFindExactClaim(
                    requiredKeys,
                    exactRequiredClaims,
                    speciesKey,
                    out RuntimeAssetPreparationClaim speciesClaim) ||
                !TryLoadSpecies(assetDatabase, speciesClaim, out _, out diagnostic))
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' exact held species " +
                    $"closure is invalid: {diagnostic}";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    public static bool TryValidateSpeciesClosure(
        CookedVegetationSpecies species,
        IReadOnlyList<RuntimeAssetPreparationClaim> exactRequiredClaims,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(species);
        return TryValidateExactClaims(
            $"vegetation species '{species.Guid:D}' mesh/material",
            GetRequiredResidencyKeys(species),
            exactRequiredClaims,
            out diagnostic);
    }

    public static bool TryValidateBiomeClosure(
        IAssetDatabase assetDatabase,
        CookedVegetationBiome biome,
        IReadOnlyList<RuntimeAssetPreparationClaim> exactRequiredClaims,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        ArgumentNullException.ThrowIfNull(biome);
        RuntimeAssetResidencyKey[] requiredKeys = GetRequiredResidencyKeys(biome);
        if (!TryValidateExactClaims(
                $"vegetation biome '{biome.Guid:D}' species",
                requiredKeys,
                exactRequiredClaims,
                out diagnostic))
        {
            return false;
        }

        for (int index = 0; index < requiredKeys.Length; index++)
        {
            if (!TryLoadSpecies(
                    assetDatabase,
                    exactRequiredClaims[index],
                    out _,
                    out diagnostic))
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] Biome '{biome.Guid:D}' exact held species " +
                    $"closure is invalid: {diagnostic}";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static RuntimeAssetResidencyKey[] BuildRequiredResidencyKeys(
        IReadOnlyList<VegetationCookedAssetDependency> dependencies,
        RuntimeAssetResidencyKey additionalKey = default)
    {
        var keys = new SortedSet<RuntimeAssetResidencyKey>();
        for (int index = 0; index < dependencies.Count; index++)
        {
            VegetationCookedAssetDependency dependency = dependencies[index];
            if (dependency.Required)
            {
                keys.Add(ToResidencyKey(
                    dependency.Guid,
                    dependency.PackageId,
                    dependency.AssetType,
                    dependency.Variant));
            }
        }

        if (additionalKey.IsValid)
        {
            keys.Add(additionalKey);
        }

        return keys.ToArray();
    }

    private static RuntimeAssetResidencyKey ToResidencyKey(
        Guid guid,
        string packageId,
        string assetType,
        string variant) => new(guid, packageId, assetType, variant);

    private static bool TryValidateExactClaims(
        string context,
        IReadOnlyList<RuntimeAssetResidencyKey> requiredKeys,
        IReadOnlyList<RuntimeAssetPreparationClaim>? exactRequiredClaims,
        out string diagnostic)
    {
        if (exactRequiredClaims == null || exactRequiredClaims.Count != requiredKeys.Count)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] {context} requires exactly " +
                $"'{requiredKeys.Count}' residency-held dependency claims.";
            return false;
        }

        for (int index = 0; index < requiredKeys.Count; index++)
        {
            RuntimeAssetPreparationClaim claim = exactRequiredClaims[index];
            if (!claim.IsValid || claim.Key != requiredKeys[index])
            {
                diagnostic =
                    $"[VegetationResidentAssetLoader] {context} residency-held dependency claim " +
                    $"'{index}' does not match exact key '{requiredKeys[index]}'.";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryFindExactClaim(
        IReadOnlyList<RuntimeAssetResidencyKey> requiredKeys,
        IReadOnlyList<RuntimeAssetPreparationClaim> exactRequiredClaims,
        RuntimeAssetResidencyKey key,
        out RuntimeAssetPreparationClaim claim)
    {
        int minimum = 0;
        int maximum = requiredKeys.Count - 1;
        while (minimum <= maximum)
        {
            int index = minimum + ((maximum - minimum) / 2);
            int comparison = requiredKeys[index].CompareTo(key);
            if (comparison == 0)
            {
                claim = exactRequiredClaims[index];
                return true;
            }

            if (comparison < 0)
            {
                minimum = index + 1;
            }
            else
            {
                maximum = index - 1;
            }
        }

        claim = default;
        return false;
    }

    private static bool TryValidatePageReference(
        CookedVegetationCluster cluster,
        CookedVegetationInstancePageReference reference,
        CookedVegetationInstancePage page,
        in VegetationInstancePagePayloadIdentity payloadIdentity,
        out string diagnostic)
    {
        if (page.ClusterGuid != cluster.Guid)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' parent cluster " +
                $"'{page.ClusterGuid:D}' does not match exact parent '{cluster.Guid:D}'.";
            return false;
        }

        if (page.Guid != reference.Guid ||
            !string.Equals(page.PackageId, reference.PackageId, StringComparison.Ordinal))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' identity/package does not " +
                "match its exact parent-cluster membership.";
            return false;
        }

        if (page.GeneratedSchemaVersion != cluster.GeneratedSchemaVersion)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' generated schema " +
                $"'{page.GeneratedSchemaVersion}' does not match parent cluster schema " +
                $"'{cluster.GeneratedSchemaVersion}'.";
            return false;
        }

        if (page.Instances.Count != reference.InstanceCount)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' instance count does not " +
                "match its exact parent-cluster reference.";
            return false;
        }

        if (!PositionBitwiseEqual(page.Origin, reference.Origin))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' origin does not match its " +
                "exact parent-cluster reference.";
            return false;
        }

        if (!BoundsBitwiseEqual(page.Bounds, reference.Bounds))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' bounds do not match its " +
                "exact parent-cluster reference.";
            return false;
        }

        if (!payloadIdentity.IsValid ||
            payloadIdentity.PageGuid != reference.Guid ||
            payloadIdentity.SizeInBytes != reference.SizeInBytes)
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' exact cooked byte size " +
                "does not match its parent-cluster pin.";
            return false;
        }

        if (!payloadIdentity.ContentHash.SequenceEqual(reference.ContentHash))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Page '{page.Guid:D}' full-file SHA-256 hash " +
                "does not match its parent-cluster pin.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private static bool TryBuildCanonicalSpecies(
        CookedVegetationCluster cluster,
        IReadOnlyList<CookedVegetationInstancePage> pages,
        out CookedVegetationSpeciesReference[] species,
        out string diagnostic)
    {
        var canonical = new SortedSet<CookedVegetationSpeciesReference>(SpeciesComparer.Instance);
        var packageByGuid = new Dictionary<Guid, string>();
        foreach (CookedVegetationInstancePage page in pages)
        {
            foreach (CookedVegetationSpeciesReference reference in page.Species)
            {
                if (packageByGuid.TryGetValue(reference.Guid, out string? packageId) &&
                    !string.Equals(packageId, reference.PackageId, StringComparison.Ordinal))
                {
                    species = Array.Empty<CookedVegetationSpeciesReference>();
                    diagnostic =
                        $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' held pages " +
                        $"assign species '{reference.Guid:D}' to conflicting package owners.";
                    return false;
                }

                packageByGuid.TryAdd(reference.Guid, reference.PackageId);
                canonical.Add(reference);
            }
        }

        species = canonical.ToArray();
        diagnostic = string.Empty;
        return true;
    }

    private static bool TryCountInstancesAndValidateStableKeys(
        CookedVegetationCluster cluster,
        IReadOnlyList<CookedVegetationInstancePage> pages,
        out int instanceCount,
        out string diagnostic)
    {
        long total = 0;
        var cursors = new PriorityQueue<PageInstanceCursor, ulong>(pages.Count);
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            CookedVegetationInstancePage page = pages[pageIndex];
            total += page.Instances.Count;
            if (total > int.MaxValue)
            {
                instanceCount = 0;
                diagnostic =
                    $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' held page " +
                    "instance count exceeds the runtime range.";
                return false;
            }

            if (page.Instances.Count > 0)
            {
                cursors.Enqueue(
                    new PageInstanceCursor(pageIndex, 0),
                    page.Instances[0].StableKey);
            }
        }

        bool hasPrevious = false;
        ulong previousStableKey = 0;
        while (cursors.TryDequeue(out PageInstanceCursor cursor, out ulong stableKey))
        {
            if (hasPrevious && stableKey == previousStableKey)
            {
                instanceCount = 0;
                diagnostic =
                    $"[VegetationResidentAssetLoader] Cluster '{cluster.Guid:D}' stable instance " +
                    $"key '{stableKey}' appears in more than one held page.";
                return false;
            }

            hasPrevious = true;
            previousStableKey = stableKey;
            CookedVegetationInstancePage page = pages[cursor.PageIndex];
            int nextInstanceIndex = cursor.InstanceIndex + 1;
            if (nextInstanceIndex < page.Instances.Count)
            {
                cursors.Enqueue(
                    new PageInstanceCursor(cursor.PageIndex, nextInstanceIndex),
                    page.Instances[nextInstanceIndex].StableKey);
            }
        }

        instanceCount = checked((int)total);
        diagnostic = string.Empty;
        return true;
    }

    private static WorldBounds ComputeBounds(
        IReadOnlyList<CookedVegetationInstancePage> pages)
    {
        WorldBounds bounds = pages[0].Bounds;
        for (int index = 1; index < pages.Count; index++)
        {
            WorldBounds pageBounds = pages[index].Bounds;
            bounds = new WorldBounds(
                new WorldPosition(
                    Math.Min(bounds.Min.X, pageBounds.Min.X),
                    Math.Min(bounds.Min.Y, pageBounds.Min.Y),
                    Math.Min(bounds.Min.Z, pageBounds.Min.Z)),
                new WorldPosition(
                    Math.Max(bounds.Max.X, pageBounds.Max.X),
                    Math.Max(bounds.Max.Y, pageBounds.Max.Y),
                    Math.Max(bounds.Max.Z, pageBounds.Max.Z)));
        }

        return bounds;
    }

    private static bool PositionBitwiseEqual(WorldPosition left, WorldPosition right) =>
        DoubleBitwiseEqual(left.X, right.X) &&
        DoubleBitwiseEqual(left.Y, right.Y) &&
        DoubleBitwiseEqual(left.Z, right.Z);

    private static bool BoundsBitwiseEqual(WorldBounds left, WorldBounds right) =>
        PositionBitwiseEqual(left.Min, right.Min) &&
        PositionBitwiseEqual(left.Max, right.Max);

    private static bool DoubleBitwiseEqual(double left, double right) =>
        BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right);

    private static bool TryGetPayload(
        IAssetDatabase assetDatabase,
        in RuntimeAssetPreparationClaim claim,
        string expectedAssetType,
        string expectedVariant,
        out ReadOnlyMemory<byte> payload,
        out string diagnosticPath,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);
        payload = default;
        diagnosticPath = claim.Key.IsValid
            ? claim.Key.ToString()
            : "invalid-residency-claim";
        if (!claim.IsValid ||
            !string.Equals(claim.Key.AssetType, expectedAssetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(claim.Key.Variant, expectedVariant, StringComparison.Ordinal))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Residency claim '{diagnosticPath}' does not " +
                $"identify '{expectedAssetType}:{expectedVariant}'.";
            return false;
        }

        diagnosticPath =
            $"{claim.Key}:resident-handle-{claim.CookedHandle.Index}:{claim.CookedHandle.Generation}";
        if (!assetDatabase.TryGetCookedAssetBytes(claim.CookedHandle, out payload))
        {
            diagnostic =
                $"[VegetationResidentAssetLoader] Residency-held cooked payload " +
                $"'{diagnosticPath}' is no longer available.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    private readonly record struct SpeciesIdentity(Guid Guid, string PackageId);

    private readonly record struct PageInstanceCursor(int PageIndex, int InstanceIndex);

    private sealed class SpeciesComparer : IComparer<CookedVegetationSpeciesReference>
    {
        internal static SpeciesComparer Instance { get; } = new();

        public int Compare(
            CookedVegetationSpeciesReference left,
            CookedVegetationSpeciesReference right)
        {
            int comparison = StringComparer.Ordinal.Compare(left.PackageId, right.PackageId);
            return comparison != 0 ? comparison : left.Guid.CompareTo(right.Guid);
        }
    }
}

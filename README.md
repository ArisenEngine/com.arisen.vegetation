# com.arisen.vegetation

Backend-neutral vegetation domain package for Arisen Engine.

This package owns stable vegetation identity, species and biome authoring data,
cooked species/biome/cluster/page formats, deterministic terrain-aware scatter
planning, runtime cluster data, bounded queries, diagnostics, and
authoring-preview publication. It remains independent from concrete render
pipelines, Editor UI, and RHI backends.

## Terrain-Aware Scatter Boundary

`VegetationScatterBaker.Build(IAssetDatabase, VegetationScatterCookRequest)` is
the trusted setup-time entry point. It loads cooked biome, species, and terrain
data, verifies every terrain tile against the exact size and SHA-256 pin stored
by the cooked terrain root, and never falls back to source assets. The decoded
record overload is internal for focused tests.

Candidate generation is stateless and seed-driven over signed one-meter spatial
cells. Terrain height, normal, slope, altitude, normalized layer weights,
density, exclusion policy, minimum spacing, and world-cell ownership are
evaluated deterministically. Placements are narrowed to the page's cell-local
float representation before spacing and ownership decisions. Stable-key order
populates sparse spacing-sized buckets incrementally, so only lower-key
candidates are searched and the accepted-page limit fails immediately. Cluster
identity includes world-cell, terrain package/GUID, species package/GUID,
biome, and entry ownership; page identity includes a SHA-256 of the
codec-canonical instance records and excludes pre-codec recipe values.

The current result contains an explicit valid-empty plan or bounded non-empty
pages partitioned in stable-key order, a logical cluster descriptor, generated
metadata, per-page hashes, a placement hash, and reconciled reject metrics. The
recipe generator stages the complete generated source set and publishes the
cluster/page closure transactionally through `IAssetDatabase.PublishCookedArtifactSet`.
Unchanged pages retain their artifacts; stale generated identities are removed
only from the exact prior closure, and a failed replacement restores the prior
source and cooked state.

## Runtime Boundary

The package also owns the cluster scene codec, cell-owned generation-qualified
CPU residency, bounded query/diagnostic services, and authoring-preview
publication. Generic Render Pipeline integration, including setup-owned
culling/LOD and GPU resource preparation, remains in
`com.arisen.vegetation.generic-renderpipeline`.

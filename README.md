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

The current result contains one non-empty bounded instance page, its logical
cluster descriptor, generated metadata, a placement hash, and reconciled reject
metrics. The caller registers the generated identities and publishes through
`VegetationClusterAssetCooker`, which validates and commits the page before the
cluster root.

## Current Limits

- Valid empty cells do not yet return an explicit no-output plan.
- Accepted instances are not yet partitioned into multiple pages.
- Publishing a replacement cluster closure does not atomically remove stale
  page rows and files or roll back already committed child pages.
- Scene codecs, runtime residency, rendering, and Editor scatter tooling remain
  later milestones.

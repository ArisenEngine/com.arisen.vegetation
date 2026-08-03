# com.arisen.vegetation

Backend-neutral vegetation domain package for Arisen Engine.

This package owns stable vegetation identity, runtime cluster data, bounded
queries, diagnostics, and authoring-preview publication. It must remain
independent from concrete render pipelines, Editor UI, and RHI backends.

The initial package spine publishes immutable snapshots through narrow service
contracts. Species, biome, cooked cluster, scatter, scene, and residency data
will be added without changing the package direction established here.

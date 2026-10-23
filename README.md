# Unity URP Moment Shadow Mapping

Source-only learning implementation of four-moment shadow mapping with an integer
summed-area table (SAT). Tested environment: Unity **6000.6.7f1**, URP **17.6.0**,
macOS / Metal. This is an experimental baseline, not a production shadow package.

## Repository scope

Tracked files are limited to the renderer feature, compute shader, their Unity
metadata and this README. Scenes, renderer/pipeline assets,
materials, third-party plugins, project settings, generated files and GPU captures
remain local and are ignored by an explicit allowlist.
When adding a new algorithm source file, add its path and metadata to the
allowlist deliberately; new Unity assets are ignored by default.

This checkout is **not a complete Unity project**. Create a compatible URP project
or import the source into an existing one. The current lab's untracked files are not part of this source-only repository.

## Integration

1. Copy `Assets/MSM` and `Assets/MSM.meta` into a compatible URP project.
   Only the two tracked source assets are required; do not copy local ignored
   debug files. Preserve the tracked `.meta` GUIDs. Remove an older copy using
   the same GUID before importing this one.
2. Add **MsmRendererFeature** to the active Universal Renderer Data asset.
3. Assign `ShadowMoments.compute` to its Compute Shader field and select
   **Full MSM**.
4. Start with a Forward/Forward+ renderer, one realtime directional light,
   **one cascade**, and a simple opaque URP Lit receiver/caster setup.
   The feature requests camera depth and restores the main-light shadow keywords
   before transparents.

The feature file was renamed from `ShadowTest.cs`; its GUID and serialized
`momentsSettings` field were preserved for the existing lab renderer.
The old grayscale debug feature is not included in this repository.

## Data path

Shadow depth → quantized four moments → tiled uint4 SAT → screen-space MSM
visibility → URP opaque main-light shadow sampling.

- S.1 `BuildTileSummaries`: local column tails and 2D right-edge summaries.
- S.2 `CompleteTileColumns`: propagate column summaries; reduce strip totals.
- S.3 `CompleteTileRows`: propagate complete left boundaries, including above strips.
- S.4 `BuildTileSAT`: reconstruct the full SAT from depth, top and left contributions.

Tiles are 16×16. S.1/S.4 use 64 cooperative I/O threads and shared local scans.
S.2 batches eight boundaries; S.4 caches the left edge. S.1 and S.4 recompute
moments directly from depth instead of storing a full moments intermediate.

Each uint4 channel is an independent moment. Prefixes wrap modulo 2^32.
The quantization scale assumes each integer rectangle queried contains at most
**256 texels**. Do not enlarge search/filter windows without revisiting this
bound, including the integer rectangles used by fractional-coverage integration.

Resolve reconstructs world position from camera depth, transforms to light shadow
coordinates, integrates a fixed 8×8-texel window, decodes average moments and runs
Hamburger MSM. It uses background early-out, streamed SAT integration and R8
visibility. Moment bias is 3e-5; receiver depth bias is 1e-4.

## Benchmark modes

| Mode | Work before fixed/actual visibility |
| --- | --- |
| Full MSM | Tiled SAT and actual MSM Resolve |
| A Constant | Fixed visibility 0.5 |
| B Moments | Standalone moments generation, then visibility 0.5 |
| C Moments And SAT | Tiled SAT, then visibility 0.5 |

B and C have different producer paths; subtracting their whole-frame times does
not isolate a pure SAT dispatch cost. Compare per-dispatch GPU timings with the
same scene/settings and repeat baseline/variant/baseline. Encoder labels may
contain more than one dispatch. No portable performance guarantee is claimed.

## Current limits and next milestone

- Only the main directional light and a single-cascade atlas have been validated
  in the lab workflow. Point/spot lights and cascade-isolated filtering are absent.
- Filtering is fixed-width. Blocker-depth estimation and contact hardening are
  **not implemented**.
- Transparent receivers retain URP's original shadow route.
- Multi-camera/XR/portal rendering, mixed/baked-lighting parity, player-build
  shader stripping and complete shadow-distance/fade behavior are not validated.
- The implementation depends on the installed URP shader/API conventions;
  re-check projection orientation and depth conventions when changing versions.

Next: query search moments, reconstruct candidate blockers, estimate average
blocker depth, derive a directional-light penumbra radius, and query the same SAT
with that dynamic filter size. Keep queries within the integer precision bound.

## References

- Peters et al., [Improved Moment Shadow Maps for Translucent Occluders,
  Soft Shadows and Single Scattering](https://jcgt.org/published/0006/01/03/), 2017.
- Nehab et al., [GPU-efficient recursive filtering and summed-area tables](https://hhoppe.com/proj/recursive/), 2011.

This is a learning implementation with AI assistance. These references describe
the algorithms; this repository does not claim to reproduce the authors' exact
code, hardware configuration or timings.

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

Resolve reconstructs world position from camera depth and transforms to light
shadow coordinates. A first SAT query supplies moments for average blocker-depth
estimation. The receiver/blocker gap determines a directional-light penumbra
radius. A second query supplies filtered moments for Hamburger MSM visibility.
Both queries reuse the same SAT. Resolve keeps background early-out, streamed
SAT integration and R8 visibility. Moment bias is 3e-5; receiver depth bias is 1e-4.

Shader constants currently use a 7.5-texel blocker-search radius, a filter-radius
range of 0.5–7.5 texels, and an angular slope of 0.05 (tan of the emitter half-angle).
The matrix row scales convert signed depth differences to world distances and
world radii to atlas texels. This assumes one orthographic directional-light
cascade. Increasing the angular slope simulates a larger light, not higher quality.
Blocker estimation rejects non-finite nodes, near-zero weight denominators and
non-finite averages by returning receiver depth, selecting minimum filtering.

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

## Current limits and validation

- Only the main directional light and a single-cascade atlas have been validated
  in the lab workflow. Point/spot lights and cascade-isolated filtering are absent.
- Basic contact hardening is implemented. The learner reports softer edges as a
  caster moves away from the receiver in the lab scene. This is a visual check,
  not validation across arbitrary depth distributions or scenes.
- Search/filter radii are capped for integer precision; large penumbrae are
  truncated. The search is fixed in texels, not a general physical search bound.
- The final Hamburger visibility reconstruction still has unguarded degenerate
  divisions. Blocker fallbacks do not make every reconstruction numerically robust.
- Transparent receivers retain URP's original shadow route.
- Multi-camera/XR/portal rendering, mixed/baked-lighting parity, player-build
  shader stripping and complete shadow-distance/fade behavior are not validated.
- The implementation depends on the installed URP shader/API conventions;
  re-check projection orientation and depth conventions when changing versions.

In the lab, increasing the angular slope exposed a gray outer contour. A fixed
filter removed it; increasing the search radius from 4 to 7.5 also removed it with
dynamic filtering restored. This supports insufficient search coverage as a
contributor, not a universal proof that search radius equals maximum filter radius.
Scene-view wheel zoom shows edge stepping in both this feature and default URP
shadows; the exact screen-sampling/projection contribution remains unisolated.

## References

- Peters et al., [Improved Moment Shadow Maps for Translucent Occluders,
  Soft Shadows and Single Scattering](https://jcgt.org/published/0006/01/03/), 2017.
- Nehab et al., [GPU-efficient recursive filtering and summed-area tables](https://hhoppe.com/proj/recursive/), 2011.

This is a learning implementation with AI assistance. These references describe
the algorithms; this repository does not claim to reproduce the authors' exact
code, hardware configuration or timings.

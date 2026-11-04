# Moment Soft Shadows for Unity URP

A real-time soft-shadow rendering project built on Unity's Universal Render
Pipeline. It combines four-moment shadow mapping, an integer summed-area table
(SAT), and blocker-depth estimation to produce contact-hardening shadows for the
main directional light.

The project explores the trade-offs between shadow quality, numerical precision,
GPU memory traffic, and temporal stability. A near-contact PCF fallback preserves
a sharper depth-comparison result where the estimated penumbra is small.

**Status:** Functional rendering prototype. Temporal stability and near-contact
quality remain under evaluation.

## Features

- **Contact-hardening soft shadows** — estimates average blocker depth and adapts
  the filter radius to the receiver–blocker separation.
- **Four-moment reconstruction** — uses Hamburger MSM to estimate visibility
  from filtered depth moments.
- **Integer SAT filtering** — supports variable-size rectangular queries with
  fractional texel coverage and bounded modular arithmetic.
- **Tiled GPU construction** — builds the SAT through local scans and boundary
  propagation, avoiding a full-resolution intermediate moment texture.
- **Near-contact PCF fallback** — uses bilinear PCF for small penumbras and blends
  smoothly into MSM as the estimated radius increases.
- **Cascade-aware sampling** — selects URP's main-light cascade and clips query
  regions to its atlas slice.
- **URP integration** — provides opaque main-light visibility through the
  screen-space shadow texture using a Renderer Feature and Render Graph.

## Rendering Pipeline

```text
URP main-light shadow depth
    ↓
Tiled moment encoding and integer SAT construction
    ↓
Camera-depth reconstruction and cascade selection
    ↓
SAT blocker-depth estimation
    ↓
World-space separation → filter radius in shadow texels
    ↓
Near contact: bilinear PCF
Transition:   PCF / MSM blend
Wider region: filtered SAT moments → MSM reconstruction
    ↓
Screen-space visibility → URP opaque lighting
```

### Tiled SAT Construction

The compute shader processes 16 × 16 tiles in four stages:

| Stage | Kernel | Purpose |
| --- | --- | --- |
| S.1 | `BuildTileSummaries` | Compute local column tails and right-edge prefix summaries. |
| S.2 | `CompleteTileColumns` | Propagate column summaries and compute strip totals. |
| S.3 | `CompleteTileRows` | Complete left-boundary prefixes across tiles and strips. |
| S.4 | `BuildTileSAT` | Reconstruct the full SAT from local depth and completed boundaries. |

S.1 and S.4 generate quantized moments directly from depth. Cooperative I/O,
batched boundary processing, and shared left-edge caching reduce intermediate
storage and repeated memory access.

Each SAT texel contains four unsigned 32-bit channels. Prefix sums wrap modulo
2³²; rectangle queries recover the correct sum within the supported query bound.
The quantization scale is derived from the world-space query cap, the current
camera's shadow matrices and atlas dimensions. All moment producers and Resolve
use the same scale. Larger query support reduces moment precision rather than
silently overflowing local rectangle sums. Very large ranges can therefore
increase reconstruction noise or light bleeding.

### Shadow Resolve

A first SAT query estimates average blocker depth. For the directional light's
orthographic projection, the receiver–blocker depth difference is converted to a
world-space separation and then to a filter radius in atlas texels.

The renderer feature exposes **Light Angular Diameter** (full angle in degrees)
and **Max Query Radius World** (world units; meters at unit scale). The default
diameter of 5.72481 degrees preserves the previous half-angle tangent of 0.05.
The maximum-radius slider spans 0.01-1 world units, defaults to 0.5 and bounds both blocker search
and final filtering. Radius is capped in world space before conversion through
each cascade's matrix; there is no fixed 7.5-texel cap. This does not add cascade
blending or remove the existing texel-based PCF/MSM transition below.
The slider range suits the meter-scale examples; it is not a precision guarantee
for every camera, cascade layout or shadow resolution.

The estimated radius, after the world-space cap, selects the visibility path:

- **Up to 1 texel:** bilinear PCF; skip final MSM reconstruction.
- **Between 1 and 2 texels:** smooth transition from PCF to MSM.
- **2 texels and above:** MSM using a second SAT query.

These thresholds refer to radius, not full penumbra width. Both SAT queries and
the PCF samples stay within the selected cascade slice. Fractional SAT queries
weight partial texel coverage without interpolating wrapped integer prefixes.

## Requirements

Development environment:

- Unity **6000.6.7f1**
- Universal Render Pipeline **17.6.0**
- macOS / Metal
- Forward or Forward+ rendering with compute-shader support

Other Unity versions, graphics APIs, and player-build configurations have not
been fully validated.

## Setup

This repository contains rendering source and example assets, not a complete
Unity project or an installable UPM package.

1. Create or open a compatible URP project.
2. Copy the tracked files under `Assets/MSM`, including their `.meta` files,
   into the project. Preserve the supplied GUIDs and avoid duplicate imports.
3. Add **MsmRendererFeature** to the active Universal Renderer Data asset.
4. Assign **ShadowMoments.compute** and select **Full MSM**.
5. Use a realtime main directional light and opaque URP Lit materials. Start
   with one shadow cascade for a baseline comparison.
6. Optionally import `Assets/MSMExamples` and its metadata.

Camera depth is requested by the feature. Main-light shadow keywords are
restored before transparent rendering; transparent receivers retain URP's
standard shadow path.

## Example Scenes

| Scene | Demonstrates |
| --- | --- |
| [ContactHardening](Assets/MSMExamples/Scenes/ContactHardening.unity) | A vertically animated sphere and grounded reference cube for inspecting the relationship between separation and shadow softness. |
| [LayeredBlockers](Assets/MSMExamples/Scenes/LayeredBlockers.unity) | Overlapping shadows from blockers at different heights, with isolated references for examining occluder-fusion artifacts. |
| [WidePenumbraBenchmark](Assets/MSMExamples/Scenes/WidePenumbraBenchmark.unity) | A screen-filling receiver beneath a slatted canopy for measuring SAT construction versus per-pixel soft-shadow filtering across output resolutions. |

The example cameras use the active pipeline's default renderer. Opening a scene
does not change pipeline or Quality settings.

In Play mode, disable **Animate** on the sphere's **Demo Sphere Motion**
component and adjust **Height** for a fixed-pose comparison. See the
[example guide](Assets/MSMExamples/README.md) for scene controls.

## Current Progress

Implemented:

- Directional-light MSM and integer tiled SAT construction.
- Fractional-coverage filtering and average blocker-depth estimation.
- Contact-hardening radius estimation.
- Cascade selection and atlas-slice isolation.
- Near-contact PCF fallback and smooth PCF/MSM blending.
- Example scenes.

Current focus:

- Evaluate contact quality and temporal stability of the hybrid visibility path.
- Separate source shadow-map aliasing from moment-reconstruction artifacts.
- Balance numerical bias against near-contact light leaking.

Not implemented:

- Cascade-transition blending.
- Point-light and spot-light MSM.
- Spatial denoising or temporal accumulation.

## Limitations

- Moment reconstruction can leak light, particularly near contact and with
  complex depth distributions. Averaging multiple blockers into one depth can
  produce incorrect softness for layered occluders.
- Filter and search radii are capped by the integer precision budget. The search
  region is fixed in texel space, and large penumbras are truncated.
- The near-contact fallback reduces reliance on MSM but cannot eliminate
  aliasing already present in the source shadow map.
- Cascade transitions are not blended and may remain visible.
- Final moment reconstruction is sensitive to degenerate inputs and numerical
  precision. Current bias values are experimental.
- XR, multi-camera rendering, mixed/baked-lighting parity, build-time shader
  stripping, and cross-platform behavior require further validation.

## Performance Analysis

The Renderer Feature includes diagnostic modes:

| Mode | Work performed |
| --- | --- |
| Full MSM | Tiled SAT construction and hybrid PCF/MSM resolve. |
| A Constant | Fixed visibility of 0.5. |
| B Moments | Standalone moment generation and fixed visibility of 0.5. |
| C Moments And SAT | Tiled SAT construction and fixed visibility of 0.5. |

B and C use different producer paths, so their whole-frame difference is not an
isolated SAT construction cost. Compare per-dispatch GPU timings under identical
scene, resolution, cascade, and lighting settings. Performance results are
hardware- and configuration-dependent.

## Repository Layout

```text
Assets/
  MSM/                 Renderer Feature and compute shader
  MSMExamples/         Demonstration scenes, materials, and animation
```

Unity metadata is included. Generated files, local pipeline settings, third-party
comparison plugins, and GPU captures are excluded from version control.

## References

- Peters et al., [Moment Shadow Mapping](https://momentsingraphics.de/I3D2015.html), 2015.
- Peters et al., [Improved Moment Shadow Maps](https://jcgt.org/published/0006/01/03/), 2017.
- Nehab et al., [GPU-efficient Recursive Filtering and Summed-Area Tables](https://hhoppe.com/proj/recursive/), 2011.

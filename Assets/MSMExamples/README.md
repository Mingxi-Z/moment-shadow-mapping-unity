# MSM examples

Requires Unity 6000.6 / URP 17.6 and the algorithm assets under Assets/MSM.
These scenes use the active pipeline's default renderer. They do not install or
change a render pipeline, Quality settings, shadow resolution or shader constants.

## Before opening

On your active URP Renderer Data, enable MsmRendererFeature, assign
ShadowMoments.compute and select Full MSM. Start with one shadow cascade to keep
unblended cascade transitions out of the basic demonstration. This is a manual
pipeline choice; opening a scene does not change it.

If you see uniform half-lighting instead of object shadows, check Benchmark Mode:
A/B/C are cost-isolation modes, not the final shadow effect. Select Full MSM.

Import Assets/MSMExamples and Assets/MSMExamples.meta together, preserving metadata.
All example materials are URP Lit; meshes are Unity primitives. No ShadowLab assets,
external models, baked lighting or additional packages are required.

## ContactHardening

Open Scenes/ContactHardening.unity and use the Game view. The fixed camera frames
an orange grounded reference cube and a blue sphere. Press Play to move the sphere
smoothly between center heights 1.05 m and 3 m over a six-second cycle. Its radius
is 1 m, so the closest receiver clearance is 0.05 m.

Select Animated Sphere and find Demo Sphere Motion in the Inspector:

- Clear Animate to hold the current height.
- Adjust Height from 0 to 1 while paused for a manual comparison.
- Re-enable Animate to resume from that height. A manual adjustment resumes on
  the rising half of the cycle.
- Keep the camera and light fixed when recording. Motion runs only in Play mode.

Compare the sphere's shadow at low and high positions. The cube provides a fixed
contact reference. The world-space query-radius cap may limit visible widening;
this scene does not raise it or alter moment/depth bias.

## LayeredBlockers

Open Scenes/LayeredBlockers.unity. Orange is the low (1 m) blocker and green is the
high (3 m) blocker. The central pair has overlapping ground-projected footprints;
the separated Low Reference and High Reference show each height on its own.

Inspect shadow overlap, edge continuity and unwanted light inside shadowed areas.
This is a stress scene, not a matched VSM/PCSS comparison or a guarantee of visible
light bleeding. Moving a blocker can change which depth layers a query contains.
There is no animation in this scene.

## WidePenumbraBenchmark

Open Scenes/WidePenumbraBenchmark.unity and use the Game view. A fixed top-down
orthographic camera sits below 13 parallel canopy slats, so the floor fills the
image while the off-camera slats cast repeated shadows onto it. Slats share a
3 m center height, 0.45 m width and 0.9 m pitch; the camera height is 1.5 m.
There is no animation or automatic benchmark configuration.

This synthetic workload targets high receiver-pixel count and frequent soft
edges. Increasing output resolution increases the number of shaded pixels
without increasing SAT dimensions. It can therefore amortize SAT construction
better than a small object surrounded by background. It does not guarantee MSM
is faster. Fixed-sample PCSS does not necessarily get slower with filter radius.

### Manual comparison

1. Use one cascade, a 1024 main-light shadow atlas and Game Scale 1x. Repeat later
   at 2048. Keep camera, light, render scale and shadow distance identical across
   paths; record their values. Source-shadow resolution is not output resolution.
2. Enable only one custom shadow feature at a time: Full MSM or the PCSS feature.
   Keep PCSS's normal optimizations enabled for the primary comparison. Record
   its sample count, hard/soft threshold, denoise and sampling-stabilization
   settings. Measure 16, 32 and 64 samples as separate quality levels.
   The current local PCSS mask skips fully lit pixels, not deep-umbra pixels.
3. Match the visible softness before comparing speed. In the current local
   PCSS-URP implementation, raw world radius is
   `2 * gap * tan(sunAngularDiameter / 2) * penumbraScale` when `farSoftness = 0`.
   With MSM's default angular diameter of 5.72481 degrees (slope 0.05), `penumbraScale = 1` and
   `sunAngularDiameter = 0.0499896` radians match that radius coefficient.
   This matches the code's radius convention, not a universal light-size setting.
   MSM's Max Query Radius World bounds both search and filtering (default 0.5 m).
   Convert it with the active shadow matrix and atlas dimensions to match PCSS's
   blocker-search radius in texels; do not reuse an old fixed 7.5-texel setting.
   Use a PCSS hard/soft threshold of 2 texels for the initial comparison.
   PCSS uses a disk and MSM a box,
   so matching radius does not make the filter shapes identical.
4. Inspect actual radius/processing coverage. Confirm much of the floor runs the
   MSM path rather than the near-contact PCF fallback, and that PCSS's mask is
   doing meaningful penumbra work. Do not disable its mask just to produce a win.
   MSM caps radius in world units. If that cap is reached, match the
   PCSS maximum radius too (`worldRadius * length(worldToShadow row 0)` in this
   plugin's atlas-UV setting), or
   report the mismatch rather than presenting sharper MSM as equivalent quality.
5. At a fixed atlas size, run 1920x1080, 2560x1440 and 3840x2160. Warm up after
   each change and record repeated GPU measurements, including MSM/PCSS/MSM
   repeat runs. Do not compare a capture replay with ordinary Play-mode timings.

Record total frame GPU time and the components separately:

| Path | GPU work to include |
| --- | --- |
| MSM | All four SAT stages plus Resolve, including the PCF/MSM hybrid. |
| PCSS | Reconnaissance, mask processing, main filtering and enabled denoising. |

Save matching images as well as timings. Lower PCSS sample counts may be faster
but noisier; stronger MSM bias may be more stable but leak more light. Such runs
are quality/performance trade-offs, not unconditional wins. No static-shadow
cache is implemented: SAT construction remains part of every measured frame.

The scene references only the existing example materials and Unity primitives.
Native shadow-caster culling of the off-camera canopy, actual penumbra coverage
and performance must be checked in Unity. If the floor has no stripes, first
inspect the main-light shadow atlas and confirm the active renderer/feature.

## Recording and limitations

Use the same Game-view resolution, camera, light and pipeline settings for every
comparison. Toggling MSM requires changing the renderer feature manually; no
comparison switcher is included. Do not interpret this scene as proof that MSM
outperforms URP's default PCF.

Verify scene import, shader compilation and the rendered result in the Unity
Editor. Use GPU captures to inspect numerical behavior and rendering cost.

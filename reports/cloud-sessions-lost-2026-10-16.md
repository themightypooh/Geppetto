# Cloud sessions archived 2026-10-16: recovered

Every work session's transcript was pulled from claude.ai (~/Downloads/geppetto-cloud-transcripts.json)
and its file-writing commands were replayed onto Geppetto-cloud-work main (3fd6ebd) in ~/recovery-work.
Each branch below was built and tested locally (Effigy.Tests). No cloud session was run.

Baseline on 3fd6ebd: 5939 passed, 1 failed (PaddingFeature needs a rig, known).

**Base warning:** these branches sit on Geppetto-cloud-work main, which includes the private
`cloud-context/` commit (8ef0d8c). Cherry-pick the feature commits onto public main; do not
merge the branches as-is.

Skipped on purpose (user stopped them): body-horror sessions (tendrils, corruption, blob body shapes).
Already safe before recovery: mesh reports (on main), helmet kit (podracer), seated poses and
viewport preview (pushed to Geppetto-cloud-work).

| Branch (local, `recovered/…`) | Commits | Tests |
|---|---|---|
| effigy-bake-selected-to-active-ui-bake-maps-cage | 3 | 5990 passed, 1 failed |
| effigy-brush-calibration-broken-brushes-filters (WIP, never committed by the session) | 1 | 5940 passed, 1 failed |
| effigy-cloth-live-cloth-layering-per-particle-maps-wind | 2 | 5939 passed, 1 failed |
| effigy-deformers-radial-symmetry-sculpt-masks-shell-options | 3 | 6004 passed, 1 failed |
| effigy-editor-tooltip-label-cleanup | 2 | 5939 passed, 1 failed |
| effigy-export-skinned-lods-fbx-blendshapes-wrinkle-maps | 4 | 6013 passed, 1 failed |
| effigy-fbx-import | 4 | 5987 passed, 1 failed |
| effigy-flesh-to-skin-shader-output-sss-material-stacks | 5 | 5985 passed, 1 failed |
| effigy-garments-hems-cuffs-rolls-gloves | 1 | 5938 passed, 2 failed |
| effigy-garments-jacket-vest-belt-boot-recipes-open-fronts-ho (WIP, never committed by the session) | 1 | 5936 passed, 4 failed |
| effigy-garments-layering-fixes-clip-checks-padding-completio (WIP, never committed by the session) | 1 | 5998 passed, 1 failed |
| effigy-garments-merge-worktree-backups-pockets-hardware-wear (WIP, never committed by the session) | 1 | 6156 passed, 1 failed |
| effigy-garments-necklines-collars | 1 | 5939 passed, 1 failed |
| effigy-gear-knots-cords-goggles-watches | 1 | 5940 passed, 1 failed |
| effigy-kernel-sparse-solver-heat-weights-arap | 2 | 5999 passed, 1 failed |
| effigy-one-click-fixes-in-diagnostics-shape-key-resculpt | 4 | 6003 passed, 1 failed |
| effigy-paint-channels-composited-paint-layers | 4 | 6023 passed, 1 failed |
| effigy-profile-upgrades-spline-radii-more-primitives | 1 | 5942 passed, 1 failed |
| effigy-rigging-from-script-bones-rig-templates-skin-feature (WIP, never committed by the session) | 1 | 5957 passed, 5 failed (after tuple fix; new features not yet mirrored) |
| effigy-set-origin-render-turnaround-symmetry-badge | 3 | 6003 passed, 1 failed |
| effigy-skeleton-to-quad-cage-isotropic-remesh-tree-rock-feat | 1 | 5998 passed, 1 failed |
| effigy-uv-toolset-seams-projections-pack-pins-uv2 | 6 | 5945 passed, 1 failed |
| marionette-correctives-drivers-pose-space-morphs | 3 | 5952 passed, 2 failed |
| marionette-root-motion-look-at-copy-constraints-multi-point | 1 | 5975 passed, 1 failed |
| marionette-shape-keys-end-to-end-key-warning-test | 4 | 5981 passed, 1 failed |

Extra failures beyond the Padding one: garment recipes and rigging-from-script need
`tools/sync-kernel.sh` (they stopped before mirroring); knee pad on both legs (layering fixes);
a 10 s timing check on a 50k-face mesh (hems/cuffs, 10.5 s here); corrective shape key needs a rig
in its defaults test (correctives). Rigging-from-script needed a one-word fix (tuple element
`Rest` renamed `Remainder`) to compile.

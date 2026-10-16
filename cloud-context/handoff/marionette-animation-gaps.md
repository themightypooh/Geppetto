# Marionette animation gaps — cloud session handoff (2026-10-16)

Branch: `claude/marionette-animation-gaps` (local commits; NOT pushed — owner approval pending,
and relayed instructions disagreed on branch vs main). Task list from marionette.md §6.

## Done: 1. Root motion extraction
- Kernel `Effigy/Rig/RootMotion.cs`: `Plan`/`Extract` (XY, XY+Yaw, Full; Even Speed = linear),
  `MotionOf`, `InPlace`, `CycleOf` (loop wrap travel), `SlideReport` (foot slide with/without).
  Two layouts: hips under the root (poses unchanged) and Citizen's (pelvis and root_IK both top
  bones: root_IK carries travel, pelvis keyed in place).
- `.riganim` `RootMotion` object read/written (`RigAnimFile.RootMotion`, `RigAnimWriter`);
  `RigAnimFile.PlayInPlace` samples in place.
- Export: `VmdlAnimation.ExtractMotion` — KV3 copied from Facepunch sbox-public
  `citizen_animationlist.vmdl_prefab` (Roll_N): children [ExtractMotion extract_tx/ty/tz/rz,
  linear, quadratic, root_bone_name, motion_type "Single"]. Wired into Marionette Export and
  Effigy Add Clip (`ClipEntry.RootMotion`).
- CLI: `sim ... rootmotion=root:hips[:xy|xyyaw|full][:exact]` prints extraction + per-foot slide,
  then lints in place.
- Marionette: `RigRootMotion` (doc property `RootMotion`), `RigRootMotionMath.MotionOf`,
  player `Root Motion` = Off / Move GameObject / Report Only (`RootMotionDelta`), loop-safe wrap
  (`RigAnimPlayerComponent.RootMotion.cs`; class made `partial`), timeline bone row ▸ Extract
  Root Motion (`RigRootMotionTool`). CHANGELOG + usability-audit rows added.
- Status: kernel implemented + tested (35/35 in `MarionetteGapTests`, run via a scratch runner;
  full suite NOT re-run). Engine/editor code: parses, engine-less gate shows only missing-engine
  names, editor-usings lint clean. AWAITING LOCAL s&box CHECK (compile, player move, export
  compile with ExtractMotion, the menu).

## Not started (resume here, in order)
2. Damped Track / Look At / Copy Rotation / Copy Location constraints. Plan: one
   `TransformConstraint {Kind, Bone, Target (bone or clip object), Weight, Axis (Along/±X/±Y/±Z),
   UpAxis, Space Model/Local, Mix Replace/Add, axis toggles}` list on RigDocument; kernel twin in a
   new Effigy/Rig file read by `RigAnimFile.UseRig`, applied after IK in `SampleLocal`; engine solver
   in a new Code/RigControl file; player after ApplyIk; viewport `EvaluatePose`; export
   `EffigyAnimExport.ToClip` (it samples the doc itself — add the pass there); keyed weight via a
   new appended TrackTarget/RigTrackKind `ConstraintWeight` named for the constraint; UI rows in
   RigConstraintsPanel.
3. Multi-point spline IK: `IkConstraint.SplineControls` (chain bones); each control's goal = its
   IkGoal track if keyed (any goal space, e.g. wrap a pipe) else its FK position; centripetal
   Catmull-Rom through root/controls/goal, joints laid per control segment by bone length, pinned
   sub-FABRIK to restore lengths. Twin in ChainIk + RigConstraintSolver.
4. F-curve modifiers: pure maths in `Code/RigControl/RigCurve.cs` (noise, cycles/cycles with
   offset), applied in BoneTrack.Evaluate and kernel RigTrack.Evaluate, JSON `Modifiers` per
   track, writer round trip, timeline row ▸ Modifiers. Per-channel keys: document as next step.
5. Editor viewport live Soft Chains: run `RigSoftPass` (Code/RigControl/RigSoftChain.cs) in
   RigViewport during playback.

Scratch tools used: an engine-less compile gate (Code/RigControl + Editor/RigControlEditor +
kernel, filter out missing-engine names) and a small runner compiling only MarionetteGapTests.

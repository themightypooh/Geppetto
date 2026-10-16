"""
Blender as a reference oracle for Effigy's sculpt tools.

    blender --factory-startup --window-geometry 0 0 640 480 --python tools/blender_oracle.py -- job.json

Sculpt operators need a real 3D viewport, so this runs Blender with a small window (not -b), does
the job from a timer once the UI exists, writes the result and quits.

job.json:
{
  "mesh": "in.obj",              # OBJ, Effigy axes (x forward, y left, z up), inches
  "out": "out.obj",              # result written here, same vertex order
  "brush": "Clay Strips",        # an Essentials sculpt brush name
  "radius": 1.5,                 # world radius (inches)
  "strength": 0.5,
  "strokes": [[[x,y,z], ...]],   # each stroke a list of world points on the surface
  "invert": false,               # Ctrl-stroke (subtract)
  "settings": {"spacing": 10}    # optional extra brush attributes, set by name
}

Behaviour only: nothing here copies Blender's code into Effigy. The oracle produces reference
meshes; Effigy's own implementations are tested against them (Effigy.Tests --parity).
"""
import bpy, sys, json, os, traceback

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
RAW = json.load(open(argv[0]))
# One job, or {"jobs": [...]} to do many in a single Blender launch.
JOBS = RAW["jobs"] if "jobs" in RAW else [RAW]
JOB = JOBS[0]
LIB = os.path.join(os.path.dirname(bpy.app.binary_path), f"{bpy.app.version[0]}.{bpy.app.version[1]}",
                   "datafiles", "assets", "brushes", "essentials_brushes-mesh_sculpt.blend")


def log(msg):
    with open(CURRENT["out"] + ".log", "a") as f:
        f.write(str(msg) + "\n")


CURRENT = JOB


def run():
    global CURRENT
    for job in JOBS:
        CURRENT = job
        run_one(job)
    bpy.ops.wm.quit_blender()
    return None


def run_one(JOB):
    # First line of every log: which Blender measured it (cached with the reference).
    log(f"blender {bpy.app.version_string}")
    try:
        win = bpy.context.window_manager.windows[0]
        area = next(a for a in win.screen.areas if a.type == 'VIEW_3D')
        region = next(r for r in area.regions if r.type == 'WINDOW')
        space = area.spaces.active
        with bpy.context.temp_override(window=win, area=area, region=region):
            for o in list(bpy.data.objects):
                bpy.data.objects.remove(o)
            bpy.ops.wm.obj_import(filepath=JOB["mesh"], forward_axis='Y', up_axis='Z')
            ob = bpy.context.selected_objects[0]
            bpy.context.view_layer.objects.active = ob
            bpy.ops.object.mode_set(mode='SCULPT')
            # The brush, from the Essentials library, by name.
            bpy.ops.brush.asset_activate(asset_library_type='ESSENTIALS',
                                         relative_asset_identifier="brushes/essentials_brushes-mesh_sculpt.blend/Brush/" + JOB["brush"])
            ts = bpy.context.tool_settings.sculpt
            brush = ts.brush
            # Blender 5 moved the unified settings onto the paint mode.
            ups = getattr(ts, 'unified_paint_settings', None) or getattr(bpy.context.tool_settings, 'unified_paint_settings', None)
            # Radius in world units, not pixels.
            for owner in [o for o in (brush, ups) if o is not None]:
                try:
                    owner.use_locked_size = 'SCENE'
                except Exception:
                    pass
                try:
                    owner.unprojected_size = JOB["radius"] * 2.0
                except Exception:
                    try:
                        owner.unprojected_radius = JOB["radius"]
                    except Exception:
                        pass
            brush.strength = JOB.get("strength", 0.5)
            # Measure the brush, not the stroke filtering: no stabiliser (lazy mouse), no jitter.
            for k, v in (("use_smooth_stroke", False), ("jitter", 0.0), ("use_pressure_strength", False)):
                try:
                    setattr(brush, k, v)
                except Exception:
                    pass
            if ups is not None:
                for k in ('use_unified_strength', 'use_unified_size'):
                    try:
                        setattr(ups, k, False)
                    except Exception:
                        pass
            # Auto-masking toggles and values, on the sculpt tool settings.
            am = bpy.context.tool_settings.sculpt.mesh_automasking_settings
            # Jobs share one Blender: start each with every toggle off.
            for p in am.bl_rna.properties:
                if p.identifier.startswith("use_automasking"):
                    setattr(am, p.identifier, False)
            for k, v in JOB.get("automask", {}).items():
                try:
                    setattr(am, k, v)
                except Exception as e:
                    log(f"automask {k}: {e}")
            # A brush texture (alpha): {"image": path, "map_mode": "AREA_PLANE"}.
            tex = JOB.get("texture")
            if tex:
                t = bpy.data.textures.new("alpha", 'IMAGE')
                t.image = bpy.data.images.load(tex["image"], check_existing=True)
                # Essentials brushes live in Blender's separate asset data and silently drop a
                # pointer to a texture made here. So: a fresh brush in this file of the same type,
                # marked as a local asset and activated, carries the texture.
                kind = brush.sculpt_brush_type
                nb = bpy.data.brushes.new("OracleAlpha", mode='SCULPT')
                nb.sculpt_brush_type = kind
                nb.asset_mark()
                bpy.ops.brush.asset_activate(asset_library_type='LOCAL', relative_asset_identifier="Brush/OracleAlpha")
                brush = ts.brush
                for owner in [o for o in (brush, ups) if o is not None]:
                    try:
                        owner.use_locked_size = 'SCENE'
                        owner.unprojected_size = JOB["radius"] * 2.0
                    except Exception:
                        pass
                brush.strength = JOB.get("strength", 0.5)
                brush.texture = t
                brush.texture_slot.map_mode = tex.get("map_mode", "AREA_PLANE")
                log(f"texture: brush.texture={brush.texture} image={t.image} size={tuple(t.image.size) if t.image else None} map={brush.texture_slot.map_mode} slot_tex={getattr(brush.texture_slot,'texture',None)}")
            else:
                try:
                    brush.texture = None
                except Exception:
                    pass
            for k, v in JOB.get("settings", {}).items():
                try:
                    setattr(brush, k, v)
                except Exception as e:
                    log(f"setting {k}: {e}")
            # Look straight at the stroke so the 2D path maps back onto it.
            from bpy_extras.view3d_utils import location_3d_to_region_2d
            rv3d = space.region_3d
            # A top-down orthographic view centred on the mesh, set directly and recomputed in place
            # (a redraw inside the override loses the sculpt context).
            from mathutils import Quaternion, Vector
            lo = Vector((min(v.co.x for v in ob.data.vertices), min(v.co.y for v in ob.data.vertices), min(v.co.z for v in ob.data.vertices)))
            hi = Vector((max(v.co.x for v in ob.data.vertices), max(v.co.y for v in ob.data.vertices), max(v.co.z for v in ob.data.vertices)))
            rv3d.view_perspective = 'ORTHO'
            rv3d.view_rotation = Quaternion((1, 0, 0, 0))
            rv3d.view_location = (lo + hi) * 0.5
            rv3d.view_distance = max((hi - lo).length, 1.0) * 1.2
            rv3d.update()
            log("radius props: " + ", ".join(f"{k}={getattr(o, k)}" for o in [brush] + ([ups] if ups else []) for k in ('use_locked_size', 'unprojected_size', 'unprojected_radius', 'size') if hasattr(o, k)))
            # A mesh filter instead of a stroke: {"type": "INFLATE", "strength": 0.5, "iterations": 1}.
            if JOB.get("filter"):
                f = JOB["filter"]
                bpy.ops.sculpt.mesh_filter(type=f["type"], strength=f.get("strength", 1.0), iteration_count=f.get("iterations", 1))
            for stroke in JOB.get("strokes", []):
                items = []
                for i, p in enumerate(stroke):
                    m = location_3d_to_region_2d(region, rv3d, p)
                    if m is None:
                        continue
                    items.append({"name": "s", "location": tuple(p), "mouse": (m.x, m.y), "mouse_event": (m.x, m.y),
                                  "is_start": i == 0, "pressure": 1.0, "size": 50, "time": i * 0.02,
                                  "x_tilt": 0, "y_tilt": 0})
                bpy.ops.sculpt.brush_stroke(stroke=items, mode='INVERT' if JOB.get("invert") else 'NORMAL')
            bpy.ops.object.mode_set(mode='OBJECT')
            with open(JOB["out"], "w") as f:
                for v in ob.data.vertices:
                    co = ob.matrix_world @ v.co
                    f.write(f"v {co.x:.6f} {co.y:.6f} {co.z:.6f}\n")
            log("ok " + brush.name)
        bpy.context.view_layer.objects.active = None
    except Exception:
        log(traceback.format_exc())
        try:
            bpy.ops.object.mode_set(mode='OBJECT')
        except Exception:
            pass


bpy.app.timers.register(run, first_interval=0.5)

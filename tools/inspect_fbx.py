import bpy
from mathutils import Vector

import sys
path = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else r"C:\Users\pooh\Documents\s&box projects\marionette-main\Assets\models\lightswitch\lightswitch_plate.fbx"
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path)
u = bpy.context.scene.unit_settings
print("UNIT", u.system, u.scale_length)
for o in bpy.data.objects:
    if o.type != "MESH":
        continue
    pts = [o.matrix_world @ v.co for v in o.data.vertices]
    xs = [p.x for p in pts]
    ys = [p.y for p in pts]
    zs = [p.z for p in pts]
    print(
        f"{o.name}: verts={len(o.data.vertices)} "
        f"dims=({max(xs)-min(xs):.4f}, {max(ys)-min(ys):.4f}, {max(zs)-min(zs):.4f}) "
        f"loc={tuple(round(x, 4) for x in o.location)} "
        f"scale={tuple(round(x, 4) for x in o.scale)}"
    )
    mats = [s.material.name if s.material else None for s in o.material_slots]
    print("  mats", mats)
    print("  min", (min(xs), min(ys), min(zs)), "max", (max(xs), max(ys), max(zs)))

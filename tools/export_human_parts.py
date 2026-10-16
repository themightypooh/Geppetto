# Export CC0 Human Base Meshes parts to OBJ in Effigy space (x forward, y left, z up, inches).
import bpy, os, re, sys
out = os.environ.get("EFFIGY_PARTS_OUT", "/tmp/claude-1000/hbm/parts")
IN = 39.3700787
want = {
    "GEO-head_sculpting_realistic": None, "GEO-head_animation_realistic": None,
    "Hand  - Realistic": None, "Foot - Realistic": None, "Eye - Realistic": None,
    "Jaw - Realistic": None, "Skull - Realistic": 3,
    "GEO-body_male_realistic": 2, "GEO-body_female_realistic": 2,
}
dg = bpy.context.evaluated_depsgraph_get()
def export(o, levels=None):
    for m in o.modifiers:
        if m.type == 'MULTIRES':
            lv = m.total_levels if levels is None else min(levels, m.total_levels)
            m.levels = lv; m.render_levels = lv; m.sculpt_levels = lv
        if m.type == 'SUBSURF':
            m.levels = 1
    dg.update()
    ev = o.evaluated_get(dg)
    me = ev.to_mesh()
    mw = o.matrix_world
    name = re.sub(r'[^a-z0-9]+', '_', o.name.lower()).strip('_')
    path = os.path.join(out, name + ".obj")
    with open(path, "w") as f:
        f.write(f"o {name}\n")
        for v in me.vertices:
            w = mw @ v.co
            f.write(f"v {-w.y*IN:.5f} {w.x*IN:.5f} {w.z*IN:.5f}\n")
        for p in me.polygons:
            f.write("f " + " ".join(str(i+1) for i in p.vertices) + "\n")
    # Face sets are how the bundle's artists split the model into regions (mouth bag, lips/jaw,
    # eye sockets, nose, ears, neck): one id per face, in OBJ face order, next to the OBJ.
    fs = me.attributes.get(".sculpt_face_set")
    if fs is not None:
        with open(os.path.join(out, name + ".facesets"), "w") as f:
            f.write("\n".join(str(fs.data[p.index].value) for p in me.polygons) + "\n")
    print("EXPORT", name, len(me.vertices), len(me.polygons))
    ev.to_mesh_clear()
for o in bpy.data.objects:
    if o.type != 'MESH': continue
    if o.name in want:
        export(o, want[o.name])
    elif o.name.startswith("GEO-skeletion."):
        export(o)

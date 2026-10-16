"""
SUPERSEDED by `tools/effigy.sh shade model.obj outdir [same options]` (Effigy.Tests/ShadeCommand.cs),
which needs no Blender. Kept as the reference the Effigy look was matched against.

Render a creature OBJ (with vertex colours) for judging, headless in Blender.

    blender -b --python tools/render_creature.py -- model.obj outdir [size=1024] [views=front,side,back,quarter,top,close] [turn=8]

Writes outdir/<name>_<view>.png: a shaded pass per view and a black silhouette per view, plus
<name>_sheet.png (all views on one image) via ImageMagick when available.

s&box space is X forward, Y left, Z up, inches. The OBJ is in that space; this script treats
Blender's Z as up and -Y as "the camera's front", so the creature's +X (its front) faces the
front camera.
"""
import bpy, sys, math, os, subprocess

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
obj_path = argv[0]
out_dir = argv[1]
opts = dict(a.split("=", 1) for a in argv[2:] if "=" in a)
size = int(opts.get("size", "1024"))
views = opts.get("views", "front,quarter,side,back,top,close").split(",")
name = os.path.splitext(os.path.basename(obj_path))[0]
os.makedirs(out_dir, exist_ok=True)

bpy.ops.wm.read_factory_settings(use_empty=True)
sc = bpy.context.scene
sc.render.engine = 'BLENDER_EEVEE'
sc.render.resolution_x = size
sc.render.resolution_y = size
sc.render.film_transparent = False
try:
    sc.eevee.taa_render_samples = int(opts.get("samples", "32"))
    sc.eevee.use_gtao = True
    sc.eevee.use_shadows = True
except Exception:
    pass
sc.view_settings.view_transform = 'Filmic' if 'Filmic' in [i.identifier for i in sc.view_settings.bl_rna.properties['view_transform'].enum_items] else 'AgX'

# Import. Blender's importer maps OBJ Y-up by default; our file is Z-up, so forward=-Y? No:
# s&box X forward, Y left, Z up. Import with up=Z and forward=X.
bpy.ops.wm.obj_import(filepath=obj_path, forward_axis='Y', up_axis='Z')  # identity: file axes are Blender axes
objs = [o for o in bpy.context.scene.objects if o.type == 'MESH']
if not objs:
    raise SystemExit("no mesh imported")
ob = objs[0]
for o in objs[1:]:
    o.select_set(True)
ob.select_set(True)
bpy.context.view_layer.objects.active = ob
if len(objs) > 1:
    bpy.ops.object.join()
ob = bpy.context.view_layer.objects.active

# Smooth shading.
mesh = ob.data
for p in mesh.polygons:
    p.use_smooth = True

# Bounds.
xs = [v.co.x for v in mesh.vertices]; ys = [v.co.y for v in mesh.vertices]; zs = [v.co.z for v in mesh.vertices]
mn = (min(xs), min(ys), min(zs)); mx = (max(xs), max(ys), max(zs))
center = ((mn[0]+mx[0])/2, (mn[1]+mx[1])/2, (mn[2]+mx[2])/2)
extent = max(mx[0]-mn[0], mx[1]-mn[1], mx[2]-mn[2])
height = mx[2]-mn[2]
print("bounds", mn, mx, "extent", extent)

# Material: vertex colour as albedo, wet flesh look.
mat = bpy.data.materials.new("flesh")
mat.use_nodes = True
nt = mat.node_tree
bsdf = nt.nodes.get("Principled BSDF")
attr = nt.nodes.new("ShaderNodeVertexColor")
col_name = None
for ca in mesh.color_attributes:
    col_name = ca.name
if col_name:
    attr.layer_name = col_name
nt.links.new(attr.outputs["Color"], bsdf.inputs["Base Color"])
bsdf.inputs["Roughness"].default_value = 0.38
try:
    bsdf.inputs["Subsurface Weight"].default_value = 0.4
    bsdf.inputs["Subsurface Radius"].default_value = (1.0, 0.3, 0.15)
    bsdf.inputs["Subsurface Scale"].default_value = 0.06  # scene units are inches: skin scatters ~1-2 mm
except Exception:
    pass
try:
    bsdf.inputs["Coat Weight"].default_value = 0.25
    bsdf.inputs["Coat Roughness"].default_value = 0.2
except Exception:
    pass
# Clay: one matte grey, so only the form is judged.
if opts.get("clay", "0") == "1":
    clay = bpy.data.materials.new("clay"); clay.use_nodes = True
    cb = clay.node_tree.nodes["Principled BSDF"]
    cb.inputs["Base Color"].default_value = (0.62, 0.6, 0.57, 1)
    cb.inputs["Roughness"].default_value = 0.65
    try:
        cb.inputs["Subsurface Weight"].default_value = 0.0
        cb.inputs["Coat Weight"].default_value = 0.0
    except Exception:
        pass
    mat = clay
ob.data.materials.clear()
ob.data.materials.append(mat)

# Ground.
bpy.ops.mesh.primitive_plane_add(size=extent * 12, location=(center[0], center[1], mn[2]))
ground = bpy.context.active_object
gm = bpy.data.materials.new("ground"); gm.use_nodes = True
gm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.12, 0.12, 0.125, 1)
gm.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 0.9
ground.data.materials.append(gm)
# ground=0: no floor, for cameras below the model (e.g. looking up into a drooping face).
if opts.get("ground", "1") == "0":
    ground.hide_render = True

# World.
w = bpy.data.worlds.new("w"); sc.world = w; w.use_nodes = True
bg = w.node_tree.nodes.get("Background") or w.node_tree.nodes.new("ShaderNodeBackground")
if not bg.outputs[0].links:
    out = w.node_tree.nodes.get("World Output") or w.node_tree.nodes.new("ShaderNodeOutputWorld")
    w.node_tree.links.new(bg.outputs[0], out.inputs[0])
print("world nodes", [n.name for n in w.node_tree.nodes]); bg.inputs[0].default_value = (0.08, 0.085, 0.1, 1); bg.inputs[1].default_value = 1.0

# Lights: key, fill, rim.
def sun(name, rot_deg, energy, color=(1,1,1), angle=8):
    l = bpy.data.lights.new(name, 'SUN'); l.energy = energy; l.color = color; l.angle = math.radians(angle)
    lo = bpy.data.objects.new(name, l); sc.collection.objects.link(lo)
    lo.rotation_euler = tuple(math.radians(a) for a in rot_deg)
    return lo

# Sun lights: distance-independent, so the scale of the creature does not matter.
sun("key", (50, 15, 35), 3.2, (1.0, 0.93, 0.85), 6)
sun("fill", (65, -10, 150), 1.2, (0.75, 0.85, 1.0), 30)
sun("rim", (35, 0, -120), 3.0, (0.9, 0.95, 1.0), 10)
sun("under", (-60, 0, 20), 0.5, (1.0, 0.45, 0.35), 40)

cam = bpy.data.cameras.new("cam"); cam.lens = 60
co = bpy.data.objects.new("cam", cam); sc.collection.objects.link(co); sc.camera = co

def aim(loc, target):
    import mathutils
    co.location = loc
    d = mathutils.Vector((target[0]-loc[0], target[1]-loc[1], target[2]-loc[2]))
    co.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()

dist = extent * 1.75
look = (center[0], center[1], center[2])
cams = {
    # creature front is +X
    "front":   ((center[0] + dist, center[1], center[2] + extent*0.15), look),
    "quarter": ((center[0] + dist*0.75, center[1] - dist*0.7, center[2] + extent*0.45), look),
    "side":    ((center[0], center[1] - dist, center[2] + extent*0.12), look),
    "back":    ((center[0] - dist, center[1], center[2] + extent*0.15), look),
    "top":     ((center[0] + 0.01, center[1], center[2] + dist), look),
    "close":   ((center[0] + dist*0.45, center[1] - dist*0.35, center[2] + extent*0.35), (center[0], center[1], center[2] + extent*0.2)),
}

sil_mat = bpy.data.materials.new("sil"); sil_mat.use_nodes = True
sil_mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0, 0, 0, 1)
sil_mat.node_tree.nodes["Principled BSDF"].inputs["Roughness"].default_value = 1.0
try:
    sil_mat.node_tree.nodes["Principled BSDF"].inputs["Emission Color"].default_value = (0,0,0,1)
except Exception:
    pass

if "cam" in opts:
    cp = tuple(float(x) for x in opts["cam"].split(","))
    lp = tuple(float(x) for x in opts.get("look", ",".join(str(c) for c in center)).split(","))
    cams["custom"] = (cp, lp)
    views = ["custom"]

outputs = []
for v in views:
    if v not in cams:
        continue
    aim(*cams[v])
    if v == "close":
        cam.lens = 85
    elif v == "custom":
        cam.lens = float(opts.get("lens", "85"))
    else:
        cam.lens = 60
    sc.render.filepath = os.path.join(out_dir, f"{name}_{v}.png")
    bpy.ops.render.render(write_still=True)
    outputs.append(sc.render.filepath)

# Silhouettes: black creature on white, no ground, flat.
if opts.get("silhouette", "1") == "1":
    ob.data.materials[0] = sil_mat
    ground.hide_render = True
    bg.inputs[0].default_value = (1, 1, 1, 1); bg.inputs[1].default_value = 1.0
    for lo in [o for o in sc.objects if o.type == 'LIGHT']:
        lo.hide_render = True
    sc.view_settings.view_transform = 'Standard'
    for v in ["front", "side", "quarter"]:
        if v in cams:
            aim(*cams[v]); cam.lens = 60
            sc.render.filepath = os.path.join(out_dir, f"{name}_sil_{v}.png")
            bpy.ops.render.render(write_still=True)
            outputs.append(sc.render.filepath)

# Turntable frames, optional.
turn = int(opts.get("turn", "0"))
if turn > 0:
    ob.data.materials[0] = mat
    ground.hide_render = False
    bg.inputs[0].default_value = (0.08, 0.085, 0.1, 1); bg.inputs[1].default_value = 1.0
    for lo in [o for o in sc.objects if o.type == 'LIGHT']:
        lo.hide_render = False
    for i in range(turn):
        a = 2*math.pi*i/turn
        aim((center[0] + dist*math.cos(a), center[1] + dist*math.sin(a), center[2] + extent*0.3), look)
        sc.render.filepath = os.path.join(out_dir, f"{name}_turn{i:02d}.png")
        bpy.ops.render.render(write_still=True)
        outputs.append(sc.render.filepath)

# Sheet.
try:
    sheet = os.path.join(out_dir, f"{name}_sheet.png")
    files = [os.path.join(out_dir, f"{name}_{v}.png") for v in views if v in cams]
    files += [os.path.join(out_dir, f"{name}_sil_{v}.png") for v in ["front", "side", "quarter"]]
    files = [f for f in files if os.path.exists(f)]
    cols = 3
    subprocess.run(["montage"] + files + ["-tile", f"{cols}x", "-geometry", "+4+4", "-background", "#202020", sheet], check=False)
    print("sheet", sheet)
except Exception as ex:
    print("no sheet", ex)
print("DONE", outputs)

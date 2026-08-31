"""
Original 1996 Tamagotchi P1 — highly detailed, separate working parts.

Real-world millimetres converted to inches (1 Blender unit = 1 inch) so the
FBX matches marionette / s&box import_scale 1.0, same as the phone kit.

Run:
  blender --background --factory-startup --python tools/tama_gen.py
"""
from __future__ import annotations

import json
import math
import os
import traceback
import uuid
from math import pi, radians, sin, cos

import bpy
import bmesh
from mathutils import Vector, Matrix

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
ROOT = r"C:\Users\pooh\Documents\s&box projects\marionette-main"
OUT_MODELS = os.path.join(ROOT, "Assets", "models", "tamagotchi")
OUT_MATS = os.path.join(ROOT, "Assets", "materials", "tamagotchi")
OUT_CODE = os.path.join(ROOT, "Code")
OUT_PREFAB = os.path.join(ROOT, "Assets", "prefabs")
BLEND_PATH = os.path.join(OUT_MODELS, "tamagotchi.blend")
PREVIEW_PATH = os.path.join(OUT_MODELS, "tamagotchi_preview.png")

# ---------------------------------------------------------------------------
# Units — millimetres in, inches out
# ---------------------------------------------------------------------------
IN = 0.03937007874015748  # mm -> inches (Blender units)


def mm(v: float) -> float:
    return v * IN


# Device envelope, original P1 (~48 x 17 x 56 mm). Slightly thicker than the
# real 14.5 mm toy so LR44 cells + PCB + LCD actually stack inside the cavity.
W = mm(48.0)       # X  width
T = mm(17.0)       # Y  thickness (front +Y)
H = mm(56.0)       # Z  height
WALL = mm(1.2)

LCD_W, LCD_H = mm(22.0), mm(16.0)
LCD_Z = mm(7.0)    # window centre above origin
LCD_CORNER = mm(2.0)

BTN_Z = mm(-16.5)
BTN_SPACING = mm(8.0)
BTN_D = mm(5.6)
BTN_HOLE = mm(6.0)

LR44_D = mm(11.6)
LR44_T = mm(5.4)

# Inner cavity ellipse (leave wall + air). Parts must stay inside this.
CAVITY = 0.78

# ---------------------------------------------------------------------------
# Scene
# ---------------------------------------------------------------------------
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = "NONE"
scene.unit_settings.scale_length = 1.0

# ---------------------------------------------------------------------------
# Tiny bpy helpers
# ---------------------------------------------------------------------------
def act(obj):
    bpy.context.view_layer.objects.active = obj
    return obj


def select_only(obj):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    act(obj)


def apply_xf(obj):
    select_only(obj)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)


def shade(obj, angle=35.0):
    select_only(obj)
    try:
        bpy.ops.object.shade_smooth_by_angle(angle=radians(angle))
    except Exception:
        bpy.ops.object.shade_smooth()


def origin_geometry(obj):
    select_only(obj)
    bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="BOUNDS")


def delete(obj):
    if obj is None:
        return
    select_only(obj)
    bpy.ops.object.delete()


def new_mat(name, color, rough=0.45, metal=0.0, alpha=1.0):
    mat = bpy.data.materials.get(name)
    if mat is None:
        mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    if bsdf is None:
        for n in nt.nodes:
            if n.type == "BSDF_PRINCIPLED":
                bsdf = n
                break
    col = (color[0], color[1], color[2], alpha)
    if bsdf:
        bsdf.inputs["Base Color"].default_value = col
        # Blender 4/5 renamed Specular IOR Level etc. Set what exists.
        for key, val in (
            ("Roughness", rough),
            ("Metallic", metal),
            ("Alpha", alpha),
            ("IOR", 1.5 if alpha < 0.99 else 1.45),
            ("Transmission Weight", 0.9 if alpha < 0.5 else 0.0),
        ):
            if key in bsdf.inputs:
                bsdf.inputs[key].default_value = val
    mat.diffuse_color = col
    if alpha < 0.99:
        mat.blend_method = "BLEND"
    return mat


def assign(obj, mat):
    if mat is None:
        return
    obj.data.materials.clear()
    obj.data.materials.append(mat)


def apply_mods(obj):
    select_only(obj)
    for m in list(obj.modifiers):
        try:
            bpy.ops.object.modifier_apply(modifier=m.name)
        except Exception as e:
            print(f"  modifier {m.name} on {obj.name} failed: {e}")
            obj.modifiers.remove(m)


def boolean(obj, cutter, op="DIFFERENCE"):
    select_only(obj)
    mod = obj.modifiers.new("bool", "BOOLEAN")
    mod.operation = op
    try:
        mod.operand_type = "OBJECT"
    except Exception:
        pass
    mod.object = cutter
    solvers = []
    for s in ("MANIFOLD", "EXACT", "FLOAT"):
        solvers.append(s)
    applied = False
    last = None
    for s in solvers:
        try:
            mod.solver = s
        except Exception:
            continue
        try:
            bpy.ops.object.modifier_apply(modifier=mod.name)
            applied = True
            break
        except Exception as e:
            last = e
    if not applied:
        print(f"  BOOLEAN {op} {obj.name} <- {cutter.name} FAILED ({last})")
        if mod.name in obj.modifiers:
            obj.modifiers.remove(mod)
    return applied


def join(objs, name):
    objs = [o for o in objs if o is not None]
    if not objs:
        return None
    if len(objs) == 1:
        objs[0].name = name
        return objs[0]
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    act(objs[0])
    bpy.ops.object.join()
    objs[0].name = name
    return objs[0]


def bevel(obj, width, segs=3, limit="ANGLE", angle=50.0):
    select_only(obj)
    m = obj.modifiers.new("bev", "BEVEL")
    m.width = width
    m.segments = segs
    m.limit_method = limit
    if limit == "ANGLE":
        m.angle_limit = radians(angle)
    apply_mods(obj)


# primitives ---------------------------------------------------------------
def cube(name, size, loc=(0, 0, 0), mat=None):
    bpy.ops.mesh.primitive_cube_add(size=1, location=loc)
    obj = bpy.context.object
    obj.name = name
    obj.scale = (size[0], size[1], size[2])
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def cyl(name, r, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=32, mat=None):
    bpy.ops.mesh.primitive_cylinder_add(
        radius=r, depth=depth, location=loc, rotation=rot, vertices=verts
    )
    obj = bpy.context.object
    obj.name = name
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def uvsp(name, r, loc=(0, 0, 0), segs=32, rings=16, mat=None):
    bpy.ops.mesh.primitive_uv_sphere_add(
        radius=r, location=loc, segments=segs, ring_count=rings
    )
    obj = bpy.context.object
    obj.name = name
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def ico(name, r, loc=(0, 0, 0), sub=2, mat=None):
    bpy.ops.mesh.primitive_ico_sphere_add(radius=r, location=loc, subdivisions=sub)
    obj = bpy.context.object
    obj.name = name
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def torus(name, maj, minr, loc=(0, 0, 0), rot=(0, 0, 0), maj_s=28, min_s=12, mat=None):
    bpy.ops.mesh.primitive_torus_add(
        location=loc,
        rotation=rot,
        major_radius=maj,
        minor_radius=minr,
        major_segments=maj_s,
        minor_segments=min_s,
    )
    obj = bpy.context.object
    obj.name = name
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def cone(name, r1, r2, depth, loc=(0, 0, 0), rot=(0, 0, 0), verts=24, mat=None):
    bpy.ops.mesh.primitive_cone_add(
        radius1=r1, radius2=r2, depth=depth, location=loc, rotation=rot, vertices=verts
    )
    obj = bpy.context.object
    obj.name = name
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def rounded_box(name, size, radius, loc=(0, 0, 0), segs=4, mat=None):
    obj = cube(name, size, loc, mat)
    r = min(radius, size[0] * 0.49, size[1] * 0.49, size[2] * 0.49)
    if r > 1e-5:
        bevel(obj, r, segs=segs, limit="ANGLE", angle=40.0)
    return obj


# bmesh egg ----------------------------------------------------------------
def make_egg_solid(name, rx, ry, rz, segs=48, rings=28, eggness=0.15):
    bpy.ops.mesh.primitive_uv_sphere_add(
        radius=1.0, location=(0, 0, 0), segments=segs, ring_count=rings
    )
    obj = bpy.context.object
    obj.name = name
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    for v in bm.verts:
        x, y, z = v.co
        # z in [-1,1] on a unit sphere
        k = 1.0 - eggness * max(z, 0.0) ** 1.35
        v.co.x = x * rx * k
        v.co.y = y * ry
        v.co.z = z * rz
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return obj


def hollow_egg(name, rx, ry, rz, wall):
    outer = make_egg_solid(name, rx, ry, rz)
    m = outer.modifiers.new("sol", "SOLIDIFY")
    m.thickness = wall
    m.offset = -1.0
    m.use_quality_normals = True
    m.use_even_offset = True
    apply_mods(outer)
    return outer


def bisect_keep_y(obj, keep_positive, plane_y=0.0):
    select_only(obj)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.bisect(
        plane_co=(0.0, plane_y, 0.0),
        plane_no=(0.0, 1.0, 0.0),
        clear_inner=keep_positive,
        clear_outer=not keep_positive,
        use_fill=True,
        threshold=0.00001,
    )
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


# heart curve (classic screen surround) ------------------------------------
def heart_solid(name, scale_x, scale_z, thick, loc, mat, n=64):
    """2D heart extruded along Y (device thickness)."""
    mesh = bpy.data.meshes.new(name)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    bm = bmesh.new()
    verts = []
    for i in range(n):
        t = 2 * pi * i / n
        # classic parametric heart, x right, y up
        hx = 16 * sin(t) ** 3
        hz = 13 * cos(t) - 5 * cos(2 * t) - 2 * cos(3 * t) - cos(4 * t)
        verts.append(bm.verts.new((hx * scale_x / 17.0, -thick * 0.5, hz * scale_z / 17.0)))
    bm.verts.ensure_lookup_table()
    bm.faces.new(verts)
    res = bmesh.ops.extrude_face_region(bm, geom=[f for f in bm.faces])
    extruded = [v for v in res["geom"] if isinstance(v, bmesh.types.BMVert)]
    for v in extruded:
        v.co.y += thick
    bm.to_mesh(mesh)
    bm.free()
    obj.location = loc
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    return obj


def curve_to_mesh(name, points, bevel_depth, mat=None):
    curve = bpy.data.curves.new(name + "_c", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = bevel_depth
    curve.bevel_resolution = 4
    curve.fill_mode = "FULL"
    spline = curve.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for i, p in enumerate(points):
        spline.points[i].co = (p[0], p[1], p[2], 1.0)
    obj = bpy.data.objects.new(name, curve)
    bpy.context.collection.objects.link(obj)
    select_only(obj)
    bpy.ops.object.convert(target="MESH")
    obj = bpy.context.object
    obj.name = name
    if mat:
        assign(obj, mat)
    return obj


def make_spring(name, radius, wire_r, height, coils=5, loc=(0, 0, 0), rot=(0, 0, 0), mat=None):
    n = int(coils * 24)
    pts = []
    for i in range(n + 1):
        t = i / n
        ang = t * coils * 2 * pi
        z = (t - 0.5) * height
        pts.append((radius * cos(ang), radius * sin(ang), z))
    obj = curve_to_mesh(name, pts, wire_r, mat)
    obj.location = loc
    obj.rotation_euler = rot
    apply_xf(obj)
    return obj


def make_phillips_cutter(head_r, head_h, loc, rot):
    w = head_r * 0.28
    d = head_r * 1.6
    h = head_h * 0.85
    a = cube("_ph_a", (d, w, h), loc)
    b = cube("_ph_b", (w, d, h), loc)
    boolean(a, b, "UNION")
    delete(b)
    a.rotation_euler = rot
    apply_xf(a)
    return a


def make_screw(name, shaft_len, shaft_d=mm(1.4), head_d=mm(2.5), head_h=mm(0.55),
               loc=(0, 0, 0), rot=(0, 0, 0), mat=None):
    """Pan-head JIS/Phillips machine screw, +Z is head -> shaft."""
    head = cyl(name + "_h", head_d * 0.5, head_h, (0, 0, shaft_len * 0.5 + head_h * 0.5), verts=24)
    # slight dome: second disc
    dome = uvsp(name + "_d", head_d * 0.48, (0, 0, shaft_len * 0.5 + head_h * 0.72), segs=16, rings=8)
    # flatten dome
    bm = bmesh.new()
    bm.from_mesh(dome.data)
    for v in bm.verts:
        if v.co.z < 0:
            v.co.z *= 0.15
        else:
            v.co.z *= 0.45
    bm.to_mesh(dome.data)
    bm.free()
    shaft = cyl(name + "_s", shaft_d * 0.5, shaft_len, (0, 0, 0), verts=16)
    # visual thread: helical wire
    thread = make_spring(
        name + "_t",
        shaft_d * 0.52,
        mm(0.12),
        shaft_len * 0.82,
        coils=max(4, int(shaft_len / mm(0.3))),
        loc=(0, 0, -shaft_len * 0.05),
    )
    cutter = make_phillips_cutter(head_d * 0.5, head_h, (0, 0, shaft_len * 0.5 + head_h * 0.72), (0, 0, 0))
    boolean(head, cutter, "DIFFERENCE")
    boolean(dome, cutter, "DIFFERENCE")
    delete(cutter)
    obj = join([head, dome, shaft, thread], name)
    obj.location = loc
    obj.rotation_euler = rot
    apply_xf(obj)
    if mat:
        assign(obj, mat)
    shade(obj, 25)
    return obj


# ---------------------------------------------------------------------------
# Materials
# ---------------------------------------------------------------------------
M_ABS = new_mat("tama_abs_white", (0.93, 0.90, 0.86), rough=0.38, metal=0.0)
M_ABS_BACK = new_mat("tama_abs_back", (0.90, 0.87, 0.82), rough=0.42, metal=0.0)
M_PINK = new_mat("tama_abs_pink", (0.95, 0.42, 0.58), rough=0.35, metal=0.0)
M_RUBBER = new_mat("tama_rubber", (0.90, 0.28, 0.48), rough=0.72, metal=0.0)
M_RUBBER_BLK = new_mat("tama_rubber_blk", (0.12, 0.12, 0.13), rough=0.80, metal=0.0)
M_LENS = new_mat("tama_lens", (0.70, 0.82, 0.80), rough=0.08, metal=0.0, alpha=0.22)
M_LCD = new_mat("tama_lcd", (0.18, 0.28, 0.16), rough=0.22, metal=0.0)
M_POLAR = new_mat("tama_polarizer", (0.12, 0.16, 0.12), rough=0.18, metal=0.0)
M_REFLECT = new_mat("tama_reflector", (0.78, 0.80, 0.72), rough=0.35, metal=0.15)
M_FR4 = new_mat("tama_fr4", (0.10, 0.38, 0.20), rough=0.48, metal=0.0)
M_COPPER = new_mat("tama_copper", (0.72, 0.40, 0.14), rough=0.28, metal=0.85)
M_GOLD = new_mat("tama_gold", (0.83, 0.65, 0.22), rough=0.22, metal=0.9)
M_STEEL = new_mat("tama_steel", (0.70, 0.71, 0.73), rough=0.32, metal=0.85)
M_NICKEL = new_mat("tama_nickel", (0.78, 0.79, 0.81), rough=0.25, metal=0.9)
M_BAT_PLUS = new_mat("tama_battery_plus", (0.82, 0.62, 0.12), rough=0.30, metal=0.8)
M_BAT_BODY = new_mat("tama_battery_body", (0.55, 0.56, 0.58), rough=0.40, metal=0.6)
M_EPOXY = new_mat("tama_epoxy", (0.05, 0.05, 0.05), rough=0.55, metal=0.0)
M_FOAM = new_mat("tama_foam", (0.78, 0.74, 0.55), rough=0.90, metal=0.0)
M_SILICONE = new_mat("tama_silicone", (0.18, 0.18, 0.19), rough=0.78, metal=0.0)
M_CARBON = new_mat("tama_carbon", (0.08, 0.08, 0.08), rough=0.65, metal=0.1)
M_PIEZO = new_mat("tama_piezo", (0.78, 0.62, 0.28), rough=0.28, metal=0.7)
M_WIRE_R = new_mat("tama_wire_red", (0.72, 0.08, 0.08), rough=0.55, metal=0.0)
M_WIRE_K = new_mat("tama_wire_blk", (0.08, 0.08, 0.08), rough=0.55, metal=0.0)
M_CRYSTAL = new_mat("tama_crystal", (0.72, 0.74, 0.76), rough=0.22, metal=0.7)
M_SMD = new_mat("tama_smd", (0.12, 0.12, 0.12), rough=0.40, metal=0.1)
M_TAB = new_mat("tama_pulltab", (0.95, 0.88, 0.20), rough=0.55, metal=0.0)
M_CHAIN = new_mat("tama_chain", (0.82, 0.68, 0.22), rough=0.28, metal=0.9)
M_ZEBRA = new_mat("tama_zebra", (0.55, 0.52, 0.45), rough=0.70, metal=0.0)
M_GASKET = new_mat("tama_gasket", (0.10, 0.10, 0.11), rough=0.85, metal=0.0)
M_ICON = new_mat("tama_icons", (0.08, 0.10, 0.08), rough=0.45, metal=0.0)

# (name, color-for-kit, surface_prop)
PARTS = []  # filled as we go


def register(obj, color, surface="plastic"):
    shade(obj)
    PARTS.append({"obj": obj, "name": obj.name, "color": color, "surface": surface})
    return obj


# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
print("=== Tamagotchi build ===")
rx, ry, rz = W * 0.5, T * 0.5, H * 0.5

# -- hollow egg, split --
print("shells")
hollow = hollow_egg("_egg", rx, ry, rz, WALL)
front = hollow
# duplicate for rear before cutting
select_only(hollow)
bpy.ops.object.duplicate()
rear = bpy.context.object
rear.name = "tama_rear_shell"

front.name = "tama_front_shell"
bisect_keep_y(front, keep_positive=True, plane_y=mm(-0.15))
bisect_keep_y(rear, keep_positive=False, plane_y=mm(0.15))

# window cutter (rounded rect, through front)
win = rounded_box(
    "_win",
    (LCD_W, T * 1.4, LCD_H),
    LCD_CORNER,
    loc=(0, ry * 0.4, LCD_Z),
    segs=5,
)
boolean(front, win, "DIFFERENCE")
delete(win)

# button holes
for i, x in enumerate((-BTN_SPACING, 0.0, BTN_SPACING)):
    hcut = cyl(f"_bh{i}", BTN_HOLE * 0.5, T * 1.2, loc=(x, ry * 0.35, BTN_Z), rot=(radians(90), 0, 0), verts=24)
    boolean(front, hcut, "DIFFERENCE")
    delete(hcut)

# keychain hole through both shells near the top
lug_z = rz - mm(3.2)
lug_y = 0.0
kh = cyl("_kh", mm(1.7), W * 0.6, loc=(0, lug_y, lug_z), rot=(0, radians(90), 0), verts=20)
boolean(front, kh, "DIFFERENCE")
boolean(rear, kh, "DIFFERENCE")
delete(kh)

# battery well in rear: rounded pocket, fully inside the egg
well_w, well_h, well_d = mm(24.0), mm(14.0), mm(7.0)
well = rounded_box(
    "_well",
    (well_w, well_d, well_h),
    mm(2.0),
    loc=(0, -ry + well_d * 0.35, mm(-8.0)),
    segs=4,
)
boolean(rear, well, "DIFFERENCE")
delete(well)

# reset pinhole in battery well
rh = cyl("_rh", mm(1.1), mm(8), loc=(0, -ry + mm(2.0), mm(-16.5)), rot=(radians(90), 0, 0), verts=12)
boolean(rear, rh, "DIFFERENCE")
delete(rh)

# case-screw holes through rear (kept inside the cavity ellipse)
CASE_SCREWS = [
    (-mm(12.0), -ry + mm(1.2), mm(15.0)),
    (mm(12.0), -ry + mm(1.2), mm(15.0)),
    (-mm(12.0), -ry + mm(1.2), mm(-15.0)),
    (mm(12.0), -ry + mm(1.2), mm(-15.0)),
]
for i, p in enumerate(CASE_SCREWS):
    sc = cyl(f"_csh{i}", mm(0.8), mm(10), loc=p, rot=(radians(90), 0, 0), verts=12)
    boolean(rear, sc, "DIFFERENCE")
    boolean(front, sc, "DIFFERENCE")
    delete(sc)

# battery-cover screw holes
COVER_SCREWS = [
    (-mm(8.5), -ry + mm(0.4), mm(-3.0)),
    (mm(8.5), -ry + mm(0.4), mm(-3.0)),
    (-mm(8.5), -ry + mm(0.4), mm(-13.0)),
    (mm(8.5), -ry + mm(0.4), mm(-13.0)),
]
for i, p in enumerate(COVER_SCREWS):
    sc = cyl(f"_bsh{i}", mm(0.8), mm(8), loc=p, rot=(radians(90), 0, 0), verts=12)
    boolean(rear, sc, "DIFFERENCE")
    delete(sc)

# inner screw bosses on the front (union cylinders)
print("bosses")
PCB_SCREWS = [
    (-mm(11.0), mm(1.0), mm(12.0)),
    (mm(11.0), mm(1.0), mm(12.0)),
    (-mm(11.0), mm(1.0), mm(-8.0)),
    (mm(11.0), mm(1.0), mm(-8.0)),
]
bosses = []
for i, p in enumerate(PCB_SCREWS):
    boss = cyl(f"_boss{i}", mm(2.0), mm(3.2), loc=p, rot=(radians(90), 0, 0), verts=16)
    hole = cyl(f"_bossh{i}", mm(0.7), mm(4.0), loc=p, rot=(radians(90), 0, 0), verts=12)
    boolean(boss, hole, "DIFFERENCE")
    delete(hole)
    bosses.append(boss)
if bosses:
    boss_mesh = join(bosses, "_bosses")
    boolean(front, boss_mesh, "UNION")
    delete(boss_mesh)

assign(front, M_ABS)
assign(rear, M_ABS_BACK)
register(front, (0.93, 0.90, 0.86), "plastic")
register(rear, (0.90, 0.87, 0.82), "plastic")

# -- battery cover --
print("battery cover")
cover = rounded_box(
    "tama_battery_cover",
    (mm(23.5), mm(1.20), mm(13.5)),
    mm(1.6),
    loc=(0, -ry + mm(0.15), mm(-8.0)),
    segs=4,
    mat=M_ABS_BACK,
)
# polarity + / - stamps as shallow cutters
plus_bar = cube("_plus1", (mm(2.2), mm(0.6), mm(0.5)), loc=(-mm(6.5), -ry - mm(0.4), mm(-4.5)))
plus_stem = cube("_plus2", (mm(0.5), mm(0.6), mm(2.2)), loc=(-mm(6.5), -ry - mm(0.4), mm(-4.5)))
minus = cube("_minus", (mm(2.2), mm(0.6), mm(0.5)), loc=(mm(6.5), -ry - mm(0.4), mm(-4.5)))
boolean(cover, plus_bar, "DIFFERENCE")
boolean(cover, plus_stem, "DIFFERENCE")
boolean(cover, minus, "DIFFERENCE")
delete(plus_bar)
delete(plus_stem)
delete(minus)
for i, p in enumerate(COVER_SCREWS):
    sc = cyl(f"_cvrh{i}", mm(0.8), mm(4), loc=(p[0], -ry + mm(0.15), p[2]), rot=(radians(90), 0, 0), verts=12)
    boolean(cover, sc, "DIFFERENCE")
    delete(sc)
register(cover, (0.88, 0.85, 0.80), "plastic")

# -- faceplate (heart / oval printed bezel) --
print("faceplate + lens")
face = heart_solid(
    "tama_faceplate",
    scale_x=mm(17.5),
    scale_z=mm(16.5),
    thick=mm(0.7),
    loc=(0, ry - mm(0.55), LCD_Z - mm(1.0)),
    mat=M_PINK,
    n=72,
)
# punch the window
win2 = rounded_box("_win2", (LCD_W - mm(1.0), mm(4), LCD_H - mm(1.0)), mm(2.0), loc=(0, ry - mm(0.55), LCD_Z), segs=5)
boolean(face, win2, "DIFFERENCE")
delete(win2)
register(face, (0.95, 0.42, 0.58), "plastic")

lens = rounded_box(
    "tama_screen_lens",
    (LCD_W - mm(0.4), mm(0.55), LCD_H - mm(0.4)),
    mm(2.0),
    loc=(0, ry - mm(0.15), LCD_Z),
    segs=5,
    mat=M_LENS,
)
register(lens, (0.55, 0.72, 0.70), "glass")

# -- LCD stack --
print("LCD stack")
lcd_y = mm(3.6)
polar_f = rounded_box(
    "tama_lcd_polarizer_front",
    (LCD_W - mm(1.6), mm(0.22), LCD_H - mm(1.6)),
    mm(0.4),
    loc=(0, lcd_y + mm(0.55), LCD_Z),
    segs=3,
    mat=M_POLAR,
)
lcd_glass = rounded_box(
    "tama_lcd_glass",
    (LCD_W - mm(1.8), mm(0.70), LCD_H - mm(1.8)),
    mm(0.4),
    loc=(0, lcd_y, LCD_Z),
    segs=3,
    mat=M_LCD,
)
reflect = rounded_box(
    "tama_lcd_reflector",
    (LCD_W - mm(1.8), mm(0.18), LCD_H - mm(1.8)),
    mm(0.4),
    loc=(0, lcd_y - mm(0.50), LCD_Z),
    segs=3,
    mat=M_REFLECT,
)
holder = rounded_box(
    "tama_lcd_holder",
    (LCD_W + mm(1.6), mm(1.5), LCD_H + mm(1.6)),
    mm(1.2),
    loc=(0, lcd_y - mm(0.1), LCD_Z),
    segs=3,
    mat=M_ABS,
)
# open the holder
inner_h = rounded_box(
    "_hld",
    (LCD_W - mm(1.2), mm(2.4), LCD_H - mm(1.2)),
    mm(0.6),
    loc=(0, lcd_y - mm(0.1), LCD_Z),
    segs=3,
)
boolean(holder, inner_h, "DIFFERENCE")
delete(inner_h)
foam = rounded_box(
    "tama_lcd_foam",
    (LCD_W + mm(0.4), mm(0.6), LCD_H + mm(0.4)),
    mm(0.8),
    loc=(0, lcd_y - mm(1.15), LCD_Z),
    segs=3,
    mat=M_FOAM,
)
inner_f = rounded_box(
    "_fm",
    (LCD_W - mm(3.0), mm(1.2), LCD_H - mm(3.0)),
    mm(0.4),
    loc=(0, lcd_y - mm(1.15), LCD_Z),
    segs=3,
)
boolean(foam, inner_f, "DIFFERENCE")
delete(inner_f)

register(polar_f, (0.12, 0.16, 0.12), "plastic")
register(lcd_glass, (0.18, 0.28, 0.16), "glass")
register(reflect, (0.78, 0.80, 0.72), "plastic")
register(holder, (0.85, 0.82, 0.78), "plastic")
register(foam, (0.78, 0.74, 0.55), "rubber")

# zebra elastomeric connector (32-way) at bottom of LCD
zebra_stripes = []
n_pads = 32
zebra_w = mm(20.0)
pitch = zebra_w / n_pads
zx0 = -zebra_w * 0.5 + pitch * 0.5
zy = lcd_y - mm(0.2)
zz = LCD_Z - LCD_H * 0.5 + mm(0.4)
for i in range(n_pads):
    col = M_CARBON if i % 2 == 0 else M_ZEBRA
    s = cube(
        f"_zb{i}",
        (pitch * 0.85, mm(1.8), mm(2.2)),
        loc=(zx0 + i * pitch, zy, zz),
        mat=col,
    )
    zebra_stripes.append(s)
zebra = join(zebra_stripes, "tama_zebra_strip")
assign(zebra, M_ZEBRA)
register(zebra, (0.45, 0.42, 0.36), "rubber")

# icon overlay around the LCD (classic P1 8 icons, simplified geometry)
def icon_box(name, sz, loc):
    return cube(name, sz, loc, M_ICON)


icons = []
# top row around LCD: food, light, play, medicine
iz = LCD_Z + LCD_H * 0.5 + mm(2.4)
iy = ry - mm(0.85)
icons += [
    cube("_ic_food", (mm(1.6), mm(0.25), mm(1.1)), (-mm(8.5), iy, iz), M_ICON),
    cube("_ic_food2", (mm(0.35), mm(0.25), mm(2.0)), (-mm(7.4), iy, iz), M_ICON),
    cyl("_ic_light", mm(0.7), mm(0.25), (-mm(2.8), iy, iz), (radians(90), 0, 0), 10, M_ICON),
    cube("_ic_play", (mm(1.8), mm(0.25), mm(1.6)), (mm(2.8), iy, iz), M_ICON),
    cube("_ic_med1", (mm(2.0), mm(0.25), mm(0.45)), (mm(8.5), iy, iz), M_ICON),
    cube("_ic_med2", (mm(0.45), mm(0.25), mm(2.0)), (mm(8.5), iy, iz), M_ICON),
]
iz2 = LCD_Z - LCD_H * 0.5 - mm(2.4)
icons += [
    rounded_box("_ic_wc", (mm(1.6), mm(0.25), mm(1.8)), mm(0.3), (-mm(8.5), iy, iz2), 2, M_ICON),
    cube("_ic_meter", (mm(2.4), mm(0.25), mm(0.7)), (-mm(2.8), iy, iz2), M_ICON),
    cube("_ic_disc", (mm(2.0), mm(0.25), mm(1.1)), (mm(2.8), iy, iz2), M_ICON),
    heart_solid("_ic_att", mm(1.3), mm(1.2), mm(0.25), (mm(8.5), iy, iz2), M_ICON, n=24),
]
icon_mesh = join(icons, "tama_lcd_icons")
assign(icon_mesh, M_ICON)
register(icon_mesh, (0.08, 0.10, 0.08), "plastic")

# -- buttons --
print("buttons")
btn_objs = []
for name, x in (("tama_button_a", -BTN_SPACING), ("tama_button_b", 0.0), ("tama_button_c", BTN_SPACING)):
    cap = cyl(name, BTN_D * 0.5, mm(1.8), loc=(x, ry - mm(0.85), BTN_Z), rot=(radians(90), 0, 0), verts=24, mat=M_RUBBER)
    # slight dome
    dome = uvsp(name + "_dome", BTN_D * 0.48, loc=(x, ry - mm(0.05), BTN_Z), segs=16, rings=8, mat=M_RUBBER)
    bm = bmesh.new()
    bm.from_mesh(dome.data)
    for v in bm.verts:
        # squash along Y so it sits as a button dome
        v.co.y *= 0.35
    bm.to_mesh(dome.data)
    bm.free()
    stem = cyl(name + "_stem", mm(2.0), mm(2.2), loc=(x, ry - mm(2.2), BTN_Z), rot=(radians(90), 0, 0), verts=12, mat=M_RUBBER)
    b = join([cap, dome, stem], name)
    assign(b, M_RUBBER)
    register(b, (0.90, 0.28, 0.48), "rubber")
    btn_objs.append(b)

# silicone keypad membrane (one piece with three pills)
print("membrane")
mem_pads = []
mem = rounded_box(
    "tama_keypad_membrane",
    (mm(24.0), mm(0.6), mm(8.5)),
    mm(1.2),
    loc=(0, mm(1.4), BTN_Z),
    segs=3,
    mat=M_SILICONE,
)
carbons = []
for i, x in enumerate((-BTN_SPACING, 0.0, BTN_SPACING)):
    pill = cyl(f"tama_carbon_{'abc'[i]}", mm(2.2), mm(0.45), loc=(x, mm(1.15), BTN_Z), rot=(radians(90), 0, 0), verts=16, mat=M_CARBON)
    register(pill, (0.08, 0.08, 0.08), "rubber")
    carbons.append(pill)
register(mem, (0.18, 0.18, 0.19), "rubber")

# reset rubber nipple
reset = cyl(
    "tama_reset_button",
    mm(1.4),
    mm(2.6),
    loc=(0, -ry + mm(3.2), mm(-16.5)),
    rot=(radians(90), 0, 0),
    verts=12,
    mat=M_RUBBER_BLK,
)
register(reset, (0.12, 0.12, 0.13), "rubber")

# -- PCB --
print("PCB")
# Elliptical board that stays inside the egg — a rectangle's corners punched through the shell.
pcb = cyl(
    "tama_pcb",
    mm(13.0),
    mm(0.80),
    loc=(0, mm(0.25), mm(1.5)),
    rot=(radians(90), 0, 0),
    verts=48,
    mat=M_FR4,
)
select_only(pcb)
pcb.scale = (1.0, 1.0, 16.0 / 13.0)
apply_xf(pcb)
# safety clip against the inner cavity
cav = make_egg_solid("_pcb_cav", rx * CAVITY, ry * CAVITY, rz * CAVITY, segs=32, rings=16)
boolean(pcb, cav, "INTERSECT")
delete(cav)
# screw holes
for i, p in enumerate(PCB_SCREWS):
    hcut = cyl(f"_pcb_h{i}", mm(0.85), mm(3), loc=(p[0], mm(0.25), p[2]), rot=(radians(90), 0, 0), verts=12)
    boolean(pcb, hcut, "DIFFERENCE")
    delete(hcut)
register(pcb, (0.10, 0.38, 0.20), "computer")

# gold LCD pads (32)
pad_meshes = []
for i in range(n_pads):
    p = cube(
        f"_pad{i}",
        (pitch * 0.7, mm(0.12), mm(2.0)),
        loc=(zx0 + i * pitch, mm(0.62), zz - mm(1.6)),
        mat=M_GOLD,
    )
    pad_meshes.append(p)
lcd_pads = join(pad_meshes, "tama_lcd_pads")
assign(lcd_pads, M_GOLD)
register(lcd_pads, (0.83, 0.65, 0.22), "metal")

# button contact pairs on PCB
btn_pads = []
for i, x in enumerate((-BTN_SPACING, 0.0, BTN_SPACING)):
    a = cube(f"_bp{i}a", (mm(2.4), mm(0.1), mm(1.1)), (x - mm(1.3), mm(0.62), BTN_Z), M_GOLD)
    b = cube(f"_bp{i}b", (mm(2.4), mm(0.1), mm(1.1)), (x + mm(1.3), mm(0.62), BTN_Z), M_GOLD)
    btn_pads += [a, b]
bp = join(btn_pads, "tama_button_pads")
assign(bp, M_GOLD)
register(bp, (0.83, 0.65, 0.22), "metal")

# reset pads
rp = cube("tama_reset_pads", (mm(2.5), mm(0.1), mm(2.5)), (0, mm(0.62), mm(-16.5)), M_GOLD)
register(rp, (0.83, 0.65, 0.22), "metal")

# CPU COB blob (E0C6S46)
cpu_base = rounded_box("tama_cpu_die", (mm(7.5), mm(0.4), mm(5.5)), mm(0.4), loc=(0, mm(0.85), mm(4.0)), segs=2, mat=M_SMD)
blob = uvsp("tama_cpu_blob", mm(4.2), loc=(0, mm(1.55), mm(4.0)), segs=24, rings=12, mat=M_EPOXY)
bm = bmesh.new()
bm.from_mesh(blob.data)
for v in bm.verts:
    v.co.x *= 1.15
    v.co.z *= 0.85
    v.co.y *= 0.55
    if v.co.y < 0:
        v.co.y *= 0.2
bm.to_mesh(blob.data)
bm.free()
cpu = join([cpu_base, blob], "tama_cpu_blob")
assign(cpu, M_EPOXY)
register(cpu, (0.05, 0.05, 0.05), "plastic")

cpu_foam = rounded_box(
    "tama_cpu_foam",
    (mm(8.0), mm(0.9), mm(6.5)),
    mm(0.8),
    loc=(0, mm(1.7), mm(4.0)),
    segs=3,
    mat=M_FOAM,
)
register(cpu_foam, (0.78, 0.74, 0.55), "rubber")

# 32.768 kHz watch crystal (metal can)
xtal = cyl(
    "tama_crystal",
    mm(1.05),
    mm(6.0),
    loc=(mm(8.5), mm(0.95), mm(-5.0)),
    rot=(0, radians(90), 0),
    verts=16,
    mat=M_CRYSTAL,
)
# two leads
lead1 = cyl("_xl1", mm(0.2), mm(1.6), loc=(mm(6.6), mm(0.55), mm(-5.0) - mm(0.7)), rot=(radians(90), 0, 0), verts=8, mat=M_STEEL)
lead2 = cyl("_xl2", mm(0.2), mm(1.6), loc=(mm(6.6), mm(0.55), mm(-5.0) + mm(0.7)), rot=(radians(90), 0, 0), verts=8, mat=M_STEEL)
xtal = join([xtal, lead1, lead2], "tama_crystal")
assign(xtal, M_CRYSTAL)
register(xtal, (0.72, 0.74, 0.76), "metal")

# SMD passives clustered near the CPU (0603-ish)
smd_list = []
smd_spots = [
    (mm(-7.0), mm(0.7), mm(8.0), mm(1.6), mm(0.5), mm(0.8)),
    (mm(-7.0), mm(0.7), mm(6.2), mm(1.6), mm(0.5), mm(0.8)),
    (mm(7.0), mm(0.7), mm(8.0), mm(1.6), mm(0.5), mm(0.8)),
    (mm(7.0), mm(0.7), mm(6.2), mm(1.6), mm(0.5), mm(0.8)),
    (mm(-5.0), mm(0.7), mm(-1.5), mm(2.0), mm(0.6), mm(1.2)),
    (mm(5.5), mm(0.7), mm(-1.8), mm(1.6), mm(0.5), mm(0.8)),
    (mm(-9.0), mm(0.7), mm(2.0), mm(1.2), mm(0.4), mm(0.6)),
    (mm(9.0), mm(0.7), mm(2.0), mm(1.2), mm(0.4), mm(0.6)),
]
for i, (x, y, z, sx, sy, sz) in enumerate(smd_spots):
    smd_list.append(cube(f"_smd{i}", (sx, sy, sz), (x, y, z), M_SMD))
smds = join(smd_list, "tama_smd_passives")
assign(smds, M_SMD)
register(smds, (0.12, 0.12, 0.12), "computer")

# battery contact pads on PCB
bat_pad_p = cube("tama_pcb_pad_bat_p", (mm(3.5), mm(0.12), mm(3.5)), (-mm(7.0), mm(0.62), mm(-8.0)), M_GOLD)
bat_pad_n = cube("tama_pcb_pad_bat_n", (mm(3.5), mm(0.12), mm(3.5)), (mm(7.0), mm(0.62), mm(-8.0)), M_GOLD)
register(bat_pad_p, (0.83, 0.65, 0.22), "metal")
register(bat_pad_n, (0.83, 0.65, 0.22), "metal")

piezo_pad_a = cube("tama_pcb_pad_piezo_a", (mm(2.0), mm(0.12), mm(2.0)), (-mm(5.0), mm(0.70), mm(14.8)), M_GOLD)
piezo_pad_b = cube("tama_pcb_pad_piezo_b", (mm(2.0), mm(0.12), mm(2.0)), (-mm(2.8), mm(0.70), mm(14.8)), M_GOLD)
register(piezo_pad_a, (0.83, 0.65, 0.22), "metal")
register(piezo_pad_b, (0.83, 0.65, 0.22), "metal")

# -- batteries + contacts --
print("power")
# Cells sit in the rear well, fully inside the egg: cover / spring / cell / PCB side.
bat_y = -ry + WALL + mm(1.2) + mm(1.0) + LR44_T * 0.5
bat_z = mm(-8.0)


def make_lr44(name, loc):
    body = cyl(name + "_b", LR44_D * 0.5, LR44_T * 0.72, loc=(loc[0], loc[1], loc[2]), rot=(radians(90), 0, 0), verts=32, mat=M_BAT_BODY)
    plus = cyl(name + "_p", LR44_D * 0.38, LR44_T * 0.18, loc=(loc[0], loc[1] + LR44_T * 0.40, loc[2]), rot=(radians(90), 0, 0), verts=24, mat=M_BAT_PLUS)
    minus = cyl(name + "_m", LR44_D * 0.5, LR44_T * 0.12, loc=(loc[0], loc[1] - LR44_T * 0.42, loc[2]), rot=(radians(90), 0, 0), verts=24, mat=M_NICKEL)
    o = join([body, plus, minus], name)
    assign(o, M_BAT_BODY)
    return o


bat_l = make_lr44("tama_battery_l", (-mm(6.2), bat_y, bat_z))
bat_r = make_lr44("tama_battery_r", (mm(6.2), bat_y, bat_z))
register(bat_l, (0.55, 0.56, 0.58), "metal")
register(bat_r, (0.55, 0.56, 0.58), "metal")

# + contact: flat strip with dimple against left cell
contact_p = rounded_box(
    "tama_contact_pos",
    (mm(8.5), mm(0.25), mm(8.5)),
    mm(0.4),
    loc=(-mm(6.2), bat_y + LR44_T * 0.55 + mm(0.3), bat_z),
    segs=2,
    mat=M_NICKEL,
)
dimple = uvsp("_dim", mm(1.4), loc=(-mm(6.2), bat_y + LR44_T * 0.55 + mm(0.05), bat_z), segs=12, rings=8, mat=M_NICKEL)
contact_p = join([contact_p, dimple], "tama_contact_pos")
assign(contact_p, M_NICKEL)
register(contact_p, (0.78, 0.79, 0.81), "metal")

# - contact: coil spring against right cell, BETWEEN cell and battery cover (inside the well)
contact_n = make_spring(
    "tama_contact_neg",
    radius=mm(2.6),
    wire_r=mm(0.18),
    height=mm(2.2),
    coils=4,
    loc=(mm(6.2), bat_y - LR44_T * 0.50 - mm(0.9), bat_z),
    rot=(radians(90), 0, 0),
    mat=M_STEEL,
)
register(contact_n, (0.70, 0.71, 0.73), "metal")

# series jumper: U-strip connecting the two cells in series
arm1 = cube("_ser1", (mm(0.3), mm(2.4), mm(3.2)), (-mm(6.2), bat_y - mm(0.1), bat_z + mm(5.5)), M_NICKEL)
arm2 = cube("_ser2", (mm(0.3), mm(2.4), mm(3.2)), (mm(6.2), bat_y - mm(0.1), bat_z + mm(5.5)), M_NICKEL)
bridge = cube("_ser3", (mm(12.4), mm(0.25), mm(3.2)), (0, bat_y - mm(1.2), bat_z + mm(5.5)), M_NICKEL)
series = join([arm1, arm2, bridge], "tama_contact_series")
assign(series, M_NICKEL)
register(series, (0.78, 0.79, 0.81), "metal")

# factory insulation pull-tab
tab = rounded_box(
    "tama_pull_tab",
    (mm(7.0), mm(0.12), mm(14.0)),
    mm(0.4),
    loc=(-mm(6.2), bat_y + LR44_T * 0.20, bat_z - mm(3.0)),
    segs=2,
    mat=M_TAB,
)
register(tab, (0.95, 0.88, 0.20), "plastic")

# -- piezo buzzer + wires --
print("audio")
piezo = cyl(
    "tama_piezo_disc",
    mm(6.0),
    mm(0.35),
    loc=(mm(0.0), -mm(3.2), mm(14.5)),
    rot=(radians(90), 0, 0),
    verts=32,
    mat=M_PIEZO,
)
# ceramic spot
cer = cyl("_cer", mm(4.0), mm(0.18), loc=(0, -mm(3.0), mm(14.5)), rot=(radians(90), 0, 0), verts=24, mat=M_EPOXY)
piezo = join([piezo, cer], "tama_piezo_disc")
assign(piezo, M_PIEZO)
register(piezo, (0.78, 0.62, 0.28), "metal")

wire_p = curve_to_mesh(
    "tama_piezo_wire_p",
    [
        (mm(-1.5), -mm(3.2), mm(14.5)),
        (mm(-3.0), -mm(1.2), mm(14.6)),
        (mm(-5.0), mm(0.2), mm(14.8)),
    ],
    mm(0.16),
    M_WIRE_R,
)
wire_n = curve_to_mesh(
    "tama_piezo_wire_n",
    [
        (mm(1.5), -mm(3.2), mm(14.5)),
        (mm(-0.5), -mm(1.2), mm(14.6)),
        (mm(-2.8), mm(0.2), mm(14.8)),
    ],
    mm(0.16),
    M_WIRE_K,
)
register(wire_p, (0.72, 0.08, 0.08), "plastic")
register(wire_n, (0.08, 0.08, 0.08), "plastic")

# -- gasket between halves --
print("gasket")
gasket_outer = make_egg_solid("_g_out", rx - mm(0.4), mm(0.45), rz - mm(0.4), segs=40, rings=20, eggness=0.15)
gasket_inner = make_egg_solid("_g_in", rx - mm(2.2), mm(1.2), rz - mm(2.2), segs=40, rings=20, eggness=0.15)
boolean(gasket_outer, gasket_inner, "DIFFERENCE")
delete(gasket_inner)
bisect_keep_y(gasket_outer, True, mm(-0.4))
bisect_keep_y(gasket_outer, False, mm(0.4))
gasket_outer.name = "tama_shell_gasket"
assign(gasket_outer, M_GASKET)
register(gasket_outer, (0.10, 0.10, 0.11), "rubber")

# -- keychain --
print("keychain")
ring = torus(
    "tama_keychain_ring",
    mm(4.0),
    mm(0.55),
    loc=(0, 0, lug_z + mm(0.2)),
    rot=(radians(90), 0, radians(90)),
    maj_s=28,
    min_s=10,
    mat=M_CHAIN,
)
register(ring, (0.82, 0.68, 0.22), "metal")

# ball chain — 7 balls + tiny links
chain_parts = []
# hang in +Z then curve to +X a little
for i in range(7):
    t = (i + 1) / 8.0
    ang = t * 0.6
    x = sin(ang) * mm(6)
    z = lug_z + mm(4.0) + i * mm(2.4)
    y = 0
    ball = uvsp(f"_ball{i}", mm(0.95), loc=(x, y, z), segs=12, rings=8, mat=M_CHAIN)
    chain_parts.append(ball)
    if i < 6:
        link = cyl(
            f"_lnk{i}",
            mm(0.28),
            mm(1.1),
            loc=(x + mm(0.4), y, z + mm(1.2)),
            rot=(0, radians(20), 0),
            verts=8,
            mat=M_CHAIN,
        )
        chain_parts.append(link)
chain = join(chain_parts, "tama_keychain_chain")
assign(chain, M_CHAIN)
register(chain, (0.82, 0.68, 0.22), "metal")

# clasp
clasp = torus(
    "tama_keychain_clasp",
    mm(3.2),
    mm(0.45),
    loc=(mm(5.5), 0, lug_z + mm(4.0) + 7 * mm(2.4)),
    rot=(0, 0, 0),
    maj_s=20,
    min_s=8,
    mat=M_CHAIN,
)
register(clasp, (0.82, 0.68, 0.22), "metal")

# -- screws --
print("screws")
# make_screw is built along +Z with the head at +Z. Rx(+90) sends +Z to -Y, so the
# head sits on the back of the device and the shaft points into it.
HEAD_H = mm(0.55)


def back_screw(name, shaft_len, x, z, surface_y):
    origin_y = surface_y + shaft_len * 0.5 + HEAD_H
    return make_screw(
        name,
        shaft_len=shaft_len,
        loc=(x, origin_y, z),
        rot=(radians(90), 0, 0),
        mat=M_STEEL,
    )


for i, p in enumerate(COVER_SCREWS):
    s = back_screw(f"tama_screw_cover_{i+1:02d}", mm(4.0), p[0], p[2], -ry)
    register(s, (0.70, 0.71, 0.73), "metal")

for i, p in enumerate(CASE_SCREWS):
    s = back_screw(f"tama_screw_case_{i+1:02d}", mm(5.5), p[0], p[2], -ry)
    register(s, (0.70, 0.71, 0.73), "metal")

for i, p in enumerate(PCB_SCREWS):
    s = back_screw(f"tama_screw_pcb_{i+1:02d}", mm(3.5), p[0], p[2], mm(-0.20))
    register(s, (0.70, 0.71, 0.73), "metal")

print(f"built {len(PARTS)} parts")

# ---------------------------------------------------------------------------
# Origins, UVs, export
# ---------------------------------------------------------------------------
os.makedirs(OUT_MODELS, exist_ok=True)
os.makedirs(OUT_MATS, exist_ok=True)
os.makedirs(OUT_CODE, exist_ok=True)
os.makedirs(OUT_PREFAB, exist_ok=True)

FBX_KW = dict(
    check_existing=False,
    use_selection=True,
    object_types={"MESH"},
    use_mesh_modifiers=True,
    mesh_smooth_type="FACE",
    use_tspace=True,
    add_leaf_bones=False,
    bake_anim=False,
    axis_forward="-Y",
    axis_up="Z",
    bake_space_transform=False,
    apply_unit_scale=False,
    apply_scale_options="FBX_SCALE_NONE",
    global_scale=1.0,
    path_mode="AUTO",
    embed_textures=False,
    batch_mode="OFF",
)


def smart_uv(obj):
    select_only(obj)
    try:
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.quads_convert_to_tris(quad_method="BEAUTY", ngon_method="BEAUTY")
        bpy.ops.uv.smart_project(angle_limit=66.0, island_margin=0.02)
        bpy.ops.object.mode_set(mode="OBJECT")
    except Exception as e:
        print("  uv fail", obj.name, e)
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass


def export_fbx(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    act(objects[0])
    bpy.ops.export_scene.fbx(filepath=path, **FBX_KW)


# record world-centre of each part, then zero for per-part export
records = []
world_copies = []  # (obj, loc)

for p in PARTS:
    obj = p["obj"]
    if obj is None or obj.name not in bpy.data.objects:
        print("  missing", p["name"])
        continue
    smart_uv(obj)
    origin_geometry(obj)
    loc = obj.location.copy()
    p["loc"] = (loc.x, loc.y, loc.z)
    records.append(p)

print("exporting per-part FBX")
for p in records:
    obj = p["obj"]
    stored = obj.location.copy()
    obj.location = (0, 0, 0)
    path = os.path.join(OUT_MODELS, p["name"] + ".fbx")
    try:
        export_fbx(path, [obj])
        print("  wrote", os.path.basename(path), "verts", len(obj.data.vertices))
    except Exception:
        traceback.print_exc()
    obj.location = stored

# assembled: one FBX with every part at its world position, origins at device centre
print("exporting assembled")
# duplicate, apply location into mesh, join
dups = []
for p in records:
    select_only(p["obj"])
    bpy.ops.object.duplicate()
    d = bpy.context.object
    d.name = p["name"] + "_asm"
    # bake world transform
    mw = d.matrix_world.copy()
    d.data.transform(mw)
    d.matrix_world = Matrix.Identity(4)
    dups.append(d)

assembled = join(dups, "tamagotchi")
origin_geometry(assembled)
# keep origin at world 0 of the device: shift so the original origin stays
# (join of baked meshes already lives in world space; origin_geometry moved it)
# Re-centre on the device origin (0,0,0) by applying the inverse of current location.
assembled.location = (0, 0, 0)
# The geometry is still in world inches around 0 because we baked matrix_world
# BEFORE origin_geometry. origin_geometry then shifted the origin to bounds centre
# and compensated location. Zeroing location would offset the mesh. Apply location.
select_only(assembled)
bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
export_fbx(os.path.join(OUT_MODELS, "tamagotchi.fbx"), [assembled])
print("  wrote tamagotchi.fbx verts", len(assembled.data.vertices))
# hide assembled so it doesn't pollute the blend as the only visible mesh
assembled.hide_viewport = True
assembled.hide_render = True

# exploded duplicate for a second FBX
print("exporting exploded")
exp = []
for p in records:
    select_only(p["obj"])
    bpy.ops.object.duplicate()
    d = bpy.context.object
    d.name = p["name"] + "_exp"
    # push along a vector from origin through the part centre
    loc = Vector(p["loc"])
    if loc.length < 1e-6:
        loc = Vector((0, 1, 0))
    d.location = loc + loc.normalized() * mm(14.0)
    mw = d.matrix_world.copy()
    d.data.transform(mw)
    d.matrix_world = Matrix.Identity(4)
    exp.append(d)
exploded = join(exp, "tamagotchi_exploded")
select_only(exploded)
bpy.ops.object.transform_apply(location=True, rotation=False, scale=False)
export_fbx(os.path.join(OUT_MODELS, "tamagotchi_exploded.fbx"), [exploded])
print("  wrote tamagotchi_exploded.fbx verts", len(exploded.data.vertices))
exploded.hide_viewport = True
exploded.hide_render = True

# ---------------------------------------------------------------------------
# vmdl / vmat / C# / prefab
# ---------------------------------------------------------------------------
VMDL = """<!-- kv3 encoding:text:version{{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d}} format:modeldoc30:version{{8c2d7a91-9c42-4bf0-883a-5a3b1762d4f1}} -->
{{
	rootNode =
	{{
		_class = "RootNode"
		children =
		[
			{{
				_class = "MaterialGroupList"
				children =
				[
					{{
						_class = "DefaultMaterialGroup"
						remaps = [  ]
						use_global_default = true
						global_default_material = "materials/default.vmat"
					}},
				]
			}},
			{{
				_class = "PhysicsShapeList"
				children =
				[
					{{
						_class = "PhysicsMeshFromRender"
						parent_bone = ""
						surface_prop = "{surface}"
						collision_tags = "solid"
					}},
				]
			}},
			{{
				_class = "RenderMeshList"
				children =
				[
					{{
						_class = "RenderMeshFile"
						filename = "models/tamagotchi/{name}.fbx"
						import_translation = [ 0.0, 0.0, 0.0 ]
						import_rotation = [ 0.0, 0.0, 0.0 ]
						import_scale = 1.0
						align_origin_x_type = "None"
						align_origin_y_type = "None"
						align_origin_z_type = "None"
						parent_bone = ""
						import_filter =
						{{
							exclude_by_default = false
							exception_list =
							[
							]
						}}
					}},
				]
			}},
		]
		model_archetype = ""
		primary_associated_entity = ""
		anim_graph_name = ""
		base_model_name = ""
	}}
}}
"""

VMAT = """// Tamagotchi part material. Flat tint, complex.shader, same slot layout as the lightswitch.
Layer0
{{
	shader "shaders/complex.shader"
	g_flModelTintAmount "1.000000"
	g_vColorTint "[{r:.6f} {g:.6f} {b:.6f} 1.000000]"
	TextureColor "materials/default/default_color.tga"
	TextureNormal "materials/default/default_normal.tga"
	TextureRoughness "materials/default/default_rough.tga"
	TextureAmbientOcclusion "materials/default/default_ao.tga"
	g_flMetalness "{metal:.6f}"
	g_flRoughness "{rough:.6f}"
}}
"""

vmat_specs = {
    "tama_abs_white": ((0.93, 0.90, 0.86), 0.0, 0.38),
    "tama_abs_back": ((0.90, 0.87, 0.82), 0.0, 0.42),
    "tama_abs_pink": ((0.95, 0.42, 0.58), 0.0, 0.35),
    "tama_rubber": ((0.90, 0.28, 0.48), 0.0, 0.72),
    "tama_lcd": ((0.18, 0.28, 0.16), 0.0, 0.22),
    "tama_lens": ((0.70, 0.82, 0.80), 0.0, 0.08),
    "tama_fr4": ((0.10, 0.38, 0.20), 0.0, 0.48),
    "tama_steel": ((0.70, 0.71, 0.73), 0.85, 0.32),
    "tama_gold": ((0.83, 0.65, 0.22), 0.90, 0.22),
    "tama_epoxy": ((0.05, 0.05, 0.05), 0.0, 0.55),
}

for n, (c, metal, rough) in vmat_specs.items():
    with open(os.path.join(OUT_MATS, n + ".vmat"), "w", encoding="utf-8") as f:
        f.write(VMAT.format(r=c[0], g=c[1], b=c[2], metal=metal, rough=rough))

for p in records:
    with open(os.path.join(OUT_MODELS, p["name"] + ".vmdl"), "w", encoding="utf-8") as f:
        f.write(VMDL.format(name=p["name"], surface=p["surface"]))

for extra, surf in (("tamagotchi", "plastic"), ("tamagotchi_exploded", "plastic")):
    with open(os.path.join(OUT_MODELS, extra + ".vmdl"), "w", encoding="utf-8") as f:
        f.write(VMDL.format(name=extra, surface=surf))

# C# teardown kit
cs_rows = []
for p in records:
    x, y, z = p["loc"]
    r, g, b = p["color"]
    cs_rows.append(
        f'\t\tnew( "{p["name"]}", new Vector3( {x:.6f}f, {y:.6f}f, {z:.6f}f ), new Color( {r:.4f}f, {g:.4f}f, {b:.4f}f ) ),'
    )

cs = f"""using Sandbox;
using System.Collections.Generic;

/// <summary>
/// Original 1996 Tamagotchi P1 as separate physics parts.
/// Real internals: shells, LCD stack, PCB, CPU blob, crystal, piezo, LR44 cells,
/// battery contacts, screws, keypad membrane, keychain. Press Use (E) to break apart.
/// </summary>
public sealed class TamagotchiTeardownKit : Component
{{
	[Property] public bool DisassembleOnUse {{ get; set; }} = true;
	[Property] public bool StartAssembled {{ get; set; }} = true;

	bool _broke;

	record PartSpec( string Name, Vector3 LocalPos, Color Tint );

	static readonly PartSpec[] Parts =
	[
{os.linesep.join(cs_rows)}
	];

	protected override void OnStart()
	{{
		var preview = Components.Get<ModelRenderer>();
		if ( preview is not null )
			preview.Enabled = false;

		if ( GameObject.Children.Count > 0 )
			return;

		foreach ( var spec in Parts )
		{{
			var model = Model.Load( $"models/tamagotchi/{{spec.Name}}.vmdl" );
			if ( model is null || model.IsError )
			{{
				Log.Warning( $"[tamagotchi] missing {{spec.Name}}.vmdl" );
				continue;
			}}

			var go = new GameObject( true, spec.Name );
			go.Parent = GameObject;
			go.LocalPosition = spec.LocalPos;
			go.LocalRotation = Rotation.Identity;
			go.Tags.Add( "tama_part" );

			var renderer = go.Components.Create<ModelRenderer>();
			renderer.Model = model;
			renderer.Tint = spec.Tint;

			var collider = go.Components.Create<ModelCollider>();
			collider.Model = model;

			var rb = go.Components.Create<Rigidbody>();
			rb.Gravity = true;
			rb.MotionEnabled = !StartAssembled;
			rb.StartAsleep = StartAssembled;
		}}
	}}

	protected override void OnUpdate()
	{{
		if ( DisassembleOnUse && Input.Pressed( "use" ) )
			BreakApart();
	}}

	public void BreakApart()
	{{
		if ( _broke ) return;
		_broke = true;

		foreach ( var child in GameObject.Children.ToArray() )
		{{
			var rb = child.Components.Get<Rigidbody>();
			if ( rb is not null )
			{{
				rb.MotionEnabled = true;
				rb.StartAsleep = false;
			}}
			child.SetParent( null, true );
		}}
	}}
}}
"""
with open(os.path.join(OUT_CODE, "TamagotchiTeardownKit.cs"), "w", encoding="utf-8") as f:
    f.write(cs)

manifest = {
    "parts": [
        {"name": p["name"], "loc": list(p["loc"]), "color": list(p["color"]), "surface": p["surface"]}
        for p in records
    ]
}
with open(os.path.join(OUT_MODELS, "parts.json"), "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)

# Assembled prefab — every part as a child at its fitted local position
def gid():
    return str(uuid.uuid4())


def prefab_child(p):
    name = p["name"]
    model = f"models/tamagotchi/{name}.vmdl"
    t = f"{p['color'][0]:.4f},{p['color'][1]:.4f},{p['color'][2]:.4f},1"
    loc = p["loc"]
    return {
        "__guid": gid(),
        "__version": 2,
        "Flags": 0,
        "Name": name,
        "Position": f"{loc[0]:.6f},{loc[1]:.6f},{loc[2]:.6f}",
        "Rotation": "0,0,0,1",
        "Scale": "1,1,1",
        "Tags": "tama_part",
        "Enabled": True,
        "NetworkMode": 2,
        "NetworkFlags": 0,
        "NetworkOrphaned": 0,
        "NetworkTransmit": True,
        "OwnerTransfer": 1,
        "Components": [
            {
                "__type": "Sandbox.Prop",
                "__guid": gid(),
                "__enabled": True,
                "Flags": 0,
                "BodyGroups": 18446744073709551615,
                "Health": 0,
                "IsStatic": False,
                "MaterialGroup": None,
                "Model": model,
                "StartAsleep": True,
                "Tint": t,
            },
            {
                "__type": "Sandbox.ModelRenderer",
                "__guid": gid(),
                "__enabled": True,
                "Flags": 0,
                "BodyGroups": 18446744073709551615,
                "CreateAttachments": False,
                "LodOverride": None,
                "MaterialGroup": None,
                "MaterialOverride": None,
                "Materials": None,
                "Model": model,
                "RenderOptions": {
                    "GameLayer": True,
                    "OverlayLayer": False,
                    "BloomLayer": False,
                    "AfterUILayer": False,
                },
                "RenderType": "On",
                "Tint": t,
            },
            {
                "__type": "Sandbox.ModelCollider",
                "__guid": gid(),
                "__enabled": True,
                "Flags": 0,
                "ColliderFlags": 0,
                "Elasticity": None,
                "Friction": None,
                "IsTrigger": False,
                "Model": model,
                "RollingResistance": None,
                "Static": False,
                "Surface": None,
                "SurfaceVelocity": "0,0,0",
            },
            {
                "__type": "Sandbox.Rigidbody",
                "__guid": gid(),
                "__enabled": True,
                "Flags": 0,
                "AngularDamping": 0.15,
                "EnableImpactDamage": False,
                "EnhancedCcd": False,
                "Gravity": True,
                "GravityScale": 1,
                "ImpactDamage": 0,
                "LinearDamping": 0.05,
                "Locking": {"X": False, "Y": False, "Z": False, "Pitch": False, "Yaw": False, "Roll": False},
                "MassCenterOverride": "0,0,0",
                "MassOverride": 0,
                "MinImpactDamageSpeed": 500,
                "MotionEnabled": False,
                "OverrideMassCenter": False,
                "RigidbodyFlags": 0,
                "SleepThreshold": 2,
                "StartAsleep": True,
            },
        ],
        "Children": [],
    }


prefab = {
    "RootObject": {
        "__guid": gid(),
        "__version": 2,
        "Flags": 0,
        "Name": "Tamagotchi",
        "Position": "0,0,0",
        "Rotation": "0,0,0,1",
        "Scale": "1,1,1",
        "Tags": "",
        "Enabled": True,
        "NetworkMode": 2,
        "NetworkFlags": 0,
        "NetworkOrphaned": 0,
        "NetworkTransmit": True,
        "OwnerTransfer": 1,
        "Components": [
            {
                "__type": "TamagotchiTeardownKit",
                "__guid": gid(),
                "__enabled": True,
                "DisassembleOnUse": True,
                "StartAssembled": True,
            }
        ],
        "Children": [prefab_child(p) for p in records],
    },
    "ResourceVersion": 2,
    "ShowInMenu": True,
    "MenuPath": "props/tamagotchi",
    "MenuIcon": None,
    "DontBreakAsTemplate": False,
    "__references": [],
    "__version": 2,
}
prefab_path = os.path.join(OUT_PREFAB, "tamagotchi.prefab")
compiled = os.path.join(OUT_PREFAB, "tamagotchi.prefab_c")
if os.path.exists(compiled):
    os.remove(compiled)
with open(prefab_path, "w", encoding="utf-8") as f:
    json.dump(prefab, f, indent=2)
print("wrote prefab", prefab_path, "children", len(records))

# ---------------------------------------------------------------------------
# Preview render
# ---------------------------------------------------------------------------
print("render preview")
# unhide originals
for p in records:
    p["obj"].hide_render = False
    p["obj"].hide_viewport = False

cam_data = bpy.data.cameras.new("preview_cam")
cam_data.lens = 50
cam = bpy.data.objects.new("preview_cam", cam_data)
bpy.context.collection.objects.link(cam)
# Front-quarter view so the screen, buttons and egg silhouette are visible
cam.location = (mm(55), mm(95), mm(28))
direction = Vector((0, 0, 0)) - cam.location
cam.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
scene.camera = cam

light_data = bpy.data.lights.new("key", "AREA")
light_data.energy = 50
light_data.size = mm(80)
key = bpy.data.objects.new("key", light_data)
bpy.context.collection.objects.link(key)
key.location = (mm(40), mm(80), mm(90))

fill_data = bpy.data.lights.new("fill", "AREA")
fill_data.energy = 18
fill_data.size = mm(80)
fill = bpy.data.objects.new("fill", fill_data)
bpy.context.collection.objects.link(fill)
fill.location = (-mm(70), mm(40), mm(40))

try:
    scene.render.engine = "BLENDER_EEVEE_NEXT"
except Exception:
    scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = 1280
scene.render.resolution_y = 1280
scene.render.filepath = PREVIEW_PATH
scene.render.film_transparent = False
world = bpy.data.worlds.new("world")
scene.world = world
world.use_nodes = True
bg = world.node_tree.nodes.get("Background")
if bg:
    bg.inputs[0].default_value = (0.12, 0.12, 0.14, 1)
    bg.inputs[1].default_value = 0.6
try:
    bpy.ops.render.render(write_still=True)
    print("  wrote", PREVIEW_PATH)
except Exception as e:
    print("  render failed", e)

# save blend
bpy.ops.wm.save_as_mainfile(filepath=BLEND_PATH)
print("saved", BLEND_PATH)
print("DONE", len(records), "parts")
for p in records:
    print(f"  {p['name']:28s}  loc=({p['loc'][0]:+.4f},{p['loc'][1]:+.4f},{p['loc'][2]:+.4f})")

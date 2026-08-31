import bpy, os, tempfile
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.mesh.primitive_cube_add(size=2.2, location=(0,0,0))
obj = bpy.context.object
obj.name = "cube"
path = r"C:\Users\pooh\Documents\s&box projects\marionette-main\tools\_unit_test.fbx"
kwargs_list = [
    dict(apply_unit_scale=False, apply_scale_options="FBX_SCALE_NONE", global_scale=1.0, bake_space_transform=False, axis_forward="-Y", axis_up="Z"),
    dict(apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", global_scale=1.0, bake_space_transform=False, axis_forward="-Y", axis_up="Z"),
    dict(apply_unit_scale=False, apply_scale_options="FBX_SCALE_NONE", global_scale=1.0, bake_space_transform=True, axis_forward="-Y", axis_up="Z"),
]
for i, kw in enumerate(kwargs_list):
    p = path.replace(".fbx", f"_{i}.fbx")
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.fbx(filepath=p, use_selection=True, object_types={"MESH"}, bake_anim=False, add_leaf_bones=False, **kw)
    print("WROTE", p)

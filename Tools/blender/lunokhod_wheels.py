import bpy, bmesh
# «Луноход-1» (§6.3): колёса — отдельные объекты Lunokhod_Wheel_0..7 (FBX Lunokhod_Wheels), чтобы вращать их по пути
# (VesselView.SpinWheels). Разовый проход по parts.blend после lunokhod_lid.py: колесо — острова целиком в его
# габарите (|x| 0,70–0,90, z 0–0,51, ⌀0,51 вокруг оси y ±0,283/±0,845); балки, рычаги и оси (x < 0,69) остаются в
# корпусе. Пивот колеса Unity берёт по центру габарита меша. Пара: FlightPhysics.RoverWheelRadius = 0,255.
AXLES = (-0.845, -0.2833, 0.2833, 0.845)
R = 0.255

def islands(bm):
    bm.verts.ensure_lookup_table()
    seen = set(); out = []
    for v in bm.verts:
        if v.index in seen: continue
        st = [v]; isl = []; seen.add(v.index)
        while st:
            x = st.pop(); isl.append(x)
            for e in x.link_edges:
                y = e.other_vert(x)
                if y.index not in seen: seen.add(y.index); st.append(y)
        out.append(isl)
    return out

o = bpy.data.objects["Lunokhod"]
bm = bmesh.new(); bm.from_mesh(o.data)
wheels = [set() for _ in range(8)]
for isl in islands(bm):
    xs = [abs(v.co.x) for v in isl]; ys = [v.co.y for v in isl]; zs = [v.co.z for v in isl]
    if min(xs) < 0.695 or max(xs) > 0.905 or max(zs) > R * 2 + 0.01 or min(zs) < -0.01: continue
    if (isl[0].co.x > 0) != all(v.co.x > 0 for v in isl): continue
    cy = (min(ys) + max(ys)) / 2
    k = min(range(4), key=lambda j: abs(cy - AXLES[j]))
    if min(ys) < AXLES[k] - R - 0.01 or max(ys) > AXLES[k] + R + 0.01: continue
    wheels[k * 2 + (1 if isl[0].co.x > 0 else 0)].update(v.index for v in isl)
bm.free()
print("LW", [len(w) for w in wheels])

def keep(mesh, idx):
    bm = bmesh.new(); bm.from_mesh(mesh); bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.index not in idx], context='VERTS')
    bm.to_mesh(mesh); bm.free(); mesh.update()

parts = []
for i, w in enumerate(wheels):
    n = f"Lunokhod_Wheel_{i}"
    old = bpy.data.objects.get(n)
    if old: bpy.data.objects.remove(old)
    part = o.copy(); part.data = o.data.copy(); part.name = n; part.data.name = n
    for c in o.users_collection: c.objects.link(part)
    keep(part.data, w)
    parts.append(part)
allw = set().union(*wheels)
keep(o.data, set(range(len(o.data.vertices))) - allw)
for ob in [o] + parts:
    vs = [v.co for v in ob.data.vertices]
    print("LWB", ob.name, len(vs), "x[%.3f,%.3f] y[%.3f,%.3f] z[%.3f,%.3f]" % (
        min(v.x for v in vs), max(v.x for v in vs), min(v.y for v in vs), max(v.y for v in vs),
        min(v.z for v in vs), max(v.z for v in vs)))

import sys
sys.path.insert(0, r"C:\CocosGames\KareSpaceProgram\Tools\blender")
import hulls_lib
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_mainfile()
hulls_lib.export("Lunokhod")
for x in bpy.context.view_layer.objects: x.select_set(False)
for p in parts: p.select_set(True)
bpy.context.view_layer.objects.active = parts[0]
bpy.ops.export_scene.fbx(filepath=r"C:\CocosGames\KareSpaceProgram\Assets\_Project\Models\Lunokhod_Wheels.fbx", use_selection=True,
                         object_types={'MESH'}, bake_space_transform=True, apply_scale_options='FBX_SCALE_ALL',
                         axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True)
print("LW exported")

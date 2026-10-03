import bpy, bmesh, sys
from mathutils import Vector
sys.path.insert(0, r"C:\CocosGames\KareSpaceProgram\Tools\blender")
import hulls_lib
# «Луноход-1» (§6.12), разовый проход по parts.blend:
# 1) колёса на реальную базу 1,7 м (оси ±0,85 и ±0,283; было ±1,105 и ±0,37 — колёса заходили под сложенные
#    трапы КТ на кромке настила r 1,17). Пара: FlightPhysics.RoverHalfBase = 0,85.
# 2) штыревые антенны и мачта — вперёд, за кромку закрытой крышки (y > 0,64), иначе пробивают крышку.
# 3) крышка с подкосами — отдельный объект Lunokhod_Lid_0 (FBX Lunokhod_Lid) в открытом положении; шарнир —
#    y −0,80, z 1,40 вдоль x. Пара: VesselView.DeployHinge (Lunokhod: 0,80 / 1,40 / 162°).
OLD = (-1.105, -0.37, 0.37, 1.105)
NEW = (-0.85, -0.2833, 0.2833, 0.85)
# Антенны: центр острова (x, y) → сдвиг (dx, dy). Мачта (x −0,65, y 0,45) и штыри по центрам из разбора lk_isl.
MOVES = [((-0.65, 0.45), (0, 0.32)), ((0.87, 0.65), (0, 0.08)), ((-0.95, 0.35), (0, 0.40)),
         ((1.0, -0.55), (-0.45, 1.29)), ((0.25, 0.60), (0, 0.14))]

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
lid = set(); moved = [0, 0]
for isl in islands(bm):
    xs = [v.co.x for v in isl]; ys = [v.co.y for v in isl]; zs = [v.co.z for v in isl]
    cx, cy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
    if max(zs) <= 0.56 and abs(cx) >= 0.6:            # колёса и их узлы
        k = min(range(4), key=lambda j: abs(cy - OLD[j]))
        for v in isl: v.co.y += NEW[k] - OLD[k]
        moved[0] += 1
    elif min(zs) >= 1.3 and min(ys) < -0.85 or min(zs) >= 1.3 and max(ys) < -0.5 and (max(zs) - min(zs)) > 0.2 \
            or min(zs) >= 1.33 and abs(abs(cx) - 0.5) < 0.02 and cy < -0.5:   # крышка, подкосы и их концы
        lid.update(v.index for v in isl)
    elif min(zs) >= 1.3:
        for (mx, my), (dx, dy) in MOVES:
            if abs(cx - mx) < 0.13 and abs(cy - my) < 0.13:
                for v in isl: v.co.x += dx; v.co.y += dy
                print("LLM", (round(cx, 2), round(cy, 2)), "->", (round(cx + dx, 2), round(cy + dy, 2)), len(isl))
                moved[1] += 1
                break
bm.to_mesh(o.data); bm.free(); o.data.update()
print("LL wheels", moved[0], "antenna", moved[1], "lid verts", len(lid))

n = "Lunokhod_Lid_0"
old = bpy.data.objects.get(n)
if old: bpy.data.objects.remove(old)
part = o.copy(); part.data = o.data.copy(); part.name = n; part.data.name = n
for c in o.users_collection: c.objects.link(part)
def keep(mesh, idx):
    bm = bmesh.new(); bm.from_mesh(mesh); bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.index not in idx], context='VERTS')
    bm.to_mesh(mesh); bm.free(); mesh.update()
keep(part.data, lid)
keep(o.data, set(range(len(o.data.vertices))) - lid)
for ob in (o, part):
    vs = [v.co for v in ob.data.vertices]
    print("LLB", ob.name, len(vs), "x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (
        min(v.x for v in vs), max(v.x for v in vs), min(v.y for v in vs), max(v.y for v in vs),
        min(v.z for v in vs), max(v.z for v in vs)))
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_mainfile()
hulls_lib.export("Lunokhod")
for x in bpy.context.view_layer.objects: x.select_set(False)
part.select_set(True); bpy.context.view_layer.objects.active = part
bpy.ops.export_scene.fbx(filepath=r"C:\CocosGames\KareSpaceProgram\Assets\_Project\Models\Lunokhod_Lid.fbx", use_selection=True,
                         object_types={'MESH'}, bake_space_transform=True, apply_scale_options='FBX_SCALE_ALL',
                         axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True)
print("LL exported")

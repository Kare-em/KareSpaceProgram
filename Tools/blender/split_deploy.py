import bpy, bmesh, sys, math
sys.path.insert(0, r"C:\CocosGames\KareSpaceProgram\Tools\blender")
import hulls_lib
# Раскладное (§6.12): опоры «Сервейора» и LM, трапы КТ «Луны-17» — отдельными мешами. Из каждой группы в файл идёт
# одна (опора / пара рельсов) в своих координатах корпуса; VesselView ставит копии по кругу и вращает на шарнире.

def islands(bm):
    bm.verts.ensure_lookup_table()
    seen = set(); out = []
    for v in bm.verts:
        if v.index in seen: continue
        st = [v]; isl = []; seen.add(v.index)
        while st:
            x = st.pop(); isl.append(x.index)
            for e in x.link_edges:
                y = e.other_vert(x)
                if y.index not in seen: seen.add(y.index); st.append(y)
        out.append(isl)
    return out

def drop(mesh, keep):
    bm = bmesh.new(); bm.from_mesh(mesh); bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if v.index not in keep], context='VERTS')
    bm.to_mesh(mesh); bm.free(); mesh.update()

def split(name, out, is_part, azimuths):
    """Острова-детали (is_part) раскладываются по ближайшему азимуту опоры; каждая группа — свой объект out_<k>
    в координатах корпуса. Шарнир VesselView находит по азимуту нижних вершин (лапы / концы рельсов)."""
    o = bpy.data.objects[name]
    bm = bmesh.new(); bm.from_mesh(o.data); bm.verts.ensure_lookup_table()
    co = [v.co.copy() for v in bm.verts]
    isl = islands(bm); bm.free()
    part = [i for i in isl if is_part([co[k] for k in i])]
    groups = [set() for _ in azimuths]
    for i in part:
        c = sum((co[k] for k in i), co[0] * 0) / len(i)
        az = math.degrees(math.atan2(c.y, c.x))
        g = min(range(len(azimuths)), key=lambda j: abs((az - azimuths[j] + 180) % 360 - 180))
        groups[g].update(i)
    pv = set(k for i in part for k in i)
    print("SD", name, "islands", len(isl), "part", len(part), "verts", len(co), len(pv), [len(g) for g in groups])
    names = []
    for j, g in enumerate(groups):
        n = "%s_%d" % (out, j)
        old = bpy.data.objects.get(n)
        if old: bpy.data.objects.remove(old)
        leg = o.copy(); leg.data = o.data.copy(); leg.name = n; leg.data.name = n
        for c in o.users_collection: c.objects.link(leg)
        drop(leg.data, g)
        names.append(n)
    drop(o.data, set(range(len(co))) - pv)
    for ob in [o] + [bpy.data.objects[n] for n in names]:
        vs = [v.co for v in ob.data.vertices]
        print("SDB", ob.name, len(vs), "x[%.2f,%.2f] y[%.2f,%.2f] z[%.2f,%.2f]" % (
            min(v.x for v in vs), max(v.x for v in vs), min(v.y for v in vs), max(v.y for v in vs),
            min(v.z for v in vs), max(v.z for v in vs)))
    return names

def export_many(file, names, folder=r"C:\CocosGames\KareSpaceProgram\Assets\_Project\Models"):
    for o in bpy.context.view_layer.objects: o.select_set(False)
    for n in names: bpy.data.objects[n].select_set(True)
    bpy.context.view_layer.objects.active = bpy.data.objects[names[0]]
    bpy.ops.export_scene.fbx(filepath=folder + "\\" + file + ".fbx", use_selection=True, object_types={'MESH'},
                             bake_space_transform=True, apply_scale_options='FBX_SCALE_ALL',
                             axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True)

r = lambda v: math.hypot(v.x, v.y)
# Правила островов — из разбора legs_info/lm_isl. LM: опора с подкосами и лестницей (на передней опоре, az 90);
# площадка у люка (z > 3,05) остаётся на корпусе. Пара: VesselView.DeployHinges (радиус и высота шарнира).
sv = split("Surveyor", "Surveyor_Leg", lambda p: max(r(v) for v in p) > 1.0 and min(v.z for v in p) < 1.0, (90, 210, 330))
lm = split("LM_Descent", "LM_Leg", lambda p: max(r(v) for v in p) > 2.4 and min(v.z for v in p) < 3.05, (0, 90, 180, 270))
kt = split("Luna17_KT", "Luna17_Ramp", lambda p: max(abs(v.x) for v in p) <= 0.95 and min(abs(v.y) for v in p) >= 1.15,
           (90, 270))
bpy.context.preferences.filepaths.save_version = 0
bpy.ops.wm.save_mainfile()
for n in ("Surveyor", "LM_Descent", "Luna17_KT"):
    hulls_lib.export(n)
export_many("Surveyor_Legs", sv); export_many("LM_Legs", lm); export_many("Luna17_Ramps", kt)
print("SD exported")

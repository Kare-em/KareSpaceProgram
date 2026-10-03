# Генератор корпусов ступеней для Kare Space Program (Blender 5.2).
# Ось ракеты +Z (в Unity станет +Y), начало — днище секции (срез сопла), метры, натуральная величина.
# Слоты материалов у всех корпусов одинаковые: 0 Hull, 1 Black, 2 Metal, 3 Nozzle — палитра VesselView.CraftPalette.
import bpy, bmesh, math
from mathutils import Vector, Matrix

H, B, M, N = 0, 1, 2, 3
SLOTS = [("Hull", (0.9, 0.9, 0.88)), ("Black", (0.07, 0.07, 0.07)), ("Metal", (0.55, 0.55, 0.53)), ("Nozzle", (0.2, 0.18, 0.17))]


def lathe(bm, prof, segs=48, mat=H, c=(0.0, 0.0), sharp=35.0, mif=None, smooth=True, closed=False, az0=0.0):
    """Тело вращения. prof — [(r, z)], обход против часовой в плоскости (r, z) => нормали наружу.
    mif(az°, zmid) -> слот материала грани (полосы, сектора)."""
    cx, cy = c
    angs = [math.radians(az0) + 2 * math.pi * j / segs for j in range(segs)]
    rings = []
    for r, z in prof:
        if r < 1e-6:
            rings.append([bm.verts.new((cx, cy, z))])
        else:
            rings.append([bm.verts.new((cx + r * math.cos(a), cy + r * math.sin(a), z)) for a in angs])
    n = len(prof)
    pairs = [(k, k + 1) for k in range(n - 1)] + ([(n - 1, 0)] if closed else [])
    for ka, kb in pairs:
        A, Bv = rings[ka], rings[kb]
        zm = 0.5 * (prof[ka][1] + prof[kb][1])
        for j in range(segs):
            j2 = (j + 1) % segs
            if len(A) == 1 and len(Bv) == 1:
                continue
            if len(A) == 1:
                vs = (A[0], Bv[j2], Bv[j])
            elif len(Bv) == 1:
                vs = (A[j], A[j2], Bv[0])
            else:
                vs = (A[j], A[j2], Bv[j2], Bv[j])
            f = bm.faces.new(vs)
            f.material_index = mif(math.degrees(angs[j]) + 180.0 / segs, zm) if mif else mat
            f.smooth = smooth
    # Острые рёбра на изломах профиля — Blender 4.1+ делит нормали по sharp-рёбрам без autosmooth.
    bm.edges.ensure_lookup_table()
    for k in range(n):
        if not closed and (k == 0 or k == n - 1):
            continue
        p0, p1, p2 = prof[k - 1], prof[k], prof[(k + 1) % n]
        d0 = Vector((p1[0] - p0[0], p1[1] - p0[1]))
        d1 = Vector((p2[0] - p1[0], p2[1] - p1[1]))
        if d0.length < 1e-9 or d1.length < 1e-9 or len(rings[k]) == 1:
            continue
        if math.degrees(d0.angle(d1)) > sharp:
            ring = rings[k]
            for j in range(segs):
                e = bm.edges.get((ring[j], ring[(j + 1) % segs]))
                if e:
                    e.smooth = False


def ring(bm, r, z, w, h, mat=M, segs=48, c=(0.0, 0.0)):
    """Шпангоут/бандаж: тор прямоугольного сечения."""
    lathe(bm, [(r - w / 2, z - h / 2), (r + w / 2, z - h / 2), (r + w / 2, z + h / 2), (r - w / 2, z + h / 2)],
          segs, mat, c, closed=True)


def torus(bm, R, z, rm, mat=M, segs=48, k=12):
    lathe(bm, [(R + rm * math.cos(2 * math.pi * i / k), z + rm * math.sin(2 * math.pi * i / k)) for i in range(k)],
          segs, mat, closed=True, sharp=80)


def sphere(bm, R, z, mat=H, segs=32, k=16, c=(0.0, 0.0)):
    lathe(bm, [(R * math.cos(t), z + R * math.sin(t)) for t in
               [-math.pi / 2 + math.pi * i / k for i in range(k + 1)]], segs, mat, c, sharp=80)


def bell(bm, c, z0, h, re, mat=N, segs=32, chamber=M, wall=0.03):
    """Сопло с камерой: срез на z0, общая высота h, радиус среза re. Внутренняя стенка видна снизу."""
    lb = h * 0.62
    rt = re * 0.32
    rc = rt * 1.7
    zt = z0 + lb
    outer = []
    for i in range(9):
        s = i / 8.0
        r = rt + (re - rt) * (1 - (1 - s) ** 1.7)
        outer.append((r + wall, zt - lb * s))
    outer.reverse()  # от среза вверх к горлу
    top = [(rc + wall, zt + h * 0.12), (rc + wall, zt + h * 0.3), (rc * 0.6, z0 + h), (0.0, z0 + h)]
    inner = [(p[0] - wall, p[1]) for p in outer]
    inner.reverse()  # от горла вниз к срезу
    prof = [(0.0, zt)] + inner + outer + top
    lathe(bm, prof, segs, c=c, mif=lambda az, z: mat if z < zt else chamber, sharp=40)


def box(bm, center, size, mat=M, az=0.0):
    m = Matrix.Translation(center) @ Matrix.Rotation(math.radians(az), 4, 'Z') @ Matrix.Diagonal((size[0], size[1], size[2], 1))
    res = bmesh.ops.create_cube(bm, size=1.0, matrix=m)
    for v in res['verts']:
        for f in v.link_faces:
            f.material_index = mat
            f.smooth = False


def fin(bm, az, pts, t, mat=H):
    """Плоский стабилизатор: pts — контур [(u радиально, z)], толщина t, азимут az°."""
    a = math.radians(az)
    rad = Vector((math.cos(a), math.sin(a), 0))
    tan = Vector((-math.sin(a), math.cos(a), 0))
    up = Vector((0, 0, 1))
    side = []
    for s in (-0.5, 0.5):
        side.append([bm.verts.new(rad * u + up * z + tan * (s * t)) for u, z in pts])
    f0 = bm.faces.new(list(reversed(side[0])))
    f1 = bm.faces.new(side[1])
    fs = [f0, f1]
    n = len(pts)
    for i in range(n):
        i2 = (i + 1) % n
        fs.append(bm.faces.new((side[0][i], side[0][i2], side[1][i2], side[1][i])))
    for f in fs:
        f.material_index = mat
        f.smooth = False
    bmesh.ops.recalc_face_normals(bm, faces=fs)


def strut(bm, p, q, rad, mat=M, segs=6):
    p, q = Vector(p), Vector(q)
    d = q - p
    L = d.length
    if L < 1e-6:
        return
    z = d / L
    x = z.orthogonal().normalized()
    y = z.cross(x)
    ra = [bm.verts.new(p + (x * math.cos(2 * math.pi * j / segs) + y * math.sin(2 * math.pi * j / segs)) * rad) for j in range(segs)]
    rb = [v_.co + d for v_ in ra]
    rb = [bm.verts.new(co) for co in rb]
    for j in range(segs):
        j2 = (j + 1) % segs
        f = bm.faces.new((ra[j], ra[j2], rb[j2], rb[j]))
        f.material_index = mat
        f.smooth = True


def truss(bm, r0, z0, r1, z1, n, rad, mat=M, rings=True):
    """Ферма горячего разделения: зигзаг из 2n стоек между кольцами."""
    for i in range(n):
        a0 = 2 * math.pi * i / n
        a1 = 2 * math.pi * (i + 0.5) / n
        a2 = 2 * math.pi * (i + 1) / n
        pb0 = (r0 * math.cos(a0), r0 * math.sin(a0), z0)
        pt = (r1 * math.cos(a1), r1 * math.sin(a1), z1)
        pb1 = (r0 * math.cos(a2), r0 * math.sin(a2), z0)
        strut(bm, pb0, pt, rad, mat)
        strut(bm, pt, pb1, rad, mat)
    if rings:
        ring(bm, r0, z0, rad * 3, rad * 3, mat)
        ring(bm, r1, z1, rad * 3, rad * 3, mat)


def stripes(sectors, offset=0.0):
    """Секторная раскраска «чёрное/белое» (ролл-паттерн): чётный сектор — Black."""
    def f(az):
        return B if int(((az - offset) % 360.0) / (360.0 / sectors)) % 2 == 0 else H
    return f


def build(name, fn):
    """Собирает модель в объект name, кладёт в коллекцию name, назначает 4 слота."""
    old = bpy.data.objects.get(name)
    if old:
        me_old = old.data
        bpy.data.objects.remove(old, do_unlink=True)
        if me_old and me_old.users == 0:
            bpy.data.meshes.remove(me_old)
    bm = bmesh.new()
    fn(bm)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    # Unity нумерует субмеши FBX по первому появлению материала в порядке граней, а не по слоту, — сортируем,
    # иначе палитра CraftPalette съезжает (у S-II Hull и Black менялись местами).
    bm.faces.sort(key=lambda f: f.material_index)
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    for slot, col in SLOTS:
        mn = name + "_" + slot
        mt = bpy.data.materials.get(mn) or bpy.data.materials.new(mn)
        mt.use_nodes = True
        bsdf = next(nd for nd in mt.node_tree.nodes if nd.type == "BSDF_PRINCIPLED")
        bsdf.inputs["Base Color"].default_value = (*col, 1.0)
        mt.diffuse_color = (*col, 1.0)
        me.materials.append(mt)
    ob = bpy.data.objects.new(name, me)
    col = bpy.data.collections.get(name)
    if col is None:
        col = bpy.data.collections.new(name)
        bpy.context.scene.collection.children.link(col)
    col.objects.link(ob)
    used = sorted({p.material_index for p in me.polygons})
    bb = [ob.matrix_world @ Vector(c) for c in ob.bound_box]
    return {"name": name, "tris": sum(len(p.vertices) - 2 for p in me.polygons), "slots": used,
            "zmin": round(min(v.z for v in bb), 3), "zmax": round(max(v.z for v in bb), 3),
            "rmax": round(max(max(abs(v.x), abs(v.y)) for v in bb), 3)}


def export(name, folder=r"C:\CocosGames\KareSpaceProgram\Assets\_Project\Models"):
    ob = bpy.data.objects[name]
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.export_scene.fbx(filepath=folder + "\\" + name + ".fbx", use_selection=True, object_types={'MESH'},
                             bake_space_transform=True, apply_scale_options='FBX_SCALE_ALL',
                             axis_forward='-Z', axis_up='Y', use_mesh_modifiers=True)


def isolate(names):
    """Показать во вьюпорте только эти объекты (для скриншота)."""
    for o in bpy.context.view_layer.objects:
        o.hide_set(o.name not in names)

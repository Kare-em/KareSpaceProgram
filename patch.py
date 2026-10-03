import io
p='Assets/_Project/Game/BodyRenderer.cs'
s=io.open(p,encoding='utf-8',newline='').read()
nl='\r\n' if '\r\n' in s else '\n'
def L(t): return t.replace('\n',nl)
old=L("""            var mesh = patchMf.sharedMesh;
            mesh.Clear();
            mesh.vertices = verts;""")
new=L("""            // Новый меш, а не Clear() старого: RTAS держит BLAS по объекту Mesh и правку вершин на месте не видит —
            // RT-тени падали от рельефа прежнего места патча (замер 03.10.2026, Луна: чёрные зоны с прямыми краями
            // при честном горизонте 1,5° против Солнца 8,2°; с DynamicGeometry — чисто). Перестройка редкая, так дешевле.
            var oldMesh = patchMf.sharedMesh;
            var mesh = new Mesh { name = "Patch", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = verts;""")
assert s.count(old)==1; s=s.replace(old,new)
old2=L("""            mesh.RecalculateBounds();
            patchMr.sharedMaterials = ocean""")
new2=L("""            mesh.RecalculateBounds();
            patchMf.sharedMesh = mesh;
            if (oldMesh != null) Destroy(oldMesh);
            patchMr.sharedMaterials = ocean""")
assert s.count(old2)==1; s=s.replace(old2,new2)
io.open(p,'w',encoding='utf-8',newline='').write(s)

# Builds the grappling-hook bolt model and the rope textures in Blender.
#
#   blender -b --factory-startup -P unity/blender/build_assets.py -- <source dir> <preview dir>
#
# Outputs into <source dir> (the Unity project's Assets/Source):
#   GrapplingHookBolt.fbx          low-poly bolt, one material slot, empties Tip and RopeAttach
#   hook_<tier>_albedo.png         baked base colour (cavity occlusion included), per tier:
#   hook_<tier>_normal.png         tangent-space normals (OpenGL, MikkTSpace)
#   hook_<tier>_metallic.png       RGB metallic, A smoothness (Unity Standard metallic map)
#                                  <tier> is scrap, iron or steel: one mesh, three finishes
#   hook_occlusion.png             ambient occlusion (shared)
#   rope_albedo.png, rope_normal.png
#                                  one lay length of three-strand rope baked from a
#                                  yarn-level model onto a cylinder: u along, v around
# Preview renders go to <preview dir>.
#
# Blender space: the bolt points along +Y (tip forward), +Z up, metres. The
# Unity build script orients the model by its Tip and RopeAttach empties, so
# the FBX axis conversion does not need to be trusted.

import math
import os
import sys

import bpy
import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SOURCE_DIR = os.path.abspath(argv[0] if len(argv) > 0 else "Assets/Source")
PREVIEW_DIR = os.path.abspath(argv[1] if len(argv) > 1 else SOURCE_DIR)
os.makedirs(SOURCE_DIR, exist_ok=True)
os.makedirs(PREVIEW_DIR, exist_ok=True)

HOOK_TEX = 1024
ROPE_TEX = 512


# ---------------------------------------------------------------- scene utils

def reset_scene():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    for block in (bpy.data.meshes, bpy.data.curves, bpy.data.materials, bpy.data.images,
                  bpy.data.cameras, bpy.data.lights):
        for item in list(block):
            block.remove(item)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 32
    scene.unit_settings.system = "METRIC"
    scene.unit_settings.scale_length = 1.0
    if scene.world is None:
        scene.world = bpy.data.worlds.new("World")
    return scene


def select_only(objs, active=None):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = active or objs[0]


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def mesh_obj(name, verts, faces, material):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    obj = link(bpy.data.objects.new(name, me))
    obj.data.materials.append(material)
    return obj


def lathe(name, profile, segments, material, phase=0.0):
    """Revolves (radius, y) points around the Y axis. Zero radius closes a pole."""
    verts, rings = [], []
    for r, y in profile:
        if r <= 1e-7:
            rings.append([len(verts)])
            verts.append((0.0, y, 0.0))
            continue
        ring = []
        for s in range(segments):
            a = phase + 2 * math.pi * s / segments
            ring.append(len(verts))
            verts.append((r * math.cos(a), y, r * math.sin(a)))
        rings.append(ring)
    faces = []
    for a, b in zip(rings, rings[1:]):
        if len(a) == 1 and len(b) == 1:
            continue
        if len(a) == 1:
            faces += [(a[0], b[(s + 1) % segments], b[s]) for s in range(segments)]
        elif len(b) == 1:
            faces += [(a[s], a[(s + 1) % segments], b[0]) for s in range(segments)]
        else:
            faces += [(a[s], a[(s + 1) % segments], b[(s + 1) % segments], b[s]) for s in range(segments)]
    return mesh_obj(name, verts, faces, material)


def curve_obj(name, points, radii, bevel, material, resolution=8, bevel_res=2):
    cu = bpy.data.curves.new(name, "CURVE")
    cu.dimensions = "3D"
    cu.resolution_u = resolution
    cu.bevel_mode = "ROUND"
    cu.bevel_depth = bevel
    cu.bevel_resolution = bevel_res
    cu.use_fill_caps = True
    sp = cu.splines.new("BEZIER")
    sp.bezier_points.add(len(points) - 1)
    for bp, co, r in zip(sp.bezier_points, points, radii):
        bp.co = co
        bp.handle_left_type = bp.handle_right_type = "AUTO"
        bp.radius = r
    obj = link(bpy.data.objects.new(name, cu))
    obj.data.materials.append(material)
    return obj


def to_mesh(obj):
    select_only([obj])
    bpy.ops.object.convert(target="MESH")
    return bpy.context.view_layer.objects.active


# ------------------------------------------------------------- shader helpers

class Mat:
    """A Principled material whose colour/metal/roughness sources are remembered
    so each can be routed to an Emission shader for baking."""

    def __init__(self, name):
        self.mat = bpy.data.materials.new(name)
        if getattr(self.mat, "node_tree", None) is None:
            self.mat.use_nodes = True
        self.nt = self.mat.node_tree
        self.nt.nodes.clear()
        self.out = self.node("ShaderNodeOutputMaterial")
        self.bsdf = self.node("ShaderNodeBsdfPrincipled")
        self.link(self.bsdf.outputs["BSDF"], self.out.inputs["Surface"])
        self.color = self.metal = self.rough = None
        self.bake_image = self.node("ShaderNodeTexImage")

    def node(self, kind, **props):
        n = self.nt.nodes.new(kind)
        for k, v in props.items():
            setattr(n, k, v)
        return n

    def link(self, a, b):
        self.nt.links.new(a, b)

    def value(self, v):
        n = self.node("ShaderNodeValue")
        n.outputs[0].default_value = v
        return n.outputs[0]

    def rgb(self, c):
        n = self.node("ShaderNodeRGB")
        n.outputs[0].default_value = (*c, 1.0)
        return n.outputs[0]

    def math(self, op, a, b=None):
        n = self.node("ShaderNodeMath", operation=op)
        for i, v in enumerate((a, b)):
            if v is None:
                continue
            if isinstance(v, (int, float)):
                n.inputs[i].default_value = v
            else:
                self.link(v, n.inputs[i])
        return n.outputs[0]

    def mix(self, fac, a, b, data_type="RGBA"):
        n = self.node("ShaderNodeMix", data_type=data_type)
        sock_type = "RGBA" if data_type == "RGBA" else "VALUE"
        ins = [s for s in n.inputs if s.type == sock_type and s.name in ("A", "B")]
        outs = [s for s in n.outputs if s.type == sock_type]
        for sock, v in ((n.inputs["Factor"], fac), (ins[0], a), (ins[1], b)):
            if isinstance(v, (int, float)):
                sock.default_value = v
            elif isinstance(v, tuple):
                sock.default_value = (*v, 1.0)
            else:
                self.link(v, sock)
        return outs[0]

    def ramp(self, fac, lo, hi):
        n = self.node("ShaderNodeMapRange")
        self.link(fac, n.inputs["Value"])
        n.inputs["From Min"].default_value = lo
        n.inputs["From Max"].default_value = hi
        n.clamp = True
        return n.outputs["Result"]

    def coords(self, scale, kind="Object"):
        tc = self.node("ShaderNodeTexCoord")
        mp = self.node("ShaderNodeMapping")
        self.link(tc.outputs[kind], mp.inputs["Vector"])
        mp.inputs["Scale"].default_value = scale
        return mp.outputs["Vector"]

    def noise(self, vec, scale, detail=6.0, roughness=0.6, out="Fac", dims="3D", w=None):
        n = self.node("ShaderNodeTexNoise")
        n.noise_dimensions = dims
        if vec is not None:
            self.link(vec, n.inputs["Vector"])
        if w is not None:
            self.link(w, n.inputs["W"])
        n.inputs["Scale"].default_value = scale
        n.inputs["Detail"].default_value = detail
        n.inputs["Roughness"].default_value = roughness
        return n.outputs[out]

    def bump(self, height, strength, distance):
        n = self.node("ShaderNodeBump")
        self.link(height, n.inputs["Height"])
        n.inputs["Strength"].default_value = strength
        n.inputs["Distance"].default_value = distance
        self.link(n.outputs["Normal"], self.bsdf.inputs["Normal"])

    def ao(self, distance):
        n = self.node("ShaderNodeAmbientOcclusion")
        n.inputs["Distance"].default_value = distance
        n.samples = 16
        return n.outputs["AO"]

    def finish(self, color, metal, rough):
        self.color, self.metal, self.rough = color, metal, rough
        for sock, v in ((self.bsdf.inputs["Base Color"], color),
                        (self.bsdf.inputs["Metallic"], metal),
                        (self.bsdf.inputs["Roughness"], rough)):
            if isinstance(v, (int, float)):
                sock.default_value = v
            elif isinstance(v, tuple):
                sock.default_value = (*v, 1.0)
            else:
                self.link(v, sock)
        return self

    def route_emission(self, source):
        """Points the surface at an emission of the given source (or the BSDF if None)."""
        for l in list(self.out.inputs["Surface"].links):
            self.nt.links.remove(l)
        if source is None:
            self.link(self.bsdf.outputs["BSDF"], self.out.inputs["Surface"])
            return
        em = self.node("ShaderNodeEmission")
        if isinstance(source, (int, float)):
            em.inputs["Color"].default_value = (source, source, source, 1.0)
        elif isinstance(source, tuple):
            em.inputs["Color"].default_value = (*source, 1.0)
        else:
            self.link(source, em.inputs["Color"])
        self.link(em.outputs["Emission"], self.out.inputs["Surface"])


def set_bake_target(mats, image):
    for m in mats:
        m.bake_image.image = image
        m.nt.nodes.active = m.bake_image


def new_image(name, size, data):
    w, h = size if isinstance(size, tuple) else (size, size)
    img = bpy.data.images.new(name, w, h, alpha=True, float_buffer=False)
    img.colorspace_settings.name = "Non-Color" if data else "sRGB"
    return img


def pixels(img):
    arr = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(arr)
    return arr.reshape(img.size[1], img.size[0], 4)


def save_image(img, path, arr=None):
    if arr is not None:
        img.pixels.foreach_set(np.clip(arr, 0, 1).astype(np.float32).ravel())
    img.filepath_raw = path
    img.file_format = "PNG"
    img.save()
    print("[assets] wrote", path)


def bake(kind, margin=8, **kw):
    bpy.ops.object.bake(type=kind, margin=margin, use_clear=True, target="IMAGE_TEXTURES", **kw)


# -------------------------------------------------------------- hook materials

# One mesh, three finishes. Each tier sets how much rust and grime the metal
# carries, how bright and smooth it is, and what the grip looks like.
TIERS = {
    "scrap": dict(
        steel_dark=(0.20, 0.17, 0.14), steel_light=(0.36, 0.32, 0.27), blotch=(0.16, 0.12, 0.09),
        rust_lo=0.44, rust_hi=0.58, grime=0.9, metal=0.55, rough=0.46,
        grip_dark=(0.10, 0.075, 0.05), grip_light=(0.24, 0.18, 0.12), grip_dust=(0.30, 0.24, 0.17), grip_rough=0.86),
    "iron": dict(
        steel_dark=(0.44, 0.44, 0.45), steel_light=(0.62, 0.62, 0.63), blotch=(0.36, 0.35, 0.33),
        rust_lo=0.66, rust_hi=0.74, grime=0.6, metal=0.82, rough=0.28,
        grip_dark=(0.045, 0.045, 0.045), grip_light=(0.11, 0.105, 0.10), grip_dust=(0.24, 0.21, 0.17), grip_rough=0.72),
    "steel": dict(
        # Lower metallic than iron on purpose: a bright diffuse silver stays
        # light under any reflections, so the tiers read apart at a glance.
        steel_dark=(0.70, 0.71, 0.73), steel_light=(0.90, 0.91, 0.93), blotch=(0.64, 0.65, 0.67),
        rust_lo=0.86, rust_hi=0.92, grime=0.2, metal=0.5, rough=0.2,
        grip_dark=(0.03, 0.03, 0.032), grip_light=(0.07, 0.07, 0.075), grip_dust=(0.12, 0.115, 0.11), grip_rough=0.62),
}


def steel_material(t):
    """Tool steel for one tier: brushed along the bolt, grime and dried rust in
    the cavities, rust specks; bare edges stay bright."""
    m = Mat("Steel")
    vec = m.coords((1.0, 1.0, 1.0))
    sep = m.node("ShaderNodeSeparateXYZ")
    m.link(vec, sep.inputs[0])
    comb = m.node("ShaderNodeCombineXYZ")
    m.link(m.math("MULTIPLY", sep.outputs[0], 2400.0), comb.inputs[0])
    m.link(m.math("MULTIPLY", sep.outputs[1], 40.0), comb.inputs[1])
    m.link(m.math("MULTIPLY", sep.outputs[2], 2400.0), comb.inputs[2])
    brushed = m.noise(comb.outputs[0], 1.0, detail=3.0, roughness=0.5)
    blotch = m.noise(vec, 90.0, detail=6.0, roughness=0.6)
    specks = m.ramp(m.noise(vec, 260.0, detail=8.0, roughness=0.7), t["rust_lo"], t["rust_hi"])
    cavity = m.ao(0.004)
    grime = m.ramp(m.math("SUBTRACT", 1.0, cavity), 0.15, 0.55)
    rust = m.math("MAXIMUM", specks, m.math("MULTIPLY", grime, t["grime"]))

    steel = m.mix(m.ramp(brushed, 0.3, 0.7), t["steel_dark"], t["steel_light"])
    steel = m.mix(m.math("MULTIPLY", m.ramp(blotch, 0.45, 0.8), 0.6), steel, t["blotch"])
    rust_col = m.mix(m.ramp(blotch, 0.3, 0.7), (0.26, 0.12, 0.05), (0.40, 0.21, 0.09))
    color = m.mix(rust, steel, rust_col)
    color = m.mix(m.math("MULTIPLY", grime, 0.9 * t["grime"]), color, (0.10, 0.08, 0.06))

    # Even the steel tier is not mirror chrome: it keeps some diffuse light so
    # it doesn't go black where the game has dull reflection probes.
    metal = m.math("SUBTRACT", t["metal"], m.math("MULTIPLY", rust, t["metal"] * 0.9))
    rough = m.math("ADD", m.math("ADD", m.math("MULTIPLY", brushed, 0.12), t["rough"]),
                   m.math("MULTIPLY", rust, 0.5))
    pits = m.noise(vec, 1800.0, detail=4.0)
    m.bump(m.math("ADD", m.math("MULTIPLY", brushed, 0.3), m.math("MULTIPLY", pits, rust)), 0.25, 0.0004)
    return m.finish(color, metal, rough)


def rubber_material(t):
    """Grip for one tier: black rubber on iron and steel, a grubby wrap on
    scrap; dust settles in the ribs."""
    m = Mat("Rubber")
    vec = m.coords((1.0, 1.0, 1.0))
    n = m.noise(vec, 1500.0, detail=6.0)
    wear = m.ramp(m.noise(vec, 140.0, detail=5.0), 0.6, 0.8)
    dust = m.ramp(m.math("SUBTRACT", 1.0, m.ao(0.003)), 0.1, 0.5)
    color = m.mix(wear, t["grip_dark"], t["grip_light"])
    color = m.mix(m.math("MULTIPLY", dust, 0.7), color, t["grip_dust"])
    m.bump(n, 0.2, 0.0002)
    return m.finish(color, 0.0, m.math("ADD", m.math("MULTIPLY", n, 0.15), t["grip_rough"]))


def apply_finish(hook, tier):
    """Swaps the hook's two procedural materials for the given tier's."""
    t = TIERS[tier]
    mats = [steel_material(t), rubber_material(t)]
    for i, slot in enumerate(hook.data.materials):
        for m in mats:
            if slot.name.split(".")[0] == m.mat.name.split(".")[0]:
                hook.data.materials[i] = m.mat
    return mats


# ------------------------------------------------------------------ hook model

SHANK_R = 0.0055
TAIL_Y = -0.436          # rear end of the shank
NOSE_Y = 0.040           # front of the domed nose
CLAWS = 4


def catmull_rom(points, samples):
    """Evenly parameterised Catmull-Rom through the points (end points repeated)."""
    pts = [points[0]] + list(points) + [points[-1]]
    out = []
    segs = len(points) - 1
    for i in range(samples + 1):
        t = i / samples * segs
        k = min(int(t), segs - 1)
        u = t - k
        p0, p1, p2, p3 = pts[k], pts[k + 1], pts[k + 2], pts[k + 3]
        out.append(tuple(
            0.5 * ((2 * b) + (-a + c) * u + (2 * a - 5 * b + 4 * c - d) * u * u +
                   (-a + 3 * b - 3 * c + d) * u ** 3)
            for a, b, c, d in zip(p0, p1, p2, p3)))
    return out


def claw(name, phi, material):
    """A flat forged claw in the radial plane at angle phi: out from the hub,
    over, and back toward the tail, with serrations on the inner (gripping)
    edge and a sharpened point."""
    centre = catmull_rom([(0.0045, 0.012), (0.0160, 0.0225), (0.0315, 0.0205), (0.0430, 0.0050),
                          (0.0465, -0.0150), (0.0430, -0.0340), (0.0355, -0.0470)], 84)
    n = len(centre)
    er = (math.cos(phi), 0.0, math.sin(phi))
    et = (-math.sin(phi), 0.0, math.cos(phi))
    teeth, t0, t1, depth = 8, 0.40, 0.86, 0.0013
    verts = []
    for i, (r, y) in enumerate(centre):
        s = i / (n - 1)
        a = centre[max(i - 1, 0)]
        b = centre[min(i + 1, n - 1)]
        dr, dy = b[0] - a[0], b[1] - a[1]
        ln = math.hypot(dr, dy) or 1.0
        # (-dy, dr) is the convex (outer) side along the whole curve.
        nr, ny = -dy / ln, dr / ln
        width = 0.0088 * (1.0 - s) ** 0.55 if s < 1.0 else 0.0
        half_t = 0.0015 * (1.0 - 0.6 * s ** 2)
        inner = width * 0.5
        if t0 <= s <= t1:
            phase = (s - t0) / (t1 - t0) * teeth
            inner -= depth * (phase - math.floor(phase))       # saw teeth raking toward the tip
        outer = width * 0.5
        for side in (outer, -inner):
            pr, py = r + nr * side, y + ny * side
            for tau in (half_t, -half_t):
                verts.append((pr * er[0] + tau * et[0], py, pr * er[2] + tau * et[2]))
    faces = []
    for i in range(n - 1):
        o0, o1 = 4 * i, 4 * (i + 1)
        # vertex order per ring: outer+, outer-, inner+, inner-
        faces.append((o0 + 0, o1 + 0, o1 + 1, o0 + 1))   # outer edge
        faces.append((o0 + 3, o1 + 3, o1 + 2, o0 + 2))   # inner edge
        faces.append((o0 + 2, o1 + 2, o1 + 0, o0 + 0))   # + face
        faces.append((o0 + 1, o1 + 1, o1 + 3, o0 + 3))   # - face
    faces.append((0, 1, 3, 2))
    return mesh_obj(name, verts, faces, material)


def pin(name, centre, axis, length, radius, material):
    h = length / 2
    p0 = tuple(c - a * h for c, a in zip(centre, axis))
    p1 = tuple(c + a * h for c, a in zip(centre, axis))
    return curve_obj(name, [p0, p1], [1.0, 1.0], radius, material, resolution=1, bevel_res=2)


def build_hook():
    steel, rubber = steel_material(TIERS["iron"]), rubber_material(TIERS["iron"])
    mats = [steel, rubber]
    parts = []

    # Shank: a steel tube from the tail cap to the hub.
    parts.append(lathe("Shank", [(SHANK_R, TAIL_Y), (SHANK_R, -0.012)], 16, steel.mat))

    # Ribbed rubber grip on the rear of the shank.
    g0, g1 = TAIL_Y + 0.014, TAIL_Y + 0.200
    prof = [(SHANK_R * 0.98, g0), (0.0068, g0 + 0.002)]
    y = g0 + 0.004
    while y < g1 - 0.010:
        prof += [(0.0068, y), (0.0074, y + 0.0015), (0.0074, y + 0.0045), (0.0068, y + 0.006)]
        y += 0.006
    prof += [(0.0068, y + 0.002), (SHANK_R * 0.98, y + 0.004)]
    parts.append(lathe("Grip", prof, 16, rubber.mat))

    # Tail cap and the rope eye.
    parts.append(lathe("TailCap", [(0.0, TAIL_Y - 0.006), (0.0048, TAIL_Y - 0.006), (0.0062, TAIL_Y - 0.003),
                                   (0.0062, TAIL_Y + 0.004), (SHANK_R * 0.98, TAIL_Y + 0.006)], 16, steel.mat))
    eye_r, wire_r = 0.0075, 0.0019
    eye_y = TAIL_Y - 0.006 - eye_r + wire_r * 0.6
    pts = [(0.0, eye_y + eye_r * math.cos(a), eye_r * 1.15 * math.sin(a))
           for a in (2 * math.pi * i / 16 for i in range(16))]
    eye = curve_obj("Eye", pts, [1.0] * 16, wire_r, steel.mat, resolution=3, bevel_res=2)
    eye.data.splines[0].use_cyclic_u = True
    parts.append(eye)

    # Hub: collar, body and a domed nose.
    parts.append(lathe("Collar", [(SHANK_R * 0.98, -0.016), (0.0072, -0.015), (0.0078, -0.012),
                                  (0.0078, -0.007), (0.0072, -0.005)], 16, steel.mat))
    parts.append(lathe("Hub", [(0.0, -0.006), (0.0082, -0.006), (0.0088, -0.003), (0.0088, 0.022),
                               (0.0084, 0.027), (0.0072, 0.032), (0.0052, 0.0365), (0.0028, 0.0392),
                               (0.0, NOSE_Y)], 20, steel.mat))

    # Claws hinge on pins through the hub.
    for k in range(CLAWS):
        phi = math.pi / 4 + k * 2 * math.pi / CLAWS
        parts.append(claw("Claw", phi, steel.mat))
        er = (math.cos(phi), 0.0, math.sin(phi))
        et = (-math.sin(phi), 0.0, math.cos(phi))
        c = (er[0] * 0.0078, 0.0125, er[2] * 0.0078)
        parts.append(pin("Pin", c, et, 0.0062, 0.0013, steel.mat))

    meshes = [to_mesh(p) if p.type == "CURVE" else p for p in parts]
    select_only(meshes, meshes[0])
    bpy.ops.object.join()
    hook = bpy.context.view_layer.objects.active
    hook.name = "GrapplingHookBolt"
    hook.data.name = "GrapplingHookBolt"

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.remove_doubles(threshold=0.00002)
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.004)
    bpy.ops.uv.pack_islands(margin=0.004)
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.shade_smooth_by_angle(angle=math.radians(38))

    tip = link(bpy.data.objects.new("Tip", None))
    tip.location = (0.0, NOSE_Y, 0.0)
    attach = link(bpy.data.objects.new("RopeAttach", None))
    attach.location = (0.0, eye_y - eye_r, 0.0)
    tris = sum(len(p.vertices) - 2 for p in hook.data.polygons)
    print("[assets] hook triangles:", tris)
    return hook, mats, tip, attach


def bake_hook(hook, mats, tier, bake_occlusion):
    select_only([hook])
    scene = bpy.context.scene
    scene.cycles.samples = 32

    albedo = new_image("hook_albedo_" + tier, HOOK_TEX, False)
    set_bake_target(mats, albedo)
    bake("DIFFUSE", pass_filter={"COLOR"})

    normal = new_image("hook_normal_" + tier, HOOK_TEX, True)
    set_bake_target(mats, normal)
    bake("NORMAL", normal_space="TANGENT")

    rough = new_image("hook_rough_" + tier, HOOK_TEX, True)
    set_bake_target(mats, rough)
    bake("ROUGHNESS")

    metal = new_image("hook_metal_" + tier, HOOK_TEX, True)
    set_bake_target(mats, metal)
    for m in mats:
        m.route_emission(m.metal)
    bake("EMIT")
    for m in mats:
        m.route_emission(None)

    if bake_occlusion:
        # Geometry only, so one map serves every tier.
        scene.cycles.samples = 64
        occl = new_image("hook_occlusion", HOOK_TEX, True)
        set_bake_target(mats, occl)
        bake("AO")
        save_image(occl, os.path.join(SOURCE_DIR, "hook_occlusion.png"))

    save_image(albedo, os.path.join(SOURCE_DIR, "hook_%s_albedo.png" % tier))
    save_image(normal, os.path.join(SOURCE_DIR, "hook_%s_normal.png" % tier))
    ms = pixels(metal).copy()
    ms[..., 3] = 1.0 - pixels(rough)[..., 0]
    save_image(metal, os.path.join(SOURCE_DIR, "hook_%s_metallic.png" % tier), ms)
    return albedo, normal, metal


def baked_material(name, albedo, normal, metal=None, rough_const=0.6):
    """Preview material wired from baked images, to check the bake in a render."""
    m = Mat(name)
    tc = m.node("ShaderNodeTexCoord")
    a = m.node("ShaderNodeTexImage")
    a.image = albedo
    m.link(tc.outputs["UV"], a.inputs["Vector"])
    m.link(a.outputs["Color"], m.bsdf.inputs["Base Color"])
    nimg = m.node("ShaderNodeTexImage")
    nimg.image = normal
    m.link(tc.outputs["UV"], nimg.inputs["Vector"])
    nm = m.node("ShaderNodeNormalMap")
    m.link(nimg.outputs["Color"], nm.inputs["Color"])
    m.link(nm.outputs["Normal"], m.bsdf.inputs["Normal"])
    if metal is not None:
        mimg = m.node("ShaderNodeTexImage")
        mimg.image = metal
        m.link(tc.outputs["UV"], mimg.inputs["Vector"])
        sep = m.node("ShaderNodeSeparateColor")
        m.link(mimg.outputs["Color"], sep.inputs["Color"])
        m.link(sep.outputs[0], m.bsdf.inputs["Metallic"])
        m.link(m.math("SUBTRACT", 1.0, mimg.outputs["Alpha"]), m.bsdf.inputs["Roughness"])
    else:
        m.bsdf.inputs["Roughness"].default_value = rough_const
    return m.mat


def export_hook(hook, tip, attach, baked):
    hook.data.materials.clear()
    hook.data.materials.append(baked)
    for o in (tip, attach):
        o.parent = hook
    select_only([hook, tip, attach], hook)
    path = os.path.join(SOURCE_DIR, "GrapplingHookBolt.fbx")
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=True, object_types={"MESH", "EMPTY"},
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_UNITS",
        axis_forward="-Z", axis_up="Y", bake_space_transform=True,
        mesh_smooth_type="FACE", use_tspace=True, add_leaf_bones=False,
        use_mesh_modifiers=True, path_mode="STRIP")
    print("[assets] wrote", path)


# ------------------------------------------------------------------------ rope

ROPE_R = 0.008
LAY = 0.054              # one lay length along the rope (the texture's u span)


def rope_material():
    m = Mat("Yarn")
    info = m.node("ShaderNodeObjectInfo")
    tc = m.node("ShaderNodeTexCoord")
    sep = m.node("ShaderNodeSeparateXYZ")
    m.link(tc.outputs["Object"], sep.inputs[0])
    # Fibres: 4D noise, fast across the rope and periodic along it (via the
    # angle 2πz/LAY), so the baked tile wraps without a seam.
    ang = m.math("MULTIPLY", sep.outputs[2], 2 * math.pi / LAY)
    comb = m.node("ShaderNodeCombineXYZ")
    m.link(m.math("MULTIPLY", sep.outputs[0], 2600.0), comb.inputs[0])
    m.link(m.math("MULTIPLY", sep.outputs[1], 2600.0), comb.inputs[1])
    m.link(m.math("MULTIPLY", m.math("COSINE", ang), 2.2), comb.inputs[2])
    w = m.math("MULTIPLY", m.math("SINE", ang), 2.2)
    fibre = m.noise(comb.outputs[0], 1.0, detail=6.0, roughness=0.7, dims="4D", w=w)
    yarn_tint = m.math("ADD", m.math("MULTIPLY", info.outputs["Random"], 0.30), 0.85)
    base = m.mix(m.ramp(fibre, 0.3, 0.75), (0.40, 0.30, 0.18), (0.72, 0.58, 0.39))
    tint = m.node("ShaderNodeCombineColor")
    for i in range(3):
        m.link(yarn_tint, tint.inputs[i])
    tinted = m.node("ShaderNodeMix", data_type="RGBA", blend_type="MULTIPLY")
    tinted.inputs["Factor"].default_value = 1.0
    rgba = [s for s in tinted.inputs if s.type == "RGBA"]
    m.link(base, rgba[0])
    m.link(tint.outputs[0], rgba[1])
    color = [s for s in tinted.outputs if s.type == "RGBA"][0]
    m.bump(fibre, 0.45, 0.00025)
    # Height for groove shading: distance from the rope axis, 0 deep in the
    # grooves, 1 on the crown of a strand.
    flat = m.node("ShaderNodeCombineXYZ")
    m.link(sep.outputs[0], flat.inputs[0])
    m.link(sep.outputs[1], flat.inputs[1])
    length = m.node("ShaderNodeVectorMath", operation="LENGTH")
    m.link(flat.outputs[0], length.inputs[0])
    m.height = m.ramp(length.outputs["Value"], 0.55 * ROPE_R, 1.0 * ROPE_R)
    return m.finish(color, 0.0, 0.85)


def build_rope(mat):
    """Three strands, each of seven yarns twisted the opposite way; the model
    spans three lay lengths so the middle one has full neighbours."""
    rs = 0.47 * ROPE_R              # strand radius
    a = 0.536 * ROPE_R              # strand centre offset from the rope axis
    ry = 0.40 * rs                  # yarn radius; yarns overlap so their grooves stay shallow
    b = rs - ry                     # yarn centre offset inside the strand
    z0, z1 = -LAY, 2 * LAY
    steps = 3 * 96
    w = 2 * math.pi / LAY           # strand turn rate
    objs = []
    for k in range(3):
        for j in range(7):
            pts = []
            for i in range(steps + 1):
                z = z0 + (z1 - z0) * i / steps
                th = w * z + k * 2 * math.pi / 3
                cx, cy = a * math.cos(th), a * math.sin(th)
                if j == 6:
                    pts.append((cx, cy, z))
                    continue
                # Yarn helix around the strand centre, opposite hand, in the
                # strand's own frame: N radial, T along the strand, B = T × N.
                nx, ny = math.cos(th), math.sin(th)
                tx, ty, tz = -a * w * math.sin(th), a * w * math.cos(th), 1.0
                tl = math.sqrt(tx * tx + ty * ty + tz * tz)
                tx, ty, tz = tx / tl, ty / tl, tz / tl
                bx, by, bz = ty * 0.0 - tz * ny, tz * nx - tx * 0.0, tx * ny - ty * nx
                ps = -w * z + j * 2 * math.pi / 6
                c, s = math.cos(ps), math.sin(ps)
                pts.append((cx + b * (c * nx + s * bx), cy + b * (c * ny + s * by), z + b * s * bz))
            o = curve_obj("Yarn", pts, [1.0] * len(pts), ry * 1.08, mat.mat, resolution=1, bevel_res=2)
            objs.append(o)
    return objs


def build_rope_target():
    """Cylinder covering one lay length; u = z / LAY, v = angle / 2π."""
    seg_a, seg_z = 64, 32
    verts, faces, uvs = [], [], []
    for iz in range(seg_z + 1):
        for ia in range(seg_a + 1):
            ang = 2 * math.pi * ia / seg_a
            verts.append((ROPE_R * math.cos(ang), ROPE_R * math.sin(ang), LAY * iz / seg_z))
    row = seg_a + 1
    for iz in range(seg_z):
        for ia in range(seg_a):
            v0 = iz * row + ia
            faces.append((v0, v0 + 1, v0 + row + 1, v0 + row))
    me = bpy.data.meshes.new("RopeTarget")
    me.from_pydata(verts, [], faces)
    uv = me.uv_layers.new(name="UVMap")
    for poly in me.polygons:
        for li in poly.loop_indices:
            vi = me.loops[li].vertex_index
            iz, ia = divmod(vi, row)
            uv.data[li].uv = (iz / seg_z, ia / seg_a)
    me.update()
    obj = link(bpy.data.objects.new("RopeTarget", me))
    for p in obj.data.polygons:
        p.use_smooth = True
    return obj


def bake_rope():
    yarn = rope_material()
    yarns = build_rope(yarn)
    target = build_rope_target()
    target_mat = Mat("RopeTarget")
    target.data.materials.append(target_mat.mat)
    scene = bpy.context.scene
    scene.cycles.samples = 32

    def bake_to(name, data, kind, **kw):
        img = new_image(name, ROPE_TEX, data)
        set_bake_target([target_mat], img)
        select_only(yarns + [target], target)
        bake(kind, use_selected_to_active=True, cage_extrusion=0.004,
             max_ray_distance=0.02, margin=0, **kw)
        return img

    color = bake_to("rope_color", False, "DIFFUSE", pass_filter={"COLOR"})
    normal = bake_to("rope_normal", True, "NORMAL", normal_space="TANGENT")
    # Groove shading from the height (distance from the axis) rather than an
    # AO bake, which came out black along one edge of the tile.
    yarn.route_emission(yarn.height)
    height = bake_to("rope_height", True, "EMIT")
    yarn.route_emission(None)
    if os.environ.get("ASSETS_DEBUG"):
        save_image(color, os.path.join(PREVIEW_DIR, "debug_rope_color.png"))
        save_image(height, os.path.join(PREVIEW_DIR, "debug_rope_height.png"))
    albedo = pixels(color).copy()
    h = pixels(height)[..., :1]
    cavity = h * h * (3 - 2 * h)
    albedo[..., :3] *= 0.32 + 0.68 * cavity
    albedo[..., 3] = 1.0
    save_image(color, os.path.join(SOURCE_DIR, "rope_albedo.png"), albedo)
    save_image(normal, os.path.join(SOURCE_DIR, "rope_normal.png"))
    for o in yarns:
        bpy.data.objects.remove(o, do_unlink=True)
    return color, normal, target


# --------------------------------------------------------------------- preview

def preview(hook, baked, rope_target, rope_albedo, rope_normal):
    scene = bpy.context.scene
    scene.cycles.samples = 96
    scene.render.film_transparent = True
    scene.render.resolution_x = scene.render.resolution_y = 640
    # A sky-to-ground gradient so metal has something to reflect.
    world = scene.world
    if getattr(world, "node_tree", None) is None:
        world.use_nodes = True
    wn = world.node_tree.nodes
    wn.clear()
    out = wn.new("ShaderNodeOutputWorld")
    bg = wn.new("ShaderNodeBackground")
    bg.inputs["Strength"].default_value = 1.0
    tc = wn.new("ShaderNodeTexCoord")
    sep = wn.new("ShaderNodeSeparateXYZ")
    mr = wn.new("ShaderNodeMapRange")
    mr.inputs["From Min"].default_value = -0.3
    mr.inputs["From Max"].default_value = 0.6
    mix = wn.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    rgba = [s for s in mix.inputs if s.type == "RGBA"]
    rgba[0].default_value = (0.16, 0.14, 0.12, 1.0)
    rgba[1].default_value = (0.85, 0.88, 0.95, 1.0)
    wl = world.node_tree.links
    wl.new(tc.outputs["Generated"], sep.inputs[0])
    wl.new(sep.outputs[2], mr.inputs["Value"])
    wl.new(mr.outputs["Result"], mix.inputs["Factor"])
    wl.new([s for s in mix.outputs if s.type == "RGBA"][0], bg.inputs["Color"])
    wl.new(bg.outputs["Background"], out.inputs["Surface"])
    sun = link(bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN")))
    sun.data.energy = 4.0
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(35))
    cam = link(bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam")))
    scene.camera = cam

    def shot(name, location, target, lens=50):
        cam.location = location
        d = [t - l for t, l in zip(target, location)]
        cam.rotation_euler = (math.atan2(math.hypot(d[0], d[1]), -d[2]), 0.0, math.atan2(d[1], d[0]) - math.pi / 2)
        cam.data.lens = lens
        cam.data.clip_start = 0.001
        scene.render.filepath = os.path.join(PREVIEW_DIR, name)
        bpy.ops.render.render(write_still=True)
        print("[assets] preview", scene.render.filepath)

    rope_target.hide_render = True
    for tier, material in baked.items():
        hook.data.materials.clear()
        hook.data.materials.append(material)
        shot("hook_side_%s.png" % tier, (0.0, -0.20, 0.75), (0.0, -0.20, 0.0), lens=55)
        shot("hook_head_%s.png" % tier, (0.13, 0.17, 0.10), (0.0, 0.01, 0.0), lens=70)

    hook.hide_render = True
    rope_target.hide_render = False
    rope_target.data.materials.clear()
    rope_target.data.materials.append(baked_material("RopePreview", rope_albedo, rope_normal, rough_const=0.85))
    # Repeat the tile along the rope for the preview.
    rope_target.scale = (1.0, 1.0, 1.0)
    for i in range(1, 4):
        dup = rope_target.copy()
        dup.location = (0.0, 0.0, LAY * i)
        link(dup)
    shot("rope.png", (0.09, 0.0, LAY * 2), (0.0, 0.0, LAY * 2), lens=50)


def main():
    reset_scene()
    hook, mats, tip, attach = build_hook()
    baked = {}
    for tier in TIERS:
        mats = apply_finish(hook, tier)
        albedo, normal, metal = bake_hook(hook, mats, tier, bake_occlusion=not baked)
        baked[tier] = baked_material("GrapplingHookBolt_" + tier, albedo, normal, metal)
    rope_albedo, rope_normal, rope_target = bake_rope()
    export_hook(hook, tip, attach, baked["iron"])
    preview(hook, baked, rope_target, rope_albedo, rope_normal)
    print("[assets] done")


main()

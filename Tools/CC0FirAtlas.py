"""Build the Grand Tour conifer textures from Poly Haven's CC0 fir_tree_01.

Stamps the scan's twig cut-outs along a stem into two dense branch sprites
(left/right halves of a 1024 RGBA atlas, trunk end at the bottom) and copies
the bark set.  usage: python -I make_fir_atlas.py <download_dir> <repo_root>
"""
import hashlib, json, math, os, random, sys, urllib.request
from PIL import Image, ImageDraw, ImageFilter

DL, REPO = sys.argv[1], sys.argv[2]
OUT = os.path.join(REPO, "Assets", "FamilyRacing", "Art", "CC0", "PolyHaven")
BASE = "https://dl.polyhaven.org/file/ph-assets/Models/jpg/1k/fir_tree_01/fir_tree_01_"
UA = {"User-Agent": "FamilyRacing-asset-fetch/1.0"}


def fetch(name):
    path = os.path.join(DL, "fir_tree_01_" + name + "_1k.jpg")
    if not os.path.exists(path):
        with urllib.request.urlopen(urllib.request.Request(BASE + name + "_1k.jpg", headers=UA)) as r:
            open(path, "wb").write(r.read())
    return path


diff = Image.open(fetch("twig_diff")).convert("RGB")
alpha = Image.open(fetch("twig_alpha")).convert("L")
twig_rgba = diff.copy()
twig_rgba.putalpha(alpha)
# Twig cut-outs in the scan atlas (pixel boxes, stem at the bottom edge).
BOXES = [(313, 410, 659, 793), (650, 463, 974, 849), (669, 53, 959, 377), (194, 48, 425, 318)]
twigs = [twig_rgba.crop(b) for b in BOXES]
bark = Image.open(fetch("bark_diff")).convert("RGB")
stem_colour = tuple(int(c * .8) for c in bark.resize((1, 1), Image.LANCZOS).getpixel((0, 0)))


def branch(seed):
    """512x1024 sprite: stem from bottom centre to the tip, twigs both sides."""
    rnd = random.Random(seed)
    sprite = Image.new("RGBA", (512, 1024), stem_colour + (0,))
    draw = ImageDraw.Draw(sprite)
    draw.line([(256, 1024), (256, 60)], fill=stem_colour + (255,), width=9)
    draw.line([(256, 600), (256, 40)], fill=stem_colour + (255,), width=5)
    # Bottom (near the trunk) is sparser and darker; the tip is fresh growth.
    n = 30
    for k in range(n):
        t = k / (n - 1)
        y = 1000 - t * 900
        side = -1 if k % 2 else 1
        size = 330 - 150 * t + rnd.uniform(-30, 30)
        twig = rnd.choice(twigs)
        scale = size / twig.height
        tw = twig.resize((max(8, int(twig.width * scale)), max(8, int(twig.height * scale))), Image.LANCZOS)
        # Twigs point outward and toward the tip (PIL rotates counter-clockwise).
        angle = -side * rnd.uniform(48, 70) * (1 - .35 * t)
        h = tw.height
        tw = tw.rotate(angle, resample=Image.BICUBIC, expand=True)
        shade = .72 + .28 * t + rnd.uniform(-.05, .05)
        r, g, b, a = tw.split()
        tw = Image.merge("RGBA", [c.point(lambda v, s=shade: min(255, int(v * s))) for c in (r, g, b)] + [a])
        # Put the twig's base (bottom centre before rotating) on the stem.
        rad = math.radians(angle)
        cx = 256 - math.sin(rad) * h / 2
        cy = y - math.cos(rad) * h / 2
        sprite.alpha_composite(tw, (int(cx - tw.width / 2), int(cy - tw.height / 2)))
    tip = twigs[0].resize((int(twigs[0].width * .55), int(twigs[0].height * .55)), Image.LANCZOS)
    sprite.alpha_composite(tip, (256 - tip.width // 2, 20))
    return sprite


atlas = Image.new("RGBA", (1024, 1024))
atlas.alpha_composite(branch(7), (0, 0))
atlas.alpha_composite(branch(19), (512, 0))
# Bleed colour into transparent texels so mips do not fringe dark.
rgb = atlas.convert("RGB")
blur = rgb.filter(ImageFilter.GaussianBlur(6))
mask = atlas.getchannel("A")
filled = Image.composite(rgb, blur, mask.point(lambda v: 255 if v > 8 else 0))
filled.putalpha(mask)

files = []
d = os.path.join(OUT, "fir_branch")
os.makedirs(d, exist_ok=True)
p = os.path.join(d, "fir_branch_diffa_1k.png"); filled.save(p, optimize=True); files.append(p)
p = os.path.join(d, "fir_branch_nor_gl_1k.jpg"); Image.new("RGB", (64, 64), (128, 128, 255)).save(p, quality=95); files.append(p)
d = os.path.join(OUT, "fir_bark")
os.makedirs(d, exist_ok=True)
for name, dst in (("bark_diff", "fir_bark_diff_1k.jpg"), ("bark_nor_gl", "fir_bark_nor_gl_1k.jpg")):
    p = os.path.join(d, dst)
    open(p, "wb").write(open(fetch(name), "rb").read())
    files.append(p)
print(json.dumps([{"path": os.path.relpath(f, REPO).replace("\\", "/"),
                   "sha256": hashlib.sha256(open(f, "rb").read()).hexdigest()} for f in files], indent=2))

"""Gera src/app.ico (ícone do executável): círculo azul com duas setas de sincronizar.
Uso: python3 tools/make-icon.py  (requer Pillow). Rode de novo só se o desenho mudar."""
import math
from PIL import Image, ImageDraw

def draw(size):
    s = size * 4  # desenha 4x maior e reduz, para bordas suaves
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = s * 0.04
    d.ellipse([pad, pad, s - pad, s - pad], fill=(47, 125, 225, 255))
    c, r, w = s / 2, s * 0.27, max(2, int(s * 0.085))
    white = (255, 255, 255, 255)
    for start in (200, 20):  # dois arcos opostos, cada um terminando numa ponta de seta
        end = start + 120
        d.arc([c - r, c - r, c + r, c + r], start, end, fill=white, width=w)
        # no PIL os ângulos crescem no sentido horário (eixo y para baixo): o movimento no fim do arco
        # segue a tangente (-sen a, cos a); a normal (cos a, sen a) aponta para fora do círculo
        a = math.radians(end)
        base = (c + r * math.cos(a), c + r * math.sin(a))
        t = (-math.sin(a), math.cos(a))
        n = (math.cos(a), math.sin(a))
        length, half = w * 1.6, w * 1.25
        d.polygon([
            (base[0] + t[0] * length, base[1] + t[1] * length),
            (base[0] + n[0] * half, base[1] + n[1] * half),
            (base[0] - n[0] * half, base[1] - n[1] * half),
        ], fill=white)
    return img.resize((size, size), Image.LANCZOS)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = [draw(n) for n in sizes]
images[-1].save("src/app.ico", sizes=[(n, n) for n in sizes], append_images=images[:-1])
print("src/app.ico gerado")

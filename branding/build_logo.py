"""Gera a identidade visual do Atalaia (símbolo, favicon e logotipo claro/escuro) a partir do desenho em código.

    pip install fonttools
    python branding/build_logo.py

Escreve em src/Atalaia.Server/wwwroot/img/: logo-mark.svg, favicon.svg, logo.svg (para fundo claro) e logo-dark.svg (para fundo escuro).
O logotipo "Atalaia" é desenhado em CONTORNOS (não é texto): fica idêntico em qualquer computador, sem depender de fonte instalada.
A fonte usada para os contornos é a Poppins (SIL Open Font License 1.1, https://github.com/google/fonts/tree/main/ofl/poppins),
baixada para branding/.fonts na primeira execução (essa pasta não vai para o repositório).

O símbolo: um "A" cujas pernas são as de uma torre de vigia (atalaia), com cabine, telhado e a janela acesa.
"""
import os, sys, urllib.request

from fontTools.ttLib import TTFont
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen

HERE = os.path.dirname(os.path.abspath(__file__))
FONTS = os.path.join(HERE, ".fonts")
IMG = os.path.join(os.path.dirname(HERE), "src", "Atalaia.Server", "wwwroot", "img")

BLUE_A, BLUE_B = "#4A9BFF", "#1250D6"   # gradiente da marca (o mesmo do painel)
ICE, AMBER = "#F3F8FF", "#FFC247"       # gelo (traço do símbolo) e a luz da torre
INK, INK_DARK = "#0F1B2D", "#EAF0FA"
BRAND_FLAT = "#2A78D6"
RAW = "https://raw.githubusercontent.com/google/fonts/main/ofl/poppins/"


def font(name):
    path = os.path.join(FONTS, name)
    if not os.path.exists(path):
        os.makedirs(FONTS, exist_ok=True)
        print("baixando", name, "...")
        urllib.request.urlretrieve(RAW + name, path)
    f = TTFont(path)
    return f, f.getGlyphSet(), f.getBestCmap(), f["head"].unitsPerEm


def text_path(fname, text, size, x, y, tracking=0.0, kern=None):
    """Contorno do texto (tracking em milésimos de em; kern: ajuste fino por par, em unidades da fonte)."""
    f, gs, cmap, upm = font(fname)
    s = size / upm
    pen = SVGPathPen(gs)
    cx = x
    for i, ch in enumerate(text):
        g = cmap[ord(ch)]
        gs[g].draw(TransformPen(pen, (s, 0, 0, -s, cx, y)))
        adv = f["hmtx"][g][0] + tracking * upm / 1000
        if kern and i + 1 < len(text):
            adv += kern.get(text[i:i + 2], 0)
        cx += adv * s
    return pen.getCommands(), cx


DEFS = (f'<linearGradient id="g-brand" x1="10" y1="4" x2="54" y2="62" gradientUnits="userSpaceOnUse"><stop offset="0" stop-color="{BLUE_A}"/><stop offset="1" stop-color="{BLUE_B}"/></linearGradient>'
        '<linearGradient id="g-sheen" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".28"/><stop offset=".6" stop-color="#fff" stop-opacity="0"/></linearGradient>'
        f'<radialGradient id="g-glow"><stop offset="0" stop-color="{AMBER}" stop-opacity=".55"/><stop offset="1" stop-color="{AMBER}" stop-opacity="0"/></radialGradient>')


def mark(tile=True, ink=ICE):
    g = []
    if tile:
        g.append('<rect x="4" y="4" width="56" height="56" rx="15" fill="url(#g-brand)"/><rect x="4" y="4" width="56" height="56" rx="15" fill="url(#g-sheen)"/>')
    g.append('<ellipse cx="32" cy="24" rx="13" ry="9" fill="url(#g-glow)"/>')
    g.append(f'<path d="M18.5 54 28.4 29.6M45.5 54 35.6 29.6" fill="none" stroke="{ink}" stroke-width="5" stroke-linecap="round"/>')   # pernas
    g.append(f'<path d="M23.6 43.4h16.8" fill="none" stroke="{ink}" stroke-width="4.2" stroke-linecap="round"/>')                     # travessa
    g.append(f'<rect x="23" y="20" width="18" height="10.5" rx="2.6" fill="{ink}"/>')                                                  # cabine
    g.append(f'<path d="M20.8 21.4 32 12.2 43.2 21.4Z" fill="{ink}" stroke="{ink}" stroke-width="1.6" stroke-linejoin="round"/>')     # telhado
    g.append(f'<rect x="28.2" y="23.2" width="7.6" height="4.6" rx="1.4" fill="{AMBER}"/>')                                           # janela acesa
    return "".join(g)


def svg(w, h, body, label="Atalaia"):
    return f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" role="img" aria-label="{label}"><defs>{DEFS}</defs>{body}</svg>\n'


def lockup(ink, tag):
    wm, x_end = text_path("Poppins-SemiBold.ttf", "Atalaia", 34, 78, 41, tracking=-8, kern={"At": -30, "ta": -8})
    tg, tg_end = text_path("Poppins-Medium.ttf", "INVENTÁRIO · SAÚDE · AUDITORIA", 7.6, 79.5, 55.5, tracking=130)
    w = round(max(x_end, tg_end) + 6)
    return svg(w, 64, f'<g>{mark()}</g><path d="{wm}" fill="{ink}"/><path d="{tg}" fill="{tag}"/>')


def save(name, content):
    os.makedirs(IMG, exist_ok=True)
    open(os.path.join(IMG, name), "w", encoding="utf-8", newline="\n").write(content)
    print("gerado:", name, f"({len(content)} bytes)")


if __name__ == "__main__":
    save("logo-mark.svg", svg(64, 64, mark()))
    save("favicon.svg", svg(64, 64, mark()))
    save("logo.svg", lockup(INK, "#6B7785"))
    save("logo-dark.svg", lockup(INK_DARK, "#8E9CB4"))

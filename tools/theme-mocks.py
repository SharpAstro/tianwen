"""Render candidate colour cores for docs/plans/colour-theme.md as PNG mockups.

Three candidate cores, each in three states (Light / Dark / Night), drawn as the same
slice of the GUI so they are comparable: sidebar rail, header, mount telemetry, a guide
graph, the severity trio, and a progress row.

Two numbers are computed per palette rather than eyeballed, because both are easy to get
wrong by looking:

  contrast  WCAG relative-luminance ratio of body text against the panel it sits on.
  rod       Scotopic stimulation index, the thing Night mode exists to minimise. Weighted
            by V'(lambda) at each sRGB primary's dominant wavelength (R ~611nm -> 0.0155,
            G ~549nm -> 0.49, B ~464nm -> 0.61), so it says what a colour costs in dark
            adaptation. Note R is ~30-40x cheaper than either G or B, which is the whole
            argument for the Night column.

Usage:  python tools/theme-mocks.py
Output: docs/plans/colour-theme-mocks/*.png + index.html
"""

import math
import os
import random

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "docs", "plans", "colour-theme-mocks"))

SCALE = 2
W, H = 660, 470

FONT_DIR = os.environ.get("WINDIR", "C:/Windows") + "/Fonts"
F_REG = FONT_DIR + "/segoeui.ttf"
F_SEMI = FONT_DIR + "/seguisb.ttf"
F_MONO = FONT_DIR + "/consola.ttf"


# ----------------------------------------------------------------------------- palettes

def core(void, panel, panel2, line, line_strong, text, dim, accent, accent2,
         info, warn, error):
    return dict(void=void, panel=panel, panel2=panel2, line=line, line_strong=line_strong,
                text=text, dim=dim, accent=accent, accent2=accent2,
                info=info, warn=warn, error=error)


CORES = {
    # CHOSEN 2026-08-07 from the studio: Plate's Light and Dark, Ember's Night. Night's DimText is
    # lifted from Ember's #a83500 to #b83c00, which is the entire margin available under the 5.25:1
    # red-on-black ceiling; the rest is carried by the RULE that Night labels use BodyText.
    "D-chosen": {
        "label": "D. Chosen",
        "pitch": "Plate for Light and Dark, Ember for Night. The hue jump at the boundary is deliberate.",
        "Light": core("#f2f4f6", "#ffffff", "#e9edf1", "#d8dee5", "#bcc5cf",
                      "#14181d", "#5a626c", "#0a63a8", "#0a63a8",
                      "#0a63a8", "#8a5000", "#b02a20"),
        "Dark": core("#101318", "#171b22", "#1e232c", "#2a3039", "#3c444f",
                     "#e2e6ec", "#8b939f", "#7cc4ff", "#7cc4ff",
                     "#7cc4ff", "#e8a33c", "#ff7a70"),
        "Night": core("#000000", "#0c0400", "#180800", "#2e1200", "#4d1e00",
                      "#e04a00", "#b83c00", "#ff6a00", "#a83c00",
                      "#8c3000", "#cc5c00", "#ff1500"),
    },
    # The site's shipped palette, extended to the app. Blue-black void, teal readout,
    # amber secondary. Continuity with sharpastro.github.io is the argument.
    "A-observatory": {
        "label": "A. Observatory",
        "pitch": "The site palette, extended to the app. Teal reads as instrument readout.",
        "Light": core("#f5f6f8", "#ffffff", "#eef1f5", "#dde2ea", "#c3ccd9",
                      "#0f141c", "#59647a", "#0b7d6d", "#8f5300",
                      "#1f6fb2", "#8f5300", "#b3261e"),
        "Dark": core("#070a12", "#0d1220", "#111829", "#1b2334", "#2b3550",
                     "#dbe2ee", "#8895ac", "#66ddcc", "#ffb35c",
                     "#5fa8e0", "#ffb35c", "#ff6b6b"),
        "Night": core("#000000", "#0a0000", "#160200", "#2e0700", "#4d0c00",
                      "#d92200", "#a31800", "#ff3b00", "#cc5c00",
                      "#993000", "#cc5c00", "#ff1500"),
    },
    # Cool neutral greys, ONE accent, no secondary. Closest to what GuiTheme looks like
    # today, so the lowest visual shock, and the most conventional "tool" reading.
    "B-plate": {
        "label": "B. Plate",
        "pitch": "Cool neutrals, one accent, no secondary. Closest to today's GUI.",
        "Light": core("#f2f4f6", "#ffffff", "#e9edf1", "#d8dee5", "#bcc5cf",
                      "#14181d", "#5a626c", "#0a63a8", "#0a63a8",
                      "#0a63a8", "#8a5000", "#b02a20"),
        "Dark": core("#101318", "#171b22", "#1e232c", "#2a3039", "#3c444f",
                     "#e2e6ec", "#8b939f", "#7cc4ff", "#7cc4ff",
                     "#7cc4ff", "#e8a33c", "#ff7a70"),
        # Plate's Night is the strict reading: as close to R-only as legibility allows.
        "Night": core("#000000", "#080000", "#120000", "#260000", "#400000",
                      "#cc0f00", "#8f0a00", "#ff2200", "#ff2200",
                      "#8c1a00", "#c24a00", "#ff1200"),
    },
    # Warm neutrals with amber leading. The argument nobody expects: amber is already the
    # dark-adaptation-adjacent hue, so Dark -> Night is a shift in DEGREE, not in kind, and
    # the app keeps one identity across all four states. Costs rod stimulation to do it.
    "C-ember": {
        "label": "C. Ember",
        "pitch": "Warm neutrals, amber-led. Dark to Night becomes a shift in degree, not kind.",
        "Light": core("#f7f4ef", "#fffdfa", "#efeae1", "#e0d9cd", "#c6bcab",
                      "#171310", "#6b6154", "#9a5b00", "#0b6d60",
                      "#1f6fb2", "#9a5b00", "#b3261e"),
        "Dark": core("#0d0b09", "#16130f", "#1f1a15", "#2e2720", "#453b30",
                     "#ece4d8", "#a29685", "#ffb055", "#6fd0c0",
                     "#6fb8e0", "#ffb055", "#ff7a66"),
        "Night": core("#000000", "#0c0400", "#180800", "#2e1200", "#4d1e00",
                      "#e04a00", "#a83500", "#ff6a00", "#cc5c00",
                      "#8c3000", "#cc5c00", "#ff1500"),
    },
}

STATES = ["Light", "Dark", "Night"]


# -------------------------------------------------------------------------- colour math

def rgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def linear(c):
    c = c / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def rel_luminance(h):
    r, g, b = (linear(c) for c in rgb(h))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast(fg, bg):
    a, b = rel_luminance(fg), rel_luminance(bg)
    hi, lo = max(a, b), min(a, b)
    return (hi + 0.05) / (lo + 0.05)


def rod_index(h):
    """Scotopic stimulation, V'(lambda) at each sRGB primary's dominant wavelength."""
    r, g, b = (linear(c) for c in rgb(h))
    return 0.0155 * r + 0.49 * g + 0.61 * b


def mix(a, b, t):
    ca, cb = rgb(a), rgb(b)
    return tuple(round(ca[i] + (cb[i] - ca[i]) * t) for i in range(3))


# ------------------------------------------------------------------------------ drawing

def font(path, size):
    return ImageFont.truetype(path, size * SCALE)


def s(v):
    return v * SCALE


def box(d, x, y, w, h, fill=None, outline=None, radius=0):
    xy = [s(x), s(y), s(x + w) - 1, s(y + h) - 1]
    if radius:
        d.rounded_rectangle(xy, radius=s(radius), fill=fill, outline=outline, width=SCALE)
    else:
        d.rectangle(xy, fill=fill, outline=outline, width=SCALE)


def text(d, x, y, txt, f, fill, anchor="la"):
    d.text((s(x), s(y)), txt, font=f, fill=fill, anchor=anchor)


def render(core_key, state):
    p = CORES[core_key][state]
    img = Image.new("RGB", (s(W), s(H)), rgb(p["void"]))
    d = ImageDraw.Draw(img)

    f_ui = font(F_REG, 12)
    f_sm = font(F_REG, 10)
    f_hd = font(F_SEMI, 13)
    f_lbl = font(F_SEMI, 9)
    f_num = font(F_MONO, 12)
    f_big = font(F_MONO, 15)

    rail = 52
    header_h = 42
    status_h = 24

    # --- sidebar rail --------------------------------------------------------------
    box(d, 0, 0, rail, H, fill=rgb(p["panel2"]))
    box(d, rail - 1, 0, 1, H, fill=rgb(p["line"]))
    tabs = ["H", "E", "P", "S", "R", "G", "N"]
    active = 4
    for i, t in enumerate(tabs):
        ty = 14 + i * 44
        if i == active:
            box(d, 0, ty - 6, 3, 34, fill=rgb(p["accent"]))
            box(d, 3, ty - 6, rail - 4, 34, fill=rgb(p["panel"]))
        col = rgb(p["accent"]) if i == active else rgb(p["dim"])
        text(d, rail / 2 + 1, ty + 4, t, f_hd, col, anchor="ma")

    # --- header --------------------------------------------------------------------
    box(d, rail, 0, W - rail, header_h, fill=rgb(p["panel2"]))
    box(d, rail, header_h - 1, W - rail, 1, fill=rgb(p["line"]))
    text(d, rail + 16, 9, "NGC 7293", f_hd, rgb(p["text"]))
    text(d, rail + 92, 11, "Helix Nebula", f_ui, rgb(p["dim"]))

    pill_w, pill_x = 74, W - 90
    box(d, pill_x, 11, pill_w, 20, fill=mix(p["panel2"], p["accent"], 0.18),
        outline=rgb(p["accent"]), radius=10)
    text(d, pill_x + pill_w / 2, 15, "Imaging", f_lbl, rgb(p["accent"]), anchor="ma")

    # --- mount telemetry card ------------------------------------------------------
    cx, cy, cw, ch = rail + 16, header_h + 16, 250, 176
    box(d, cx, cy, cw, ch, fill=rgb(p["panel"]), outline=rgb(p["line"]), radius=4)
    text(d, cx + 12, cy + 10, "MOUNT", f_lbl, rgb(p["dim"]))
    box(d, cx + 12, cy + 26, cw - 24, 1, fill=rgb(p["line"]))

    rows = [("RA", "22h 29m 38.5s"), ("Dec", "-20d 50' 14\""), ("Altitude", "58.4 deg"),
            ("Hour angle", "-1h 12m"), ("Pier side", "East"), ("Tracking", "Sidereal")]
    for i, (k, v) in enumerate(rows):
        ry = cy + 36 + i * 22
        text(d, cx + 12, ry, k, f_ui, rgb(p["dim"]))
        text(d, cx + cw - 12, ry, v, f_num, rgb(p["text"]), anchor="ra")

    # --- guide graph ---------------------------------------------------------------
    gx, gy, gw, gh = cx + cw + 16, header_h + 16, W - (cx + cw + 16) - 16, 108
    box(d, gx, gy, gw, gh, fill=rgb(p["panel"]), outline=rgb(p["line"]), radius=4)
    text(d, gx + 12, gy + 10, "GUIDING", f_lbl, rgb(p["dim"]))
    text(d, gx + gw - 12, gy + 10, "RMS 0.42\"", f_num, rgb(p["text"]), anchor="ra")

    plot = (gx + 12, gy + 30, gx + gw - 12, gy + gh - 12)
    mid = (plot[1] + plot[3]) / 2
    box(d, plot[0], mid, plot[2] - plot[0], 1, fill=rgb(p["line_strong"]))

    rnd = random.Random(7)
    for series, colour, amp in (("ra", p["accent"], 12), ("dec", p["accent2"], 8)):
        pts, v = [], 0.0
        n = 46
        for i in range(n):
            v = v * 0.72 + rnd.uniform(-1, 1) * amp * 0.5
            px = plot[0] + (plot[2] - plot[0]) * i / (n - 1)
            pts.append((s(px), s(mid + max(-26, min(26, v)))))
        d.line(pts, fill=rgb(colour), width=SCALE, joint="curve")

    # --- severity trio -------------------------------------------------------------
    sx, sy, sw = gx, gy + gh + 14, gw
    box(d, sx, sy, sw, 96, fill=rgb(p["panel"]), outline=rgb(p["line"]), radius=4)
    notes = [("info", "Filter wheel moved to Ha"),
             ("warn", "Focus drift 0.41 above baseline"),
             ("error", "Guide star lost for 32 s")]
    for i, (sev, msg) in enumerate(notes):
        ny = sy + 12 + i * 26
        # Error carries a FILLED stripe against warn's outline. In Night the two hues sit
        # about 22 degrees apart, which is the tightest call in the palette, so severity is
        # reinforced by form and not left to hue alone.
        if sev == "error":
            box(d, sx + 12, ny, 4, 16, fill=rgb(p[sev]))
        else:
            box(d, sx + 12, ny, 4, 16, outline=rgb(p[sev]))
        text(d, sx + 24, ny + 1, msg, f_ui, rgb(p["text"] if sev == "info" else p[sev]))

    # --- progress ------------------------------------------------------------------
    px_, py = cx, cy + ch + 14
    pw = W - cx - 16
    box(d, px_, py, pw, 58, fill=rgb(p["panel"]), outline=rgb(p["line"]), radius=4)
    text(d, px_ + 12, py + 10, "target 2/3", f_lbl, rgb(p["dim"]))
    text(d, px_ + pw - 12, py + 8, "frame 23 / 100", f_big, rgb(p["text"]), anchor="ra")
    bar_y = py + 34
    box(d, px_ + 12, bar_y, pw - 24, 8, fill=rgb(p["line"]), radius=4)
    box(d, px_ + 12, bar_y, int((pw - 24) * 0.23), 8, fill=rgb(p["accent"]), radius=4)

    # --- status bar ----------------------------------------------------------------
    box(d, 0, H - status_h, W, status_h, fill=rgb(p["panel2"]))
    box(d, 0, H - status_h, W, 1, fill=rgb(p["line"]))
    text(d, 12, H - status_h + 5, "Cooling  -10.0 C   Focuser  18420", f_sm, rgb(p["dim"]))
    text(d, W - 12, H - status_h + 5, state.upper(), f_lbl, rgb(p["accent"]), anchor="ra")

    return img


# -------------------------------------------------------------------------------- main

def main():
    os.makedirs(OUT, exist_ok=True)
    made = []

    for key in CORES:
        for state in STATES:
            img = render(key, state)
            name = "{}-{}.png".format(key, state.lower())
            img.save(os.path.join(OUT, name))
            made.append((key, state, name))

    # Contact sheet: one row per core, one column per state.
    tw, th = s(W) // 2, s(H) // 2
    pad, top = 16, 34
    sheet = Image.new("RGB", (len(STATES) * tw + pad * (len(STATES) + 1),
                              len(CORES) * (th + top) + pad),
                      (24, 24, 28))
    sd = ImageDraw.Draw(sheet)
    f_t = ImageFont.truetype(F_SEMI, 17)
    f_p = ImageFont.truetype(F_REG, 13)
    for r, key in enumerate(CORES):
        y = pad + r * (th + top)
        sd.text((pad, y), CORES[key]["label"], font=f_t, fill=(235, 235, 240))
        sd.text((pad + 150, y + 2), CORES[key]["pitch"], font=f_p, fill=(150, 155, 168))
        for c, state in enumerate(STATES):
            tile = render(key, state).resize((tw, th), Image.LANCZOS)
            sheet.paste(tile, (pad + c * (tw + pad), y + top))
    sheet.save(os.path.join(OUT, "contact-sheet.png"))

    # Numbers, so the trade-offs are read rather than guessed.
    print("{:<16} {:<6} {:>9} {:>9} {:>8}".format(
        "core", "state", "text/bg", "err/bg", "rod"))
    rows_html = []
    for key in CORES:
        for state in STATES:
            p = CORES[key][state]
            c_text = contrast(p["text"], p["panel"])
            c_err = contrast(p["error"], p["panel"])
            rod = sum(rod_index(p[k]) for k in ("text", "accent", "warn", "error")) / 4
            print("{:<16} {:<6} {:>8.2f}:1 {:>8.2f}:1 {:>8.4f}".format(
                key, state, c_text, c_err, rod))
            rows_html.append((key, state, c_text, c_err, rod))

    write_index(made, rows_html)
    print("\n{} mockups + contact sheet -> {}".format(len(made), OUT))


def write_index(made, rows):
    by = {}
    for key, state, name in made:
        by.setdefault(key, {})[state] = name

    cards = []
    for key in CORES:
        tiles = "".join(
            '<figure><img src="{}" alt="{} {}" width="{}" height="{}" loading="lazy">'
            '<figcaption>{}</figcaption></figure>'.format(
                by[key][st], key, st, W * SCALE, H * SCALE, st)
            for st in STATES)
        nums = "".join(
            '<tr><td>{}</td><td class="n">{:.2f}:1</td><td class="n">{:.2f}:1</td>'
            '<td class="n">{:.4f}</td></tr>'.format(r[1], r[2], r[3], r[4])
            for r in rows if r[0] == key)
        swatches = "".join(
            '<span class="sw" style="background:{}" title="{} {}"></span>'.format(
                CORES[key]["Dark"][t], t, CORES[key]["Dark"][t])
            for t in ("void", "panel", "panel2", "line", "line_strong",
                      "text", "dim", "accent", "accent2"))
        cards.append(
            '<section><h2>{}</h2><p class="pitch">{}</p><div class="sws">{}</div>'
            '<div class="row">{}</div>'
            '<table><thead><tr><th>state</th><th>text/bg</th><th>error/bg</th>'
            '<th>rod</th></tr></thead><tbody>{}</tbody></table></section>'.format(
                CORES[key]["label"], CORES[key]["pitch"], swatches, tiles, nums))

    html = """<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>TianWen colour core candidates</title>
<style>
  :root {{ color-scheme: dark; --bg:#0b0d12; --card:#12151d; --line:#222836;
           --tx:#dfe4ee; --dim:#8b95a8; }}
  * {{ box-sizing:border-box; }}
  body {{ margin:0; padding:32px; background:var(--bg); color:var(--tx);
          font:15px/1.55 "Segoe UI",system-ui,sans-serif; }}
  h1 {{ font-size:26px; margin:0 0 6px; letter-spacing:-.01em; }}
  .lede {{ color:var(--dim); max-width:70ch; margin:0 0 8px; }}
  .note {{ color:var(--dim); max-width:78ch; margin:0 0 28px; font-size:13.5px; }}
  section {{ background:var(--card); border:1px solid var(--line); border-radius:10px;
             padding:20px; margin-bottom:24px; }}
  h2 {{ font-size:18px; margin:0 0 4px; }}
  .pitch {{ color:var(--dim); margin:0 0 12px; font-size:13.5px; }}
  .sws {{ display:flex; gap:3px; margin-bottom:16px; }}
  .sw {{ width:34px; height:16px; border-radius:3px; border:1px solid #0006; }}
  .row {{ display:grid; grid-template-columns:repeat(auto-fit,minmax(330px,1fr));
          gap:16px; }}
  figure {{ margin:0; }}
  img {{ width:100%; height:auto; display:block; border-radius:6px;
         border:1px solid var(--line); }}
  figcaption {{ color:var(--dim); font-size:12px; margin-top:6px;
                text-transform:uppercase; letter-spacing:.08em; }}
  table {{ margin-top:18px; border-collapse:collapse; font-size:13px; }}
  th,td {{ text-align:left; padding:5px 20px 5px 0; border-bottom:1px solid var(--line); }}
  th {{ color:var(--dim); font-weight:600; font-size:11.5px;
        text-transform:uppercase; letter-spacing:.07em; }}
  .n {{ font-family:Consolas,ui-monospace,monospace; font-variant-numeric:tabular-nums; }}
</style></head><body>
<h1>Colour core candidates</h1>
<p class="lede">Three cores, each in Light / Dark / Night, drawn as the same slice of the
GUI so they are comparable.</p>
<p class="note"><strong>text/bg</strong> and <strong>error/bg</strong> are WCAG contrast
ratios (4.5:1 is AA for body text). <strong>rod</strong> is scotopic stimulation, averaged
over the four foreground colours, weighted by V'(lambda) at each sRGB primary's dominant
wavelength. It is what Night mode exists to minimise: lower is darker-adapted. Red is
roughly 30 to 40 times cheaper than green or blue, so any G or B in a Night palette is a
cost paid for hue separation.</p>
{}
</body></html>""".format("\n".join(cards))

    with open(os.path.join(OUT, "index.html"), "w", encoding="utf-8") as f:
        f.write(html)


if __name__ == "__main__":
    main()

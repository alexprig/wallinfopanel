# Original anime-style night scene for the 792x272 4-grey e-paper. Only 4 tones are used.
import math, random, sys
random.seed(7)
B, D, L, W = "#000000", "#555555", "#aaaaaa", "#ffffff"
o = []
add = o.append
add(f'<svg xmlns="http://www.w3.org/2000/svg" width="792" height="272" viewBox="0 0 792 272">')
add(f'<rect width="792" height="272" fill="{D}"/>')

# --- stars
for _ in range(70):
    x, y = random.uniform(0, 792), random.uniform(0, 200)
    if (x-205)**2 + (y-100)**2 < 95**2: continue
    add(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="{random.choice([0.8,1,1.2,1.6]):.1f}" fill="{W}"/>')
def sparkle(x, y, s):
    add(f'<path d="M{x},{y-s} Q{x+s*0.18},{y-s*0.18} {x+s},{y} Q{x+s*0.18},{y+s*0.18} {x},{y+s} '
        f'Q{x-s*0.18},{y+s*0.18} {x-s},{y} Q{x-s*0.18},{y-s*0.18} {x},{y-s} Z" fill="{W}"/>')
for x, y, s in [(330,28,7),(690,40,9),(760,120,6),(410,150,5),(95,175,5),(735,200,7),(650,95,4)]:
    sparkle(x, y, s)

# --- moon with halo and craters
add(f'<circle cx="205" cy="100" r="88" fill="none" stroke="{L}" stroke-width="2" stroke-dasharray="3 7"/>')
add(f'<circle cx="205" cy="100" r="72" fill="{W}"/>')
for cx, cy, r in [(184,82,8),(229,116,10),(213,70,4),(177,122,5),(244,88,3)]:
    add(f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{L}"/>')

# --- city skyline (left), lit windows
x = -4
while x < 445:
    w = random.randint(22, 44); h = random.randint(28, 78)
    if x > 330: h = random.randint(20, 45)
    add(f'<rect x="{x}" y="{272-h}" width="{w}" height="{h}" fill="{B}"/>')
    for wy in range(272-h+7, 268, 9):
        for wx in range(x+5, x+w-5, 8):
            if random.random() < 0.3:
                add(f'<rect x="{wx}" y="{wy}" width="3" height="4" fill="{random.choice([W, L, L])}"/>')
    x += w + random.randint(0, 3)

# --- cherry blossom branch (top left)
add(f'<path d="M-6,36 C60,24 118,46 178,34 C228,24 268,46 330,40" fill="none" stroke="{B}" stroke-width="7" stroke-linecap="round"/>')
add(f'<path d="M116,42 C138,66 160,78 196,84" fill="none" stroke="{B}" stroke-width="4" stroke-linecap="round"/>')
add(f'<path d="M226,30 C246,16 270,10 304,8" fill="none" stroke="{B}" stroke-width="3.5" stroke-linecap="round"/>')
add(f'<path d="M58,30 C70,12 84,6 104,4" fill="none" stroke="{B}" stroke-width="3" stroke-linecap="round"/>')
def blossom(cx, cy, r, rot):
    g = [f'<g transform="translate({cx},{cy}) rotate({rot})">']
    for k in range(5):
        g.append(f'<ellipse cx="0" cy="{-r*0.62:.1f}" rx="{r*0.42:.1f}" ry="{r*0.62:.1f}" fill="{W}" stroke="{B}" stroke-width="1" transform="rotate({k*72})"/>')
    g.append(f'<circle r="{r*0.28:.1f}" fill="{L}" stroke="{B}" stroke-width="0.8"/></g>')
    add("".join(g))
for cx, cy, r, rot in [(30,30,11,10),(78,26,9,40),(102,6,8,5),(150,44,12,20),(196,84,10,0),(170,72,8,30),
                       (212,32,11,15),(260,40,9,50),(304,9,9,25),(286,14,7,0),(330,40,10,35),(128,58,7,12)]:
    blossom(cx, cy, r, rot)

# --- falling petals (background layer)
def petal(x, y, s, rot, col=W):
    add(f'<ellipse cx="{x:.1f}" cy="{y:.1f}" rx="{s:.1f}" ry="{s*0.55:.1f}" fill="{col}" stroke="{B}" stroke-width="0.7" transform="rotate({rot} {x:.1f} {y:.1f})"/>')
for _ in range(16):
    petal(random.uniform(260, 790), random.uniform(60, 250), random.uniform(2.5, 4), random.uniform(0, 180))

# --- GIRL (centre x = 560) ------------------------------------------------
# back hair, long and falling behind the shoulders
add(f'<path d="M470,62 C468,16 520,1 562,1 C606,1 654,16 652,70 C656,150 672,214 708,272 L414,272 C454,214 468,146 470,60 Z" fill="{B}"/>')
# wind-blown strands to the left
for d in ["M482,64 C432,84 396,124 352,146 C396,140 432,128 474,118 Z",
          "M472,112 C432,156 398,196 344,214 C394,212 440,194 470,166 Z",
          "M466,164 C442,212 410,242 376,268 C420,262 456,234 474,206 Z",
          "M650,96 C676,120 690,150 704,166 C688,160 668,146 652,132 Z"]:
    add(f'<path d="{d}" fill="{B}"/>')
# hair shine on the strands
for d in ["M440,108 C420,124 402,134 384,142", "M430,170 C414,186 396,198 374,208", "M442,222 C430,236 416,248 402,258"]:
    add(f'<path d="{d}" fill="none" stroke="{L}" stroke-width="1.6" stroke-linecap="round"/>')

# neck + shadow
add(f'<path d="M543,196 L541,234 L579,234 L577,196 Z" fill="{W}" stroke="{B}" stroke-width="2"/>')
add(f'<path d="M543,199 Q560,214 577,199 L577,212 Q560,224 543,212 Z" fill="{L}"/>')
# blouse
add(f'<path d="M402,272 C424,246 484,234 541,230 L579,230 C636,234 696,246 718,272 Z" fill="{W}" stroke="{B}" stroke-width="2.5"/>')
# sailor collar
add(f'<path d="M482,237 C506,232 526,231 541,230 L560,254 L579,230 C594,231 614,232 638,237 L560,272 Z" fill="{D}" stroke="{B}" stroke-width="2"/>')
add(f'<path d="M494,241 L560,268 L626,241" fill="none" stroke="{W}" stroke-width="2"/>')
# blouse folds
add(f'<path d="M450,262 C462,256 474,252 486,250 M670,262 C658,256 646,252 634,250" fill="none" stroke="{L}" stroke-width="2"/>')
# bow
add(f'<path d="M560,256 C538,242 524,264 544,270 Z M560,256 C582,242 596,264 576,270 Z" fill="{B}"/>')
add(f'<path d="M556,258 L548,272 L556,272 L560,262 L564,272 L572,272 L564,258 Z" fill="{B}"/>')
add(f'<circle cx="560" cy="257" r="5" fill="{B}" stroke="{D}" stroke-width="1"/>')

# face
add(f'<path d="M492,96 C490,150 515,192 560,210 C605,192 630,150 628,96 Z" fill="{W}" stroke="{B}" stroke-width="2.5"/>')
# soft shadow under the bangs
add(f'<path d="M494,110 Q560,128 626,110 L626,121 Q560,138 494,121 Z" fill="{L}"/>')
# eyebrows
add(f'<path d="M511,128 Q525,122 540,126 M609,126 Q595,122 580,128" fill="none" stroke="{D}" stroke-width="2.2" stroke-linecap="round"/>')

def eye(cx, cy, mirror):
    t = f'translate({cx},{cy}) scale(1.15)' + (' scale(-1,1)' if mirror else '')
    add(f'<g transform="{t}">'
        f'<ellipse cx="0" cy="2" rx="12.5" ry="15.5" fill="{D}" stroke="{B}" stroke-width="1.5"/>'
        f'<path d="M-11,5 Q0,19 11,5 Q0,13 -11,5 Z" fill="{L}"/>'
        f'<ellipse cx="0" cy="0" rx="5.6" ry="8.2" fill="{B}"/>'
        f'<path d="M-9,-3 Q0,-11 9,-3" fill="none" stroke="{B}" stroke-width="2"/>'
        f'<circle cx="-4.5" cy="-6" r="4.6" fill="{W}"/>'
        f'<circle cx="5" cy="7.5" r="2.1" fill="{W}"/>'
        f'<circle cx="-7" cy="5" r="1.2" fill="{W}"/>'
        f'<path d="M-18,-10 Q-2,-22 17,-11 L20,-7 Q2,-17 -17,-6 Z" fill="{B}"/>'
        f'<path d="M15,-11 L24,-15 L19,-6 Z" fill="{B}"/>'
        f'<path d="M-13,-18 Q0,-25 15,-18" fill="none" stroke="{B}" stroke-width="1.1"/>'
        f'<path d="M-12,16 Q0,20 11,15" fill="none" stroke="{B}" stroke-width="1.5"/>'
        f'<path d="M-15,-3 L-17,-1 M17,-2 L19,0" stroke="{B}" stroke-width="1"/>'
        f'</g>')
eye(526, 152, True)
eye(594, 152, False)
# blush, nose, mouth
add(f'<path d="M506,176 l6,-6 M513,178 l6,-6 M520,180 l6,-6 M600,180 l6,-6 M607,178 l6,-6 M614,176 l6,-6" stroke="{L}" stroke-width="1.6" stroke-linecap="round"/>')
add(f'<path d="M562,170 l-2,5" stroke="{B}" stroke-width="1.5" stroke-linecap="round"/>')
add(f'<path d="M551,189 Q560,195 569,189" fill="none" stroke="{B}" stroke-width="1.8" stroke-linecap="round"/>')
add(f'<path d="M555,191.5 Q560,194 565,191.5" fill="none" stroke="{L}" stroke-width="1.2"/>')

# side locks
add(f'<path d="M484,98 C472,150 478,202 498,244 C503,240 507,236 510,232 C496,192 494,150 500,106 Z" fill="{B}"/>')
add(f'<path d="M636,98 C648,150 642,202 622,244 C617,240 613,236 610,232 C624,192 626,150 620,106 Z" fill="{B}"/>')
# bangs
tips = [(640,112),(626,124),(610,118),(594,130),(576,122),(557,140),(540,124),(522,131),(505,118),(490,124),(478,110)]
gaps = [(633,104),(618,106),(602,103),(586,107),(567,102),(548,104),(531,105),(514,103),(497,107),(484,104)]
d = "M476,118 C462,40 516,6 562,6 C610,6 662,40 648,104"
for i, (tx, ty) in enumerate(tips):
    d += f" Q{tx+8},{ty-19} {tx},{ty}"          # curved outer edge down to the tip
    if i < len(gaps):
        gx, gy = gaps[i]
        d += f" Q{tx-6},{ty-19} {gx},{gy}"      # back up into the gap
d += " Z"
add(f'<path d="{d}" fill="{B}"/>')
# hair shine ring and strands
add(f'<path d="M504,54 Q526,40 550,37 M572,37 Q597,40 618,54" fill="none" stroke="{W}" stroke-width="3.5" stroke-linecap="round"/>')
add(f'<path d="M512,62 Q520,56 528,53 M594,53 Q603,56 611,62" fill="none" stroke="{L}" stroke-width="2" stroke-linecap="round"/>')
# hair ornament: small blossom clip
blossom(632, 70, 9, 15)

# --- foreground petals (in front of the girl)
for x, y, s, r in [(470,150,4.5,30),(655,200,5,120),(520,240,3.5,60),(700,130,4,10),(610,40,3.5,80),(420,90,4,150)]:
    petal(x, y, s, r)
add('</svg>')
open(sys.argv[1], "w").write("\n".join(o))
print("svg elements:", len(o))

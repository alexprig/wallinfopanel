# WallInfoPanel desk case

A 3D-printed wedge that stands under the monitor and shows the 5.79" e-paper at **30° back from vertical**. USB-C powered, no battery.

Size: **170.4 × 61.3 × 70.7 mm** (W × D × H), **2.5 mm walls**, rounded edges (4.5 mm outside, 2 mm inside).

The screen window has the same 3-step bevel on all four sides: the opening is 0.5, 1.1 and 1.7 mm larger than the 139 × 47.74 mm image (inner to outer step).

## Files

| Path | What |
|---|---|
| `fusion/WallInfoPanel.f3d` | The Fusion model: case, bottom plate, clamp tabs, button caps, the two coupons and grey stand-ins for the panel, NodeMCU and USB-C flange |
| `stl/` | Print files, exported from the model |

## Edit the model

Open `fusion/WallInfoPanel.f3d` in Fusion (File → Open → Open from my computer). It's a direct-modeling design without a parameter table, so you change a dimension by editing the bodies themselves (Press Pull, Move/Copy on faces). Afterwards, run Inspect → Interference on the case and the stand-ins to check that nothing collides.

**Export the STLs:** right-click each printed body → **Save as Mesh** → STL (binary, mm) into `stl/`, with the same file names as in the table below. Keep the print orientation described there.

## Print

PLA or PETG, 0.4 mm nozzle, 0.2 mm layers, **no supports**. The STLs are already in print orientation.

| File | Qty | Notes |
|---|---|---|
| `window_coupon.stl` | 1 | Print first (≈10 min): check the screen window |
| `nodemcu_coupon.stl` | 1 | Print first (≈5 min): check the NodeMCU holes |
| `body.stl` | 1 | Upside down, flat top on the bed. 3+ perimeters for the insert bosses |
| `bottom_plate.stl` | 1 | Inside face on the bed |
| `clamp_tabs_x2.stl` | 1 | Two tabs (bottom edge) |
| `button_caps_x2.stl` | 1 | RST and FLASH caps; collar on the bed. Small parts: print slowly, or with a brim |

## Check with the coupons first

1. **Window:** lay `window_coupon` on the panel with its edges on the PCB edges and the **notch on the flex-cable side**. The whole image should show, with no white border. Designed for: image 139.0 × 47.74 mm, starting 3.6 mm from the left PCB edge, 9.9 mm from the right and 4.9 mm from the top. If it's off, move the window faces in the model.
2. **NodeMCU:** put the board on `nodemcu_coupon`. The four Ø3.5 mm holes should line up with the board's holes. Designed for: 52.5 × 25.4 mm hole spacing, holes 2.5 mm from the board's end. If not, move the holes and bosses in the model.
3. **USB-C socket:** check the flange with calipers. Designed for: flange 20.0 × 6.0 mm, two Ø2.4 mm screw holes 15.5 mm apart, socket opening 10.5 × 4.3 mm.

## Assemble

Screws: 2 × M2.5 × 6 self-tapping (tabs), 4 × M2.5 heat-set inserts (OD 3.5, L 4 mm) + 4 × M2.5 × 6 socket-head screws (NodeMCU), 2 × M2 × 6 + 2 nuts (USB-C), 4 × M3 × 6 countersunk (bottom plate), 4 rubber feet Ø10 mm.

The NodeMCU goes in **before the panel**: its screws are driven from the inside, and the panel would block them.

1. **Inserts:** melt the four M2.5 inserts into the bosses on the inside of the back wall (Ø3.2 × 5 mm holes) with a soldering iron, flush with the boss tops. The back wall stays closed on the outside.
2. **Button caps and NodeMCU:** push the two **button caps** in from inside: square shaft through the square holes, collar against the wall. Then put the board on the bosses, components facing the back wall, USB end towards the USB-C socket, and fix it with 4 × M2.5 × 6 from the pin side. The upper two screws are reachable through the window opening; for the lower two, use a 2 mm L-key through the open bottom. The board's buttons sit right behind the caps. From outside: **RST** is the cap just below the upper screw, **FLASH** the one just above the lower screw (hold FLASH, tap RST to force flashing mode).
3. **USB-C socket:** from inside, flange in its pocket in the back wall, 2 × M2 screws from outside with nuts inside. Wire G, V, D+, D− to the NodeMCU's USB lines.
4. **Setup switch:** push the mini slide switch into the frame behind the slot, lever through the slot. A drop of hot glue holds it.
5. **Panel:** from inside, glass towards the window, flex cable on the right. Slide its **top edge under the three built-in hooks**, lay it flat against the window, and let it drop onto the small ribs at the bottom. Ribs on the left and near the right corners keep it from sliding sideways. Fix the bottom edge with the two clamp tabs (hole end away from the panel), M2.5 self-tapping into the bosses. Plug in the panel cable.
6. **Bottom plate:** 4 × M3 countersunk screws from below, then the rubber feet in their recesses.

**Power note:** 4-pad USB-C boards often lack the two 5.1 kΩ CC resistors. Without them, a USB-C-to-C cable or charger gives no power; USB-A-to-C cables still work.

"""2-D ray sensor: forward 180 degrees, 121 beams, 0.15..4.5 metres.

No visibility flood fill or current-cell four-wall shortcut. A beam stops at the
first wall. Exact corner ties are conservatively stopped instead of inventing
free passage through a geometrically ambiguous point. Vertical rendering/noise
are outside this test sensor's scope.
"""

from math import cos, floor, inf, radians, sin

from planner_service.simulation.world import World


def observe_edge(seen, known, x, z, dx, dz, bit, opposite, wall):
    for px, pz, flag in ((x, z, bit), (x + dx, z + dz, opposite)):
        if 0 <= px < 16 and 0 <= pz < 16:
            seen[px][pz] |= flag
            if wall:
                known[px][pz] |= flag


def scan(world: World, pose: tuple[int, int, int], seen, known) -> list[dict]:
    x, z, theta = pose
    ox, oz = x + 0.5, z + 0.5
    rays = []
    for index in range(121):
        relative = -90 + index * 1.5
        angle = radians(theta + relative)
        vx, vz = sin(angle), cos(angle)
        sx, sz = (1 if vx >= 0 else -1), (1 if vz >= 0 else -1)
        tx = 0.5 / abs(vx) if abs(vx) > 1e-12 else inf
        tz = 0.5 / abs(vz) if abs(vz) > 1e-12 else inf
        stepx = 1 / abs(vx) if abs(vx) > 1e-12 else inf
        stepz = 1 / abs(vz) if abs(vz) > 1e-12 else inf
        cx, cz = x, z
        ray = {"angle_deg": relative, "hit": False, "distance_m": 4.5, "point": None}
        while min(tx, tz) <= 4.5 + 1e-9:
            if abs(tx - tz) < 1e-9:
                ray["corner_ambiguous"] = True
                ray["distance_m"] = round(min(tx, 4.5), 6)
                break
            if tx < tz:
                distance = tx
                dx, dz, bit, opposite = (sx, 0, 2, 8) if sx > 0 else (sx, 0, 8, 2)
                tx += stepx
            else:
                distance = tz
                dx, dz, bit, opposite = (0, sz, 1, 4) if sz > 0 else (0, sz, 4, 1)
                tz += stepz
            wall = bool(world.walls[cx][cz] & bit)
            if distance >= 0.15:
                observe_edge(seen, known, cx, cz, dx, dz, bit, opposite, wall)
            if wall:
                ray.update(hit=True, distance_m=round(min(distance, 4.5), 6),
                           point={"x": round(ox + distance * vx, 6),
                                  "z": round(oz + distance * vz, 6)})
                break
            cx, cz = cx + dx, cz + dz
        rays.append(ray)
    return rays

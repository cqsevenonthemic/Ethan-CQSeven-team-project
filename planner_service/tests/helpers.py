"""Synthetic maps: no evaluation files or hidden production inputs."""

from planner_service.planner.grid import DIRECTIONS


def open_map():
    seen = [[15] * 16 for _ in range(16)]
    walls = [[0] * 16 for _ in range(16)]
    for x in range(16):
        walls[x][0] |= 4
        walls[x][15] |= 1
    for z in range(16):
        walls[0][z] |= 8
        walls[15][z] |= 2
    return {"seen_walls": seen, "known_walls": walls}


def set_edge(grid, x, z, theta, *, seen=True, wall=False):
    dx, dz, bit, opposite = DIRECTIONS[theta]
    nx, nz = x + dx, z + dz
    for px, pz, flag in ((x, z, bit), (nx, nz, opposite)):
        for key, enabled in (("seen_walls", seen), ("known_walls", wall)):
            if enabled:
                grid[key][px][pz] |= flag
            else:
                grid[key][px][pz] &= ~flag


def observe():
    return dict(
        **open_map(), pose={"x": 7, "z": 6, "theta": 0}, step_count=0,
        carry_slot="s1", slots={
            "s1": {"x": 7, "z": 6, "pred_role": "zhaojun", "verified": False, "state": "carried"},
            "s2": {"x": 8, "z": 8, "pred_role": "quyuan", "verified": True, "state": "delivered"},
        }, event=None,
    )

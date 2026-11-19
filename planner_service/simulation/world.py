"""Evaluator-owned truth. Only sensors and the mock Unity runner may inspect it."""

from dataclasses import dataclass
import random


DELTAS = ((0, 1, 1, 4), (1, 0, 2, 8), (0, -1, 4, 1), (-1, 0, 8, 2))


@dataclass(frozen=True)
class Figure:
    cell: tuple[int, int]
    role: str


@dataclass
class World:
    walls: list[list[int]]
    figures: tuple[Figure, Figure]
    seed: int
    start: tuple[int, int, int] = (0, 0, 0)

    def validate(self):
        if len(self.walls) != 16 or any(len(row) != 16 for row in self.walls):
            raise ValueError("world must be 16x16")
        for x in range(16):
            for z in range(16):
                value = self.walls[x][z]
                if type(value) is not int or not 0 <= value <= 15:
                    raise ValueError("invalid world wall mask")
                for dx, dz, bit, opposite in DELTAS:
                    nx, nz = x + dx, z + dz
                    if 0 <= nx < 16 and 0 <= nz < 16:
                        if bool(value & bit) != bool(self.walls[nx][nz] & opposite):
                            raise ValueError("world walls must be symmetric")
                    elif not value & bit:
                        raise ValueError("world boundaries must be closed")
        if len(self.figures) != 2 or {item.role for item in self.figures} != {"quyuan", "zhaojun"}:
            raise ValueError("world must contain exactly one of each figure")
        if len({item.cell for item in self.figures}) != 2:
            raise ValueError("benchmark figures must occupy different cells")
        if self.start[2] not in (0, 90, 180, 270) or not all(0 <= v < 16 for v in self.start[:2]):
            raise ValueError("invalid start pose")
        reachable = {self.start[:2]}
        pending = list(reachable)
        while pending:
            x, z = pending.pop()
            for dx, dz, bit, _ in DELTAS:
                target = x + dx, z + dz
                if not self.walls[x][z] & bit and target not in reachable:
                    reachable.add(target)
                    pending.append(target)
        required = {item.cell for item in self.figures} | {(7, 7), (8, 8)}
        if not required <= reachable:
            raise ValueError("figures and destinations must be reachable from start")


def generate_world(seed: int) -> World:
    """Connected random spanning-tree mazes; alternating seeds add random loops."""
    rng = random.Random(seed)
    walls = [[15] * 16 for _ in range(16)]
    visited = {(0, 0)}
    stack = [(0, 0)]
    while stack:
        x, z = stack[-1]
        options = [(dx, dz, bit, opposite) for dx, dz, bit, opposite in DELTAS
                   if 0 <= x + dx < 16 and 0 <= z + dz < 16 and (x + dx, z + dz) not in visited]
        if not options:
            stack.pop()
            continue
        dx, dz, bit, opposite = rng.choice(options)
        walls[x][z] &= ~bit
        walls[x + dx][z + dz] &= ~opposite
        visited.add((x + dx, z + dz))
        stack.append((x + dx, z + dz))
    if seed % 2:
        for x in range(16):
            for z in range(16):
                for dx, dz, bit, opposite in DELTAS[:2]:
                    if x + dx < 16 and z + dz < 16 and rng.random() < 0.08:
                        walls[x][z] &= ~bit
                        walls[x + dx][z + dz] &= ~opposite
    cells = rng.sample([(x, z) for x in range(16) for z in range(16)], 2)
    roles = ["quyuan", "zhaojun"]
    rng.shuffle(roles)
    world = World(walls, tuple(Figure(cell, role) for cell, role in zip(cells, roles)), seed)
    world.validate()
    return world

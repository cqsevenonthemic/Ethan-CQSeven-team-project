"""Thin-wall grid; unknown edges may be evaluated but never executed."""

from collections.abc import Iterator

from planner_service.schemas import Action, MapInput, Pose

State = tuple[int, int, int]
DIRECTIONS = {0: (0, 1, 1, 4), 90: (1, 0, 2, 8),
              180: (0, -1, 4, 1), 270: (-1, 0, 8, 2)}


def as_state(pose: Pose) -> State:
    return pose.x, pose.z, pose.theta


def can_forward(grid: MapInput, state: State, *, optimistic: bool = False) -> bool:
    x, z, theta = state
    dx, dz, bit, opposite = DIRECTIONS[theta]
    nx, nz = x + dx, z + dz
    if not (0 <= nx < 16 and 0 <= nz < 16):
        return False
    if grid.known_walls[x][z] & bit or grid.known_walls[nx][nz] & opposite:
        return False
    return optimistic or bool(grid.seen_walls[x][z] & bit
                              and grid.seen_walls[nx][nz] & opposite)


def successors(grid: MapInput, state: State, *, optimistic: bool = False
               ) -> Iterator[tuple[Action, State]]:
    x, z, theta = state
    if can_forward(grid, state, optimistic=optimistic):
        dx, dz, _, _ = DIRECTIONS[theta]
        yield "forward", (x + dx, z + dz, theta)
    yield "turn_left", (x, z, (theta - 90) % 360)
    yield "turn_right", (x, z, (theta + 90) % 360)

"""Snapshot-based search, single-capacity two-target scheduling and correction."""

from planner_service.planner.frontier_explorer import cover_unvisited, explore
from planner_service.planner.grid import as_state
from planner_service.planner.oriented_astar import Path, shortest_path
from planner_service.planner.progress import SCAN_LIMIT, update_progress
from planner_service.planner.task_dijkstra import ordered_cost
from planner_service.planner.task_frontier import ExplorationStrategy, task_frontier
from planner_service.schemas import (
    ActiveResponse, Decision, DESTINATIONS, ObserveRequest, Pose, RejectedEvent,
    TerminalResponse, Trajectory,
)


class FeatureUnavailable(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code
        self.message = message


def decide(
    request: ObserveRequest, *, exploration: ExplorationStrategy = ExplorationStrategy.BASELINE
) -> ActiveResponse | TerminalResponse:
    # Copy the snapshot: repeated identical requests produce identical decisions.
    slots = {key: slot.model_copy(deep=True) for key, slot in request.slots.items()}
    carry = request.carry_slot
    pose = request.pose
    start = as_state(pose)
    remaining = 2 - sum(slot.state == "delivered" for slot in slots.values())
    progress, stale_scan = update_progress(request)
    decision = Decision(reason="No task switch required")
    if isinstance(request.event, RejectedEvent):
        rejected = slots[request.event.slot_id]
        rejected.pred_role = "quyuan" if rejected.pred_role == "zhaojun" else "zhaojun"
        rejected.verified = True
        for key, slot in slots.items():
            if key != request.event.slot_id and not slot.verified and slot.pred_role == rejected.pred_role:
                slot.pred_role = "quyuan" if rejected.pred_role == "zhaojun" else "zhaojun"
        decision.reason = "Rejected identity corrected under the two-figure task constraint"

    def response(path: Path, status: str, reason: str):
        common = dict(
            carry_slot=carry, slots=slots,
            plan_path=[(x, z) for x, z, _ in path.states],
            decision=decision.model_copy(update={"reason": decision.reason + "; " + reason}), remaining_tasks=remaining,
            trajectory=Trajectory(
                poses=[Pose(x=x, z=z, theta=theta) for x, z, theta in path.states],
                actions=path.actions, steps=len(path.actions)),
            observation_seq=request.observation_seq, progress=progress,
        )
        if status in ("planning", "exploring"):
            return ActiveResponse(action=path.actions[0], status=status, **common)
        return TerminalResponse(status=status, done=status == "completed", **common)

    if remaining == 0:
        return response(Path([start], []), "completed", "Both targets were confirmed delivered by Unity")
    waiting_here = [key for key, slot in slots.items()
                    if slot.state == "waiting" and (slot.x, slot.z) == start[:2]]
    if carry is None and waiting_here:
        carry = min(waiting_here)
        slots[carry].state = "carried"
        waiting_here.remove(carry)
    if carry and waiting_here:
        other = min(waiting_here)
        first_goal = DESTINATIONS[slots[carry].pred_role]
        second_goal = DESTINATIONS[slots[other].pred_role]
        keep = ordered_cost(request, start, [first_goal, start[:2], second_goal])
        switch = ordered_cost(request, start, [second_goal, start[:2], first_goal])
        decision.cost_keep, decision.cost_switch = keep, switch
        decision.cost_scope = "confirmed_map"
        # Switching must be a proven strict improvement. Unknown/tied costs keep
        # the current load; no hidden map or future target location is consulted.
        if switch is not None and (keep is None or switch < keep):
            slots[carry].state = "waiting"
            slots[carry].x, slots[carry].z = start[:2]
            carry = other
            slots[carry].state = "carried"
            decision.switched = True
            decision.reason = "Switch has a lower complete hypothetical cost on the confirmed map"
        else:
            decision.reason = "Keep current load: no proven strict switch improvement on the confirmed map"

    path = None
    goal = None
    if carry:
        goal = DESTINATIONS[slots[carry].pred_role]
        if start[:2] == goal:
            return response(Path([start], []), "awaiting_delivery", "Unity must verify delivery before the next request")
        path = shortest_path(request, start, goal)
    else:
        candidates = []
        for key, slot in slots.items():
            if slot.state == "waiting":
                candidate = shortest_path(request, start, (slot.x, slot.z))
                if candidate:
                    candidates.append((len(candidate.actions), key, candidate))
        if candidates:
            path = min(candidates, key=lambda item: (item[0], item[1]))[2]
    if stale_scan:
        return response(Path([start], []), "awaiting_observation", "Action completed without a newer observation sequence")
    if path and path.actions:
        return response(path, "planning", "Shortest unit-cost route on confirmed open edges")
    if request.visited_cells is None:
        return response(Path([start], []), "awaiting_observation", "Search requires caller-owned visited_cells; wall visibility is not search coverage")
    if goal is not None and (exploration == ExplorationStrategy.CARRIED_TASK
                             or (exploration == ExplorationStrategy.KNOWN_TASKS and len(slots) == 2)):
        can_scan_here = (progress.scan_attempts < SCAN_LIMIT
                         and (request.progress is not None or request.step_count == 0))
        directed = task_frontier(request, start, goal, allow_local_scan=can_scan_here)
        if directed:
            progress.pending_scan = directed.states[-1][:2] == start[:2]
            return response(directed, "exploring",
                            "Observe an unknown edge on a low-cost possible route to the current destination")
    coverage = cover_unvisited(request, start, set(request.visited_cells))
    if coverage:
        return response(coverage, "exploring", "Enter a reachable cell not yet physically searched")
    frontier = explore(request, start)
    if frontier:
        scanning_here = frontier.states[-1][:2] == start[:2]
        if scanning_here:
            if progress.scan_attempts >= SCAN_LIMIT:
                return response(Path([start], []), "awaiting_observation", "Four scan rotations made no information progress; recover the sensor before resuming")
            if request.progress is None and request.step_count > 0:
                return response(Path([start], []), "awaiting_observation", "Echo progress to bound repeated scan actions")
            progress.pending_scan = True
        return response(frontier, "exploring", "Observe an unknown edge reachable through confirmed open edges")
    return response(Path([start], []), "unreachable", "Reachable component searched; no remaining target route or observation frontier")

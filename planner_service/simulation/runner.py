"""Mock Unity owns truth and execution; decision_fn receives JSON observations only."""

from collections.abc import Callable
from copy import deepcopy
from dataclasses import dataclass, field
from time import perf_counter

from planner_service.schemas import ObserveRequest
from planner_service.service import decide
from planner_service.simulation.mock_recognizer import MOCK_IMAGES, recognize_mock
from planner_service.simulation.sensor import scan
from planner_service.simulation.world import World


def local_decision(payload: dict) -> dict:
    return decide(ObserveRequest.model_validate(payload)).model_dump(mode="json")


@dataclass
class RunResult:
    seed: int
    success: bool = False
    failure: str | None = None
    steps: int = 0
    forward: int = 0
    turns: int = 0
    discoveries: list[str] = field(default_factory=list)
    rejections: int = 0
    switches: int = 0
    elapsed_s: float = 0
    trace: list[dict] = field(default_factory=list)


def run_world(world: World, *, invert_first: bool = False, freeze_sensor: bool = False,
              decision_fn=local_decision, max_actions: int = 4096,
              record_sensors: bool = False,
              recognize_figure: Callable[[str], dict] | None = None) -> RunResult:
    """Truth stays in this evaluator. Safeguards fail, never bypass, bad actions."""
    if recognize_figure is not None and invert_first:
        raise ValueError("Recognition fault injection applies only to the mock recognizer")
    world.validate()
    result = RunResult(seed=world.seed)
    started = perf_counter()
    x, z, theta = world.start
    seen, known = [[0] * 16 for _ in range(16)], [[0] * 16 for _ in range(16)]
    visited, slots, carry, progress = set(), {}, None, None
    discovered = {}  # evaluator figure index -> observation slot id
    true_roles = {}  # evaluator-only; never inserted in a decision payload
    accepted_deliveries = set()
    seq = 0
    previous_observation = [[0] * 16 for _ in range(16)]
    # Includes non-motion delivery confirmations; bound is structural, not a timeout.
    for _ in range(max_actions + 10):
        visited.add((x, z))
        seq += 1
        rays = [] if freeze_sensor else scan(world, (x, z, theta), seen, known)
        updates = []
        for px in range(16):
            for pz in range(16):
                packed = seen[px][pz] | (known[px][pz] << 4)
                if packed != previous_observation[px][pz]:
                    updates.append([px, pz, seen[px][pz], known[px][pz]])
                    previous_observation[px][pz] = packed
        event = None
        for index, figure in enumerate(world.figures):
            if figure.cell == (x, z) and index not in discovered:
                slot_id = "s" + str(len(discovered) + 1)
                if recognize_figure is None:
                    prediction = recognize_mock(MOCK_IMAGES[figure.role], invert=invert_first and not discovered)
                else:
                    # The role selects an evaluator-owned portrait at contact.
                    # The live recognition request carries only the image bytes.
                    prediction = dict(recognize_figure(figure.role))
                discovered[index] = slot_id
                true_roles[slot_id] = figure.role
                confidence = prediction.pop("confidence")
                slots[slot_id] = dict(x=x, z=z, verified=False, state="waiting", **prediction)
                event = dict(type="trigger_discovered", slot_id=slot_id, x=x, z=z,
                             pred_role=prediction["pred_role"], confidence=confidence,
                             recognition_id=prediction["recognition_id"])
                result.discoveries.append(figure.role)
        # Rejection is sent with the unchanged pre-correction carried snapshot.
        if carry and (x, z) in ((7, 7), (8, 8)):
            predicted_destination = (8, 8) if slots[carry]["pred_role"] == "quyuan" else (7, 7)
            if (x, z) == predicted_destination:
                actual_destination = (8, 8) if true_roles[carry] == "quyuan" else (7, 7)
                if (x, z) != actual_destination:
                    event = dict(type="delivery_rejected", slot_id=carry, rejected_at={"x": x, "z": z})
                    result.rejections += 1
        payload = dict(pose=dict(x=x, z=z, theta=theta), seen_walls=deepcopy(seen),
                       known_walls=deepcopy(known), step_count=result.steps, slots=deepcopy(slots),
                       carry_slot=carry, visited_cells=sorted(visited), observation_seq=seq,
                       progress=deepcopy(progress), event=event)
        # Pass plain observations and defensive copies, never World/Figure/true_roles.
        # API-adapter tests also exercise this snapshot through the JSON HTTP boundary.
        response = decision_fn(payload)
        record = dict(observation_seq=seq, pose_before=payload["pose"],
                      visited_cell=[x, z], wall_updates=updates, event=event,
                      action=response["action"], executed=False, status=response["status"],
                      slots=deepcopy(response["slots"]), carry_slot=response["carry_slot"],
                      decision=deepcopy(response["decision"]), progress=deepcopy(response["progress"]))
        record.update(pose_after=dict(x=x, z=z, theta=theta), step_count=result.steps,
                      environment_slots=deepcopy(slots), environment_carry_slot=carry)
        if record_sensors:
            record["sensor_rays"] = rays
        result.trace.append(record)
        if set(response["slots"]) != set(slots):
            result.failure = "policy_invented_or_deleted_target"
            break
        if any(slot["state"] == "delivered" and key not in accepted_deliveries
               for key, slot in response["slots"].items()):
            result.failure = "unconfirmed_delivery"
            break
        if any(slot["verified"] and slot["pred_role"] != true_roles[key]
               for key, slot in response["slots"].items()):
            result.failure = "false_verification"
            break
        slots, carry, progress = deepcopy(response["slots"]), response["carry_slot"], deepcopy(response["progress"])
        if sum(slot["state"] == "carried" for slot in slots.values()) > 1:
            result.failure = "capacity_violation"
            break
        if response["decision"]["switched"]:
            result.switches += 1
        if response["done"]:
            result.success = (len(slots) == 2 and all(slot["state"] == "delivered" for slot in slots.values())
                              and len(accepted_deliveries) == 2
                              and set(true_roles.values()) == {"quyuan", "zhaojun"})
            if not result.success:
                result.failure = "false_completion"
            record.update(pose_after=dict(x=x, z=z, theta=theta), step_count=result.steps)
            break
        if response["status"] == "awaiting_delivery":
            if carry is None:
                result.failure = "delivery_without_load"
                break
            expected = (8, 8) if true_roles[carry] == "quyuan" else (7, 7)
            if (x, z) == expected:
                slots[carry].update(state="delivered", verified=True)
                accepted_deliveries.add(carry)
                record["delivery_accepted"] = carry
                carry = None
            else:
                # On the next request the evaluator sends delivery_rejected.
                record["delivery_accepted"] = None
            record.update(pose_after=dict(x=x, z=z, theta=theta), step_count=result.steps,
                          environment_slots=deepcopy(slots), environment_carry_slot=carry)
            continue
        action = response["action"]
        if action is None:
            result.failure = response["status"]
            record.update(pose_after=dict(x=x, z=z, theta=theta), step_count=result.steps)
            break
        if result.steps >= max_actions:
            result.failure = "action_budget_exceeded"
            break
        if action == "forward":
            dx, dz, bit = {0: (0, 1, 1), 90: (1, 0, 2), 180: (0, -1, 4), 270: (-1, 0, 8)}[theta]
            if not seen[x][z] & bit or world.walls[x][z] & bit:
                result.failure = "unsafe_forward"
                break
            x, z = x + dx, z + dz
            result.forward += 1
        elif action in ("turn_left", "turn_right"):
            theta = (theta + (-90 if action == "turn_left" else 90)) % 360
            result.turns += 1
        else:
            result.failure = "invalid_action"
            break
        result.steps += 1
        if carry:
            slots[carry].update(x=x, z=z)
        record.update(pose_after=dict(x=x, z=z, theta=theta), step_count=result.steps, executed=True,
                      environment_slots=deepcopy(slots), environment_carry_slot=carry)
    else:
        result.failure = "transition_budget_exceeded"
    result.elapsed_s = round(perf_counter() - started, 6)
    return result

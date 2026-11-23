"""Validated v1 inputs; task/environment state always belongs to the caller."""

import base64
import binascii
from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

SlotId = Literal["s1", "s2"]
Role = Literal["quyuan", "zhaojun"]
Action = Literal["forward", "turn_left", "turn_right"]
Coordinate = Annotated[int, Field(strict=True, ge=0, le=15)]
Mask = Annotated[int, Field(strict=True, ge=0, le=15)]
MaskRow = Annotated[list[Mask], Field(min_length=16, max_length=16)]
WallMap = Annotated[list[MaskRow], Field(min_length=16, max_length=16)]
DESTINATIONS = {"quyuan": (8, 8), "zhaojun": (7, 7)}


class Model(BaseModel):
    # Permit additive fields from a newer Unity client without discarding core validation.
    model_config = ConfigDict(extra="ignore", allow_inf_nan=False)


class Point(Model):
    x: Coordinate
    z: Coordinate


class Pose(Point):
    theta: Annotated[int, Field(strict=True)]

    @model_validator(mode="after")
    def cardinal_direction(self):
        if self.theta not in (0, 90, 180, 270):
            raise ValueError("theta must be 0, 90, 180 or 270")
        return self


class Slot(Point):
    pred_role: Role
    verified: Annotated[bool, Field(strict=True)] = False
    state: Literal["waiting", "carried", "delivered"]
    recognition_id: Annotated[str, Field(min_length=1, max_length=128)] | None = None
    recognition_source: Literal["mock", "onnx"] | None = None

    @model_validator(mode="after")
    def delivered_at_destination(self):
        if self.state == "delivered":
            if not self.verified or (self.x, self.z) != DESTINATIONS[self.pred_role]:
                raise ValueError("delivered slots must be verified at their destination")
        return self


class DiscoveredEvent(Point):
    type: Literal["trigger_discovered"]
    slot_id: SlotId
    pred_role: Role
    confidence: Annotated[float, Field(ge=0, le=1)]
    recognition_id: Annotated[str, Field(min_length=1, max_length=128)] | None = None


class RejectedEvent(Model):
    type: Literal["delivery_rejected"]
    slot_id: SlotId
    rejected_at: Point


Event = Annotated[DiscoveredEvent | RejectedEvent, Field(discriminator="type")]


class MapInput(Model):
    seen_walls: WallMap
    known_walls: WallMap

    @model_validator(mode="after")
    def consistent_walls(self):
        for x in range(16):
            for z in range(16):
                seen, known = self.seen_walls[x][z], self.known_walls[x][z]
                if known & seen != known:
                    raise ValueError("known_walls must be a subset of seen_walls")
                for bit, dx, dz, opposite in ((1, 0, 1, 4), (2, 1, 0, 8),
                                               (4, 0, -1, 1), (8, -1, 0, 2)):
                    nx, nz = x + dx, z + dz
                    if 0 <= nx < 16 and 0 <= nz < 16:
                        if bool(seen & bit) != bool(self.seen_walls[nx][nz] & opposite):
                            raise ValueError("adjacent seen_walls must be symmetric")
                        if bool(known & bit) != bool(self.known_walls[nx][nz] & opposite):
                            raise ValueError("adjacent known_walls must be symmetric")
                    elif seen & bit and not known & bit:
                        raise ValueError("an observed outer boundary must be a wall")
        return self


class ScanProgress(Model):
    """Caller-owned, echoed snapshot; never a hidden server session."""

    fingerprint: Annotated[str, Field(pattern=r"^(?:[0-9a-f]{64})?$")] = ""
    last_observation_seq: Annotated[int, Field(strict=True, ge=0)] = 0
    last_step_count: Annotated[int, Field(strict=True, ge=0)] = 0
    scan_attempts: Annotated[int, Field(strict=True, ge=0, le=4)] = 0
    pending_scan: bool = False


class ObserveRequest(MapInput):
    pose: Pose
    step_count: Annotated[int, Field(strict=True, ge=0)]
    carry_slot: SlotId | None = None
    slots: dict[SlotId, Slot] = Field(default_factory=dict)
    event: Event | None = None
    visited_cells: Annotated[list[tuple[Coordinate, Coordinate]], Field(max_length=256)] | None = None
    observation_seq: Annotated[int, Field(strict=True, ge=0)] = 0
    progress: ScanProgress | None = None

    @model_validator(mode="after")
    def consistent_task(self):
        carried = [key for key, slot in self.slots.items() if slot.state == "carried"]
        if carried != ([] if self.carry_slot is None else [self.carry_slot]):
            raise ValueError("carry_slot must identify the only carried slot")
        for slot in self.slots.values():
            if slot.state == "carried" and (slot.x, slot.z) != (self.pose.x, self.pose.z):
                raise ValueError("carried slot coordinates must match pose")
        verified = [slot.pred_role for slot in self.slots.values() if slot.verified]
        if len(verified) != len(set(verified)):
            raise ValueError("verified slots must represent different historical figures")
        if self.visited_cells is not None:
            self.visited_cells = sorted(set(self.visited_cells))
            if (self.pose.x, self.pose.z) not in self.visited_cells:
                raise ValueError("visited_cells must include the actually occupied current cell")
        if self.progress is not None:
            if (self.progress.last_step_count > self.step_count
                    or self.progress.last_observation_seq > self.observation_seq):
                raise ValueError("progress cannot refer to future steps or observations")
        if self.event:
            slot = self.slots.get(self.event.slot_id)
            if slot is None:
                raise ValueError("event slot must already be included in slots")
            if isinstance(self.event, DiscoveredEvent):
                if ((self.event.x, self.event.z) != (self.pose.x, self.pose.z)
                        or (slot.x, slot.z) != (self.event.x, self.event.z)
                        or slot.pred_role != self.event.pred_role
                        or (self.event.recognition_id is not None
                            and slot.recognition_id != self.event.recognition_id)
                        or slot.state == "delivered"):
                    raise ValueError("discovery event must match the current cell and slot")
            elif (slot.verified or self.event.slot_id != self.carry_slot
                  or (self.event.rejected_at.x, self.event.rejected_at.z)
                  != (self.pose.x, self.pose.z)
                  or (self.pose.x, self.pose.z) != DESTINATIONS[slot.pred_role]):
                raise ValueError("rejection must match the carried slot's predicted destination")
            elif any(other.verified and other.pred_role != slot.pred_role
                     for key, other in self.slots.items() if key != self.event.slot_id):
                raise ValueError("rejection contradicts an already verified figure")
        return self


class RecognizeRequest(Model):
    # 4 MiB decoded image maximum; decoding/format verification belongs to vision integration.
    image_base64: Annotated[str, Field(min_length=1, max_length=5_592_408)]

    @model_validator(mode="after")
    def valid_encoding(self):
        try:
            content = base64.b64decode(self.image_base64, validate=True)
        except (ValueError, binascii.Error) as exc:
            raise ValueError("image_base64 must be raw standard Base64 without a data URI") from exc
        if not content or len(content) > 4 * 1024 * 1024:
            raise ValueError("decoded image must be 1 byte to 4 MiB")
        if not (content.startswith(b"\x89PNG\r\n\x1a\n") or content.startswith(b"\xff\xd8\xff")):
            raise ValueError("image must have a PNG or JPEG signature")
        return self


class RecognizeResponse(Model):
    pred_role: Role
    confidence: Annotated[float, Field(ge=0, le=1)]
    recognition_id: str
    recognition_source: Literal["onnx"]
    image_sha256: str
    status: Literal["recognized", "low_confidence"]


class InitialTargets(Model):
    quyuan: Point
    zhaojun: Point


class SummaryRequest(MapInput):
    start_pose: Pose
    initial_targets: InitialTargets


class Decision(Model):
    switched: bool = False
    reason: str
    cost_keep: int | None = None
    cost_switch: int | None = None
    cost_scope: Literal["not_evaluated", "confirmed_map"] = "not_evaluated"


class Trajectory(Model):
    poses: list[Pose]
    actions: list[Action]
    steps: int


class EpisodeRecord(Model):
    episode: int
    behavior: Literal["astar_demonstration", "astar_guided", "epsilon_greedy"]
    epsilon: float
    reward: int
    completed: bool
    failure: Literal["action_budget_exceeded"] | None = None
    turns: int
    switch_steps: list[int]
    trajectory: Trajectory


class OptimalSteps(Model):
    total: int
    forward: int
    turn: int


SummaryPath = list[tuple[Coordinate, Coordinate]]


class SummaryResponse(Model):
    explored_trajectories: Annotated[list[SummaryPath], Field(min_length=30, max_length=50)]
    episode_steps: list[int]
    optimal_path: SummaryPath
    optimal_actions: list[Action]
    optimal_steps: OptimalSteps
    episode_records: list[EpisodeRecord]
    optimal_episode: int
    training_seed: int
    training_episodes: int
    exact_steps: int
    map_scope: Literal["complete_map", "confirmed_subgraph"]
    optimality_proven: bool


class ActiveResponse(Model):
    action: Action
    carry_slot: SlotId | None
    slots: dict[SlotId, Slot]
    plan_path: list[tuple[int, int]]
    decision: Decision
    done: Literal[False] = False
    status: Literal["planning", "exploring"]
    remaining_tasks: int
    trajectory: Trajectory
    observation_seq: int = 0
    progress: ScanProgress = Field(default_factory=ScanProgress)


class TerminalResponse(Model):
    action: None = None
    carry_slot: SlotId | None
    slots: dict[SlotId, Slot]
    plan_path: list[tuple[int, int]]
    decision: Decision
    done: bool
    status: Literal["completed", "unreachable", "awaiting_delivery", "awaiting_observation"]
    remaining_tasks: int
    trajectory: Trajectory
    observation_seq: int = 0
    progress: ScanProgress = Field(default_factory=ScanProgress)


class ErrorDetail(Model):
    code: str
    message: str


class ErrorResponse(Model):
    error: ErrorDetail

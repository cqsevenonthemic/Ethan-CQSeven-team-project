"""Observe information progress across caller-supplied immutable snapshots."""

from hashlib import sha256
import json

from planner_service.schemas import ObserveRequest, ScanProgress

SCAN_LIMIT = 4


def update_progress(request: ObserveRequest) -> tuple[ScanProgress, bool]:
    fingerprint = sha256(json.dumps(
        [request.seen_walls, request.known_walls, request.visited_cells],
        separators=(",", ":"),
    ).encode("ascii")).hexdigest()
    previous = request.progress
    attempts = 0
    stale = False
    if previous is not None:
        delta = request.step_count - previous.last_step_count
        stale = delta > 0 and request.observation_seq == previous.last_observation_seq
        if previous.fingerprint == fingerprint:
            attempts = previous.scan_attempts
            if previous.pending_scan:
                attempts = min(SCAN_LIMIT, attempts + delta)
    return ScanProgress(
        fingerprint=fingerprint, last_observation_seq=request.observation_seq,
        last_step_count=request.step_count, scan_attempts=attempts,
    ), stale

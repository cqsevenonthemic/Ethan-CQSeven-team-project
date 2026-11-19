# Mock planner fixtures

These fixed fixtures are illustrative offline demo data, not evaluation data. `PlannerHttpClient` first probes the local API without requesting an action; it selects this Mock mode only when the planner endpoint cannot be reached. HTTP errors from a reachable planner stay in Live mode and stop automation.

`mock_evaluation.json` provides two fixed station positions for the offline demo. `action_sequence.json` is a fixture-only action schedule for the fixed Unity maze. The next action is indexed by the request's `step_count`, so retrying an identical snapshot returns the same action instead of consuming another step. Runtime responses add the v1 status, trajectory, observation sequence, and progress fields from the request snapshot.

`sample_recognition*.json` and `sample_summary.json` are successful local demo fixtures. They do not represent the live backend, where `/recognize` and `/plan_summary` currently return 501 until those features are implemented.

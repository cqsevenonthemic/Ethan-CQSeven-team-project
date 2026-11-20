import pytest

from planner_service.planner.grid import as_state
from planner_service.schemas import SummaryRequest, SummaryResponse
from planner_service.simulation.delivery import AStarDelivery, DeliveryEnvironment, Move, exact_delivery
from planner_service.simulation.q_learning import evaluate, load_policy, save_policy, train
from planner_service.simulation.rl_experiment import full_map_request
from planner_service.summary import experiment, training_slot
from planner_service.tests.helpers import set_edge
from planner_service.tests.test_api import client, load


def assert_physical_replay(request, actions, poses):
    # Check both wall masks independently of the training environment.
    x, z, theta = as_state(request.start_pose)
    states = [(x, z, theta)]
    for action in actions:
        if action == "forward":
            dx, dz, bit, opposite = ((0, 1, 1, 4), (1, 0, 2, 8),
                                     (0, -1, 4, 1), (-1, 0, 8, 2))[theta // 90]
            nx, nz = x + dx, z + dz
            assert 0 <= nx < 16 and 0 <= nz < 16
            assert request.seen_walls[x][z] & bit and request.seen_walls[nx][nz] & opposite
            assert not (request.known_walls[x][z] & bit or request.known_walls[nx][nz] & opposite)
            x, z = nx, nz
        else:
            assert action in ("turn_left", "turn_right")
            theta = (theta + (90 if action == "turn_right" else -90)) % 360
        states.append((x, z, theta))
    assert [as_state(pose) for pose in poses] == states


@pytest.mark.parametrize("partial", [False, True])
def test_summary_contract_and_every_episode_from_samples(partial):
    payload = load("sample_summary_request.json")
    if partial:
        payload["start_pose"] = {"x": 2, "z": 5, "theta": 90}
        set_edge(payload, 2, 5, 90, seen=False)
        set_edge(payload, 2, 5, 0, wall=True)
    request = SummaryRequest.model_validate(payload)
    response = client.post("/plan_summary", json=payload)
    assert response.status_code == 200
    body = SummaryResponse.model_validate_json(response.content)
    assert body.map_scope == ("confirmed_subgraph" if partial else "complete_map")
    assert len(body.episode_steps) == len(body.episode_records) == len(body.explored_trajectories) == 40
    episode_ids = [record.episode for record in body.episode_records]
    assert episode_ids == sorted(set(episode_ids))
    assert all(1 <= number <= body.training_episodes for number in episode_ids)
    env = DeliveryEnvironment(request)
    if not partial:
        # A second deterministic training run checks which execution produced
        # each claimed episode ID independently of the HTTP selection/cache.
        source = experiment(request, seed=body.training_seed,
                            episodes=body.training_episodes).training.episodes
    for path, steps, record in zip(body.explored_trajectories, body.episode_steps, body.episode_records):
        actions = record.trajectory.actions
        switch_steps = set(record.switch_steps)
        assert len(switch_steps) == len(record.switch_steps)
        assert all(1 <= offset <= steps for offset in switch_steps)
        assert steps == record.trajectory.steps == len(actions) == len(path) - 1
        assert record.turns == sum(action != "forward" for action in actions)
        assert record.reward == -steps + (25 if record.completed else 0)
        assert_physical_replay(request, actions, record.trajectory.poses)
        moves = [Move(action, i in switch_steps) for i, action in enumerate(actions, 1)]
        states = env.replay(moves)
        assert [state.pose[:2] for state in states] == path
        assert states[-1].completed == record.completed
        if not partial:
            actual = source[record.episode - 1]
            assert (actual.episode, actual.moves, actual.states) == (record.episode, moves, states)
            assert (actual.behavior, actual.epsilon) == (record.behavior, record.epsilon)
    best = body.episode_records[episode_ids.index(body.optimal_episode)]
    assert best.completed
    assert body.optimal_path == body.explored_trajectories[episode_ids.index(body.optimal_episode)]
    assert body.optimal_actions == best.trajectory.actions
    assert body.optimal_steps.total == body.optimal_steps.forward + body.optimal_steps.turn
    assert body.optimal_steps.total == min(record.trajectory.steps for record in body.episode_records
                                         if record.completed)
    assert body.optimal_steps.turn == best.turns
    assert body.optimality_proven == (body.exact_steps == body.optimal_steps.total)
    assert exact_delivery(env).steps == body.exact_steps
    assert client.post("/plan_summary", json=payload).content == response.content


def test_summary_can_return_a_best_observed_route_without_an_optimality_proof():
    payload = full_map_request(16).model_dump(mode="json")
    # Seed 16 has a confirmed wall here. Making it unknown leaves the executable
    # graph unchanged, but the caller's map can no longer certify all walls.
    assert payload["known_walls"][0][0] & 1
    set_edge(payload, 0, 0, 0, seen=False)
    response = client.post("/plan_summary", json=payload)
    assert response.status_code == 200
    body = SummaryResponse.model_validate_json(response.content)
    assert body.map_scope == "confirmed_subgraph"
    assert body.exact_steps < body.optimal_steps.total
    assert body.optimality_proven is False


def test_summary_training_does_not_feed_the_online_unknown_map():
    payload = load("sample_summary_request.json")
    online = dict(pose=payload["start_pose"], seen_walls=[[0] * 16 for _ in range(16)],
                  known_walls=[[0] * 16 for _ in range(16)], step_count=0,
                  visited_cells=[[0, 0]], observation_seq=1)
    before = client.post("/next_action", json=online)
    assert before.status_code == 200
    assert before.json()["action"] in ("turn_left", "turn_right")
    assert before.json()["slots"] == {}
    assert before.json()["remaining_tasks"] == 2
    # Change both hidden origins to ensure this is a different cold summary.
    payload["initial_targets"] = {"quyuan": {"x": 0, "z": 1}, "zhaojun": {"x": 1, "z": 0}}
    assert client.post("/plan_summary", json=payload).status_code == 200
    after = client.post("/next_action", json={**online, "initial_targets": payload["initial_targets"]})
    assert after.content == before.content


def test_unreachable_summary_and_busy_do_not_fabricate_paths():
    payload = load("sample_summary_request.json")
    payload["seen_walls"] = [[0] * 16 for _ in range(16)]
    payload["known_walls"] = [[0] * 16 for _ in range(16)]
    with training_slot:
        response = client.post("/plan_summary", json=payload)
        assert response.status_code == 503
        assert response.json()["error"]["code"] == "SUMMARY_BUSY"
    response = client.post("/plan_summary", json=payload)
    assert response.status_code == 422
    assert response.json()["error"]["code"] == "INVALID_INPUT"


def test_q_learning_saved_and_evaluated_without_a_guide(tmp_path):
    request = SummaryRequest.model_validate(load("sample_summary_request.json"))
    result = experiment(request, seed=20261120, episodes=80)
    repeated = train(result.env, AStarDelivery(result.env), seed=20261120,
                     episodes=80, max_steps=result.max_steps)
    records = result.training.episodes
    assert repeated.q == result.training.q
    assert repeated.episodes == records
    assert result.training.updates == sum(record.steps for record in records)
    assert any(record.behavior == "epsilon_greedy" and record.completed for record in records)
    path = tmp_path / "policy.json"
    save_policy(path, request, result.training.q, seed=20261120, episodes=80, max_steps=result.max_steps)
    env = DeliveryEnvironment(request)
    policy, max_steps = load_policy(path, env)
    assert policy == result.training.q
    evaluation = evaluate(env, policy, seed=49, max_steps=max_steps)
    assert evaluation.completed
    assert env.replay(evaluation.moves) == evaluation.states
    request.start_pose.theta = 90
    with pytest.raises(ValueError, match="different version or scenario"):
        load_policy(path, DeliveryEnvironment(request))


def test_optional_load_switch_and_delivery_at_contact():
    payload = load("sample_summary_request.json")
    payload.update(start_pose={"x": 7, "z": 6, "theta": 0}, initial_targets={
        "quyuan": {"x": 7, "z": 6}, "zhaojun": {"x": 7, "z": 7},
    })
    env = DeliveryEnvironment(SummaryRequest.model_validate(payload))
    before_contact, contact, after_switch = env.replay([Move("forward"), Move("turn_right", True)])
    assert before_contact.carry == contact.carry == after_switch.carry == 0
    assert contact.waiting == (None, (7, 7))
    assert after_switch.waiting == (None, None)
    assert after_switch.pose == (7, 7, 90)

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    public enum DemoAct
    {
        Act1_OnlineMission,
        Act2_RlExploration,
        Act3_OptimalReplay,
        Completed
    }

    /// <summary>Executes one planner action from a single immutable Unity snapshot at a time.</summary>
    public sealed class PlannerTaskDriver : MonoBehaviour
    {
        [SerializeField] private RobotController robot;
        [SerializeField] private LidarSimulator lidar;
        [SerializeField] private PlannerHttpClient plannerClient;
        [SerializeField] private StateMachineTrigger stateMachine;
        [SerializeField] private TrajectoryVisualizer visualizer;

        private PlannerProgressDto _progress;
        private string _lastObservationFingerprint;
        private string _awaitingInformationFingerprint;
        private long _observationSequence;
        private long _snapshotGeneration;
        private long _lastExecutedGeneration = -1;
        private bool _initialized;
        private bool _requestInFlight;
        private bool _stopped;
        private bool _awaitingInformation;
        private bool _postActionScanPending;
        private DemoAct _currentAct = DemoAct.Act1_OnlineMission;
        private bool _lastReplayFailed;

        public event Action<TrajectoryDto> PlannerTrajectoryPreviewChanged;
        public event Action<RobotPose[]> ActualPathChanged;
        public event Action<string> StatusChanged;
        public event Action<DemoAct> ActChanged;
        public event Action<PlannerDecisionDto, RobotPose> DecisionEvaluated;
        public event Action<int, int, int, int> RlProgressUpdated;
        public event Action<PlanSummaryResponseDto> PlanSummaryLoaded;

        public DemoAct CurrentAct { get { return _currentAct; } }
        public bool RequestInFlight { get { return _requestInFlight; } }
        public bool IsStopped { get { return _stopped; } }
        public long ObservationSequence { get { return _observationSequence; } }
        public TrajectoryVisualizer Visualizer { get { return visualizer; } }
        public PlannerHttpClient PlannerClient { get { return plannerClient; } }

        private void Start()
        {
            if (robot == null)
                robot = FindFirstObjectByType<RobotController>();
            if (lidar == null)
                lidar = FindFirstObjectByType<LidarSimulator>();
            if (plannerClient == null)
                plannerClient = FindFirstObjectByType<PlannerHttpClient>();
            if (stateMachine == null)
                stateMachine = FindFirstObjectByType<StateMachineTrigger>();
            if (visualizer == null)
                visualizer = FindFirstObjectByType<TrajectoryVisualizer>();

            if (robot == null || lidar == null || plannerClient == null || stateMachine == null)
            {
                StopWithError("PlannerTaskDriver requires RobotController, LidarSimulator, PlannerHttpClient, and StateMachineTrigger.");
                return;
            }

            stateMachine.InitializationCompleted += OnInitializationCompleted;
            if (stateMachine.IsReady)
                OnInitializationCompleted(true, stateMachine.StatusMessage);
            else if (!string.IsNullOrEmpty(stateMachine.ErrorMessage))
                StopWithError(stateMachine.ErrorMessage);
        }

        private void OnDestroy()
        {
            if (stateMachine != null)
            {
                stateMachine.InitializationCompleted -= OnInitializationCompleted;
                stateMachine.RecognitionCompleted -= OnRecognitionCompleted;
                stateMachine.DeliveryValidated -= OnDeliveryValidated;
            }
            if (_initialized)
            {
                robot.PoseChanged -= OnPoseChanged;
                robot.ActionStarted -= OnActionStarted;
                robot.ActionCompleted -= OnActionCompleted;
                lidar.ScanCompleted -= OnScanCompleted;
            }
        }

        private void OnInitializationCompleted(bool succeeded, string message)
        {
            if (!succeeded)
            {
                StopWithError(message);
                return;
            }
            if (_initialized)
                return;

            robot.PoseChanged += OnPoseChanged;
            robot.ActionStarted += OnActionStarted;
            robot.ActionCompleted += OnActionCompleted;
            lidar.ScanCompleted += OnScanCompleted;
            stateMachine.RecognitionCompleted += OnRecognitionCompleted;
            stateMachine.DeliveryValidated += OnDeliveryValidated;
            _initialized = true;
            lidar.Scan();
        }

        private void OnPoseChanged(RobotPose pose)
        {
            if (_currentAct != DemoAct.Act1_OnlineMission)
                return;
            _snapshotGeneration++;
        }

        private void OnActionStarted(RobotAction action)
        {
            if (_currentAct != DemoAct.Act1_OnlineMission)
                return;
            _snapshotGeneration++;
            _postActionScanPending = true;
        }

        private void OnActionCompleted(RobotActionResult result)
        {
            if (_currentAct != DemoAct.Act1_OnlineMission)
                return;

            ActualPathChanged?.Invoke(robot.CopyExecutedPoses());
            if (result.BlockedByWall)
            {
                StopWithError("The planner requested a forward move blocked by a wall; automation stopped.");
                return;
            }
            if (_postActionScanPending)
                StartCoroutine(EnsurePostActionScan());
        }

        private IEnumerator EnsurePostActionScan()
        {
            yield return null;
            if (_postActionScanPending && robot != null && lidar != null && !robot.IsActionInProgress)
                lidar.Scan();
        }

        private void OnScanCompleted(LidarScan scan)
        {
            if (!_initialized || scan == null || _currentAct != DemoAct.Act1_OnlineMission)
                return;

            _observationSequence++;
            stateMachine.StorePlannerSnapshot(_observationSequence, _progress);
            var fingerprint = BuildObservationFingerprint();
            if (fingerprint != _lastObservationFingerprint)
            {
                _lastObservationFingerprint = fingerprint;
                _snapshotGeneration++;
            }

            if (_awaitingInformation)
            {
                if (fingerprint == _awaitingInformationFingerprint)
                {
                    SetStatus("Awaiting a scan that adds new walls or a visited cell; no action or step is issued.");
                    return;
                }
                _awaitingInformation = false;
                _awaitingInformationFingerprint = null;
            }

            if (_postActionScanPending && !robot.IsActionInProgress)
                _postActionScanPending = false;
            TryRequestNextAction();
        }

        private void OnRecognitionCompleted(StationRuntimeState station)
        {
            if (_currentAct != DemoAct.Act1_OnlineMission)
                return;
            _snapshotGeneration++;
            TryRequestNextAction();
        }

        private void OnDeliveryValidated(DeliveryValidationEvent result)
        {
            if (_currentAct != DemoAct.Act1_OnlineMission)
                return;
            _snapshotGeneration++;
        }

        private void TryRequestNextAction()
        {
            if (!_initialized || _stopped || _awaitingInformation || _requestInFlight ||
                _postActionScanPending || robot.IsActionInProgress || stateMachine.HasPendingRecognition ||
                !string.IsNullOrEmpty(stateMachine.ErrorMessage) || _observationSequence == 0 ||
                _currentAct != DemoAct.Act1_OnlineMission)
                return;

            _requestInFlight = true;
            StartCoroutine(RequestNextAction());
        }

        private IEnumerator RequestNextAction()
        {
            var requestGeneration = _snapshotGeneration;
            NextActionRequestDto request = null;
            string buildError = null;
            try
            {
                request = stateMachine.BuildNextActionRequest(robot.Map.GetObservedMaskSnapshot(),
                    robot.TotalStepCount, _observationSequence, _progress);
            }
            catch (Exception exception)
            {
                buildError = exception.Message;
            }
            if (request == null)
            {
                _requestInFlight = false;
                StopWithError("Could not build a planner snapshot: " + buildError);
                yield break;
            }
            ApiResult<NextActionResponseDto> result = null;
            yield return plannerClient.NextAction(request, value => result = value);
            _requestInFlight = false;

            if (result == null || !result.Succeeded || result.Value == null)
            {
                StopWithError(result == null ? "Planner request returned no result." : result.Error);
                yield break;
            }
            if (requestGeneration != _snapshotGeneration)
            {
                SetStatus("Discarded a late response because the local decision snapshot changed.");
                TryRequestNextAction();
                yield break;
            }

            string validationError;
            if (!PlannerResponseValidator.TryValidate(request, result.Value, out validationError))
            {
                StopWithError("Planner response failed contract validation: " + validationError);
                yield break;
            }

            try
            {
                stateMachine.ApplyNextActionResponse(result.Value);
            }
            catch (Exception exception)
            {
                StopWithError("Could not apply planner slot state: " + exception.Message);
                yield break;
            }
            if (request.@event != null && !stateMachine.AcknowledgePendingEvent(request.@event))
            {
                StopWithError("The pending event changed while its response was in flight.");
                yield break;
            }

            _progress = result.Value.progress;
            stateMachine.StorePlannerSnapshot(_observationSequence, _progress);
            _snapshotGeneration++;
            PlannerTrajectoryPreviewChanged?.Invoke(result.Value.trajectory);
            stateMachine.ReportPlannerStatus("Planner status: " + result.Value.status + ".");

            // Notify UI of cost comparison decision whenever evaluated
            if (result.Value.decision != null &&
                (result.Value.decision.cost_keep.HasValue || result.Value.decision.cost_switch.HasValue))
            {
                DecisionEvaluated?.Invoke(result.Value.decision, robot.CurrentPose);
            }

            if (stateMachine.PendingEventCount > 0)
            {
                TryRequestNextAction();
                yield break;
            }

            switch (result.Value.status)
            {
                case "planning":
                case "exploring":
                    if (_lastExecutedGeneration == requestGeneration)
                    {
                        StopWithError("A response attempted to execute the same snapshot action more than once.");
                        yield break;
                    }
                    _lastExecutedGeneration = requestGeneration;
                    if (!TryExecuteFirstAction(result.Value.action))
                        StopWithError("RobotController rejected the planner action; no action was retried.");
                    yield break;

                case "awaiting_delivery":
                    if (!stateMachine.ValidateCurrentDelivery())
                    {
                        StopWithError("Planner is awaiting delivery, but Unity could not validate a destination.");
                        yield break;
                    }
                    _snapshotGeneration++;
                    TryRequestNextAction();
                    yield break;

                case "awaiting_observation":
                    _awaitingInformation = true;
                    _awaitingInformationFingerprint = BuildObservationFingerprint();
                    SetStatus("Planner is waiting for new sensor information; no action or step was executed.");
                    yield break;

                case "unreachable":
                    _stopped = true;
                    SetStatus("Known reachable area has no remaining task route or observation frontier.");
                    Debug.LogWarning("Planner task stopped as unreachable after " + robot.TotalStepCount + " actions.", this);
                    yield break;

                case "completed":
                    _stopped = true;
                    SetStatus("Both state machines were verified at their correct destinations.");
                    Debug.Log("Planner task completed after " + robot.TotalStepCount + " actions.", this);
                    StartCoroutine(RunPostCompletionWorkflow());
                    yield break;
            }
        }

        private IEnumerator RunPostCompletionWorkflow()
        {
            PlanSummaryRequestDto request = null;
            try
            {
                request = stateMachine.BuildPlanSummaryRequest(robot.Map.GetObservedMaskSnapshot());
            }
            catch (Exception ex)
            {
                SetStatus("Act 1 completed. Could not build summary request: " + ex.Message);
                yield break;
            }

            SetStatus("Requesting Act 2 RL Plan Summary...");
            ApiResult<PlanSummaryResponseDto> result = null;
            yield return plannerClient.PlanSummary(request, value => result = value);

            if (result == null || !result.Succeeded || result.Value == null)
            {
                var errorMsg = result == null ? "No response received" :
                    (result.StatusCode == 501 ? "HTTP 501 (RL_NOT_IMPLEMENTED): Q-Learning summary not implemented yet." : result.Error);
                SetStatus("Act 1 Complete. " + errorMsg);
                yield break;
            }

            PlanSummaryLoaded?.Invoke(result.Value);
            _currentAct = DemoAct.Act2_RlExploration;
            ActChanged?.Invoke(_currentAct);
            SetStatus("Act 2: Reinforcement Learning exploration visualization running (20x)...");
            robot.TrySetSpeedMultiplier(20f);

            if (visualizer != null)
            {
                yield return visualizer.PlayRlTrajectories(result.Value, (k, total, steps, best) =>
                {
                    RlProgressUpdated?.Invoke(k, total, steps, best);
                });
            }
            else
            {
                yield return new WaitForSeconds(1.5f);
            }

            _currentAct = DemoAct.Act3_OptimalReplay;
            ActChanged?.Invoke(_currentAct);
            SetStatus("Act 3: Replaying optimal converged trajectory (2x)...");
            if (visualizer != null)
            {
                visualizer.ShowOptimalPath(result.Value.optimal_path);
            }

            robot.TrySetSpeedMultiplier(2f);
            yield return RunReplayWorkflow(result.Value);

            if (_lastReplayFailed)
                yield break;

            _currentAct = DemoAct.Completed;
            ActChanged?.Invoke(_currentAct);
            SetStatus("Three-act demonstration completed successfully!");
        }

        private IEnumerator RunReplayWorkflow(PlanSummaryResponseDto summary)
        {
            _lastReplayFailed = false;
            if (summary == null || summary.optimal_actions == null || summary.optimal_actions.Count == 0 || robot == null)
                yield break;

            var actions = summary.optimal_actions;

            // Determine actual start_pose: extract from optimal record if present, else use stateMachine.InitialPose
            EpisodeRecordDto optimalRecord = null;
            if (summary.episode_records != null && summary.episode_records.Count > 0)
            {
                for (var r = 0; r < summary.episode_records.Count; r++)
                {
                    if (summary.episode_records[r].episode == summary.optimal_episode)
                    {
                        optimalRecord = summary.episode_records[r];
                        break;
                    }
                }
                if (optimalRecord == null)
                {
                    Debug.LogWarning("[Replay] Could not find episode matching optimal_episode (" + summary.optimal_episode + ") in episode_records; will not fallback to record[0].");
                }
            }

            var startPose = stateMachine != null ? stateMachine.InitialPose : new RobotPose(new Vector2Int(0, 0), RobotHeading.North);
            List<PoseDto> expectedPoses = null;
            var switchSteps = new HashSet<int>();

            if (optimalRecord != null)
            {
                if (optimalRecord.switch_steps != null)
                {
                    for (var s = 0; s < optimalRecord.switch_steps.Count; s++)
                        switchSteps.Add(optimalRecord.switch_steps[s]);
                }

                if (optimalRecord.trajectory != null && optimalRecord.trajectory.poses != null && optimalRecord.trajectory.poses.Count > 0)
                {
                    expectedPoses = optimalRecord.trajectory.poses;
                    var firstPose = expectedPoses[0];
                    startPose = new RobotPose(new Vector2Int(firstPose.x, firstPose.z), RobotPose.HeadingFromDegrees(firstPose.theta));
                }
            }

            stateMachine.IsReplayMode = true;
            robot.EnterReplayMode(startPose);
            yield return new WaitForSeconds(0.4f);

            for (var i = 0; i < actions.Count; i++)
            {
                var action = actions[i];

                if (switchSteps.Contains(i))
                {
                    SetStatus("Act 3 复盘: 动作 #" + (i + 1) + " [" + action + "] (触发站点拾取/换人/派送)...");
                }

                if (!TryExecuteFirstAction(action))
                {
                    Debug.LogError("[Replay Blocked/Failed] Step " + i + ": action '" + action + "' blocked by wall or failed at pose " + robot.CurrentPose);
                    SetStatus("<color=red>复盘失败: 第 " + (i + 1) + " 步动作 '" + action + "' 遇阻碰墙!</color>");
                    _lastReplayFailed = true;
                    break;
                }

                while (robot.IsActionInProgress)
                    yield return null;

                // Pose verification against expected trajectory
                if (expectedPoses != null && (i + 1) < expectedPoses.Count)
                {
                    var exp = expectedPoses[i + 1];
                    var cur = robot.CurrentPose;
                    if (cur.Cell.x != exp.x || cur.Cell.y != exp.z || cur.HeadingDegrees != exp.theta)
                    {
                        Debug.LogWarning("[Replay Pose Mismatch] Step " + (i + 1) + ": expected (" + exp.x + ", " + exp.z + ", " + exp.theta + "°), got (" + cur.Cell.x + ", " + cur.Cell.y + ", " + cur.HeadingDegrees + "°)");
                    }
                }
            }

            if (!_lastReplayFailed)
            {
                SetStatus("Act 3: Optimal trajectory replay completed successfully!");
                yield return new WaitForSeconds(0.5f);
            }

            robot.ExitReplayMode();
            stateMachine.IsReplayMode = false;
        }

        private bool TryExecuteFirstAction(string action)
        {
            switch (action)
            {
                case "forward": return robot.TryExecuteAction(RobotAction.Forward);
                case "turn_left": return robot.TryExecuteAction(RobotAction.TurnLeft);
                case "turn_right": return robot.TryExecuteAction(RobotAction.TurnRight);
                default: return false;
            }
        }

        public void RetryAfterError()
        {
            _stopped = false;
            stateMachine.RetryErrorState();
            _snapshotGeneration++;
            TryRequestNextAction();
        }

        public void ResumeAfterSensorRecovery()
        {
            _stopped = false;
            _awaitingInformation = false;
            _awaitingInformationFingerprint = null;
            _snapshotGeneration++;
            stateMachine.ClearPlannerError();
            lidar.Scan();
            TryRequestNextAction();
        }

        private string BuildObservationFingerprint()
        {
            var snapshot = robot.Map.GetObservedMaskSnapshot();
            var visited = robot.Map.CopyVisitedCells();
            var builder = new StringBuilder(snapshot.Width * snapshot.Height * 4 + visited.Length * 8);
            var seen = snapshot.CopySeenWalls();
            var known = snapshot.CopyKnownWalls();
            for (var i = 0; i < seen.Length; i++)
                builder.Append(seen[i].ToString("X1")).Append(known[i].ToString("X1"));
            builder.Append('|');
            for (var i = 0; i < visited.Length; i++)
                builder.Append(visited[i].x).Append(',').Append(visited[i].y).Append(';');
            return builder.ToString();
        }

        private void StopWithError(string message)
        {
            _stopped = true;
            SetStatus(message);
            Debug.LogError("Planner automation stopped: " + message, this);
            if (stateMachine != null)
                stateMachine.ReportPlannerError(message);
        }

        private void SetStatus(string message)
        {
            StatusChanged?.Invoke(message);
            if (stateMachine != null)
                stateMachine.ReportPlannerStatus(message);
        }
    }
}

using System;
using System.Collections;
using System.Text;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Executes one planner action from a single immutable Unity snapshot at a time.</summary>
    public sealed class PlannerTaskDriver : MonoBehaviour
    {
        [SerializeField] private RobotController robot;
        [SerializeField] private LidarSimulator lidar;
        [SerializeField] private PlannerHttpClient plannerClient;
        [SerializeField] private StateMachineTrigger stateMachine;

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

        public event Action<TrajectoryDto> PlannerTrajectoryPreviewChanged;
        public event Action<RobotPose[]> ActualPathChanged;
        public event Action<string> StatusChanged;

        public bool RequestInFlight { get { return _requestInFlight; } }
        public bool IsStopped { get { return _stopped; } }
        public long ObservationSequence { get { return _observationSequence; } }

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
            _snapshotGeneration++;
        }

        private void OnActionStarted(RobotAction action)
        {
            _snapshotGeneration++;
            _postActionScanPending = true;
        }

        private void OnActionCompleted(RobotActionResult result)
        {
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
            if (!_initialized || scan == null)
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
            _snapshotGeneration++;
            TryRequestNextAction();
        }

        private void OnDeliveryValidated(DeliveryValidationEvent result)
        {
            _snapshotGeneration++;
        }

        private void TryRequestNextAction()
        {
            if (!_initialized || _stopped || _awaitingInformation || _requestInFlight ||
                _postActionScanPending || robot.IsActionInProgress || stateMachine.HasPendingRecognition ||
                !string.IsNullOrEmpty(stateMachine.ErrorMessage) || _observationSequence == 0)
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
                    yield break;
            }
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

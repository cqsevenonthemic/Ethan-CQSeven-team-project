using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Runs one cardinal cell move or 90-degree turn at a time.</summary>
    public sealed class RobotController : MonoBehaviour
    {
        private const float BaseActionDurationSeconds = 0.2f;

        [SerializeField] private MazeBuilder maze;
        [SerializeField] private Vector2Int initialCell = new Vector2Int(0, 0);
        [SerializeField] private RobotHeading initialHeading = RobotHeading.North;
        [SerializeField, Min(0.01f)] private float speedMultiplier = 1f;

        private RobotPose _pose;
        private bool _initialized;
        private bool _isAnimating;
        private RobotAction _activeAction;
        private RobotPose _actionStartPose;
        private RobotPose _actionEndPose;
        private Vector3 _startPosition;
        private Vector3 _targetPosition;
        private float _startYaw;
        private float _targetYaw;
        private float _elapsedSeconds;
        private float _actionDurationSeconds;

        // Act 1 step counts
        private int _totalStepCount;
        private int _forwardStepCount;
        private int _leftTurnStepCount;
        private int _rightTurnStepCount;
        private readonly List<RobotPose> _executedPoses = new List<RobotPose>();
        private readonly List<RobotAction> _executedActions = new List<RobotAction>();

        // Replay mode (Act 3) state
        private bool _isReplayMode;
        private int _replayStepCount;
        private int _replayForwardStepCount;
        private int _replayLeftTurnStepCount;
        private int _replayRightTurnStepCount;

        public event Action<RobotAction> ActionStarted;
        public event Action<RobotActionResult> ActionCompleted;
        public event Action<RobotPose> PoseChanged;
        public event Action<RobotPose> ForwardBlocked;

        public RobotPose CurrentPose { get { return _pose; } }
        public MazeBuilder Map { get { return maze; } }
        public bool IsActionInProgress { get { return _isAnimating; } }
        public RobotAction ActiveAction { get { return _activeAction; } }
        public int TotalStepCount { get { return _totalStepCount; } }
        public int ForwardStepCount { get { return _forwardStepCount; } }
        public int LeftTurnStepCount { get { return _leftTurnStepCount; } }
        public int RightTurnStepCount { get { return _rightTurnStepCount; } }
        public float SpeedMultiplier { get { return speedMultiplier; } }
        public float CurrentActionDurationSeconds { get { return _actionDurationSeconds; } }
        public int ExecutedActionCount { get { return _executedActions.Count; } }

        public bool IsReplayMode { get { return _isReplayMode; } }
        public int ReplayStepCount { get { return _replayStepCount; } }
        public int ReplayForwardStepCount { get { return _replayForwardStepCount; } }
        public int ReplayLeftTurnStepCount { get { return _replayLeftTurnStepCount; } }
        public int ReplayRightTurnStepCount { get { return _replayRightTurnStepCount; } }

        public RobotPose[] CopyExecutedPoses() { return _executedPoses.ToArray(); }
        public RobotAction[] CopyExecutedActions() { return _executedActions.ToArray(); }

        private void Awake()
        {
            if (!TrySetSpeedMultiplier(speedMultiplier))
                speedMultiplier = 1f;
            if (maze == null)
                maze = FindFirstObjectByType<MazeBuilder>();
            Initialize(maze, new RobotPose(initialCell, initialHeading));
        }

        private void Start()
        {
            if (!_initialized && maze == null)
                maze = FindFirstObjectByType<MazeBuilder>();
            if (!_initialized && maze != null)
                Initialize(maze, new RobotPose(initialCell, initialHeading));
        }

        private void Update()
        {
            if (!_isAnimating)
                return;

            _elapsedSeconds += Time.deltaTime;
            var progress = Mathf.Clamp01(_elapsedSeconds / _actionDurationSeconds);
            var position = Vector3.Lerp(_startPosition, _targetPosition, progress);
            position.y = _startPosition.y;
            transform.position = position;
            transform.rotation = Quaternion.Euler(0f, Mathf.Lerp(_startYaw, _targetYaw, progress), 0f);

            if (progress >= 1f)
                CompleteActiveAction();
        }

        /// <summary>Sets the logical and visual start pose. Fails without changing state when invalid.</summary>
        public bool Initialize(MazeBuilder map, RobotPose pose)
        {
            if (_isAnimating || map == null || !map.IsInBounds(pose.Cell) || !IsValidHeading(pose.Heading))
                return false;

            Vector3 center;
            if (!map.TryCellToWorld(pose.Cell, out center))
                return false;

            maze = map;
            _pose = pose;
            _executedPoses.Clear();
            _executedActions.Clear();
            _totalStepCount = 0;
            _forwardStepCount = 0;
            _leftTurnStepCount = 0;
            _rightTurnStepCount = 0;
            _executedPoses.Add(pose);
            transform.position = center;
            transform.rotation = Quaternion.Euler(0f, pose.HeadingDegrees, 0f);
            maze.MarkVisited(pose.Cell);
            _initialized = true;
            return true;
        }

        /// <summary>Enters replay mode without corrupting Act 1 recorded poses or steps.</summary>
        public bool EnterReplayMode(RobotPose startPose)
        {
            if (maze == null || !maze.IsInBounds(startPose.Cell) || !IsValidHeading(startPose.Heading))
                return false;

            Vector3 center;
            if (!maze.TryCellToWorld(startPose.Cell, out center))
                return false;

            _isAnimating = false;
            _isReplayMode = true;
            _replayStepCount = 0;
            _replayForwardStepCount = 0;
            _replayLeftTurnStepCount = 0;
            _replayRightTurnStepCount = 0;
            _pose = startPose;
            transform.position = center;
            transform.rotation = Quaternion.Euler(0f, startPose.HeadingDegrees, 0f);
            PoseChanged?.Invoke(_pose);
            return true;
        }

        /// <summary>Exits replay mode.</summary>
        public void ExitReplayMode()
        {
            _isReplayMode = false;
        }

        /// <summary>Accepts and starts an action when idle. Wall-blocked forward attempts count once and complete immediately.</summary>
        public bool TryExecuteAction(RobotAction action)
        {
            if (!_initialized || _isAnimating || !IsValidAction(action) || maze == null)
                return false;

            var startPose = _pose;
            var blocked = action == RobotAction.Forward && !maze.IsPassageOpen(startPose.Cell, startPose.Heading);
            var endPose = CalculateEndPose(startPose, action);
            var targetCellCenter = transform.position;
            if (!blocked && !maze.TryCellToWorld(endPose.Cell, out targetCellCenter))
                return false;

            _activeAction = action;
            _actionStartPose = startPose;
            _actionEndPose = endPose;
            _elapsedSeconds = 0f;
            _actionDurationSeconds = BaseActionDurationSeconds / speedMultiplier;
            _startPosition = transform.position;
            _targetPosition = new Vector3(targetCellCenter.x, _startPosition.y, targetCellCenter.z);
            _startYaw = _pose.HeadingDegrees;
            _targetYaw = GetTargetYaw(_startYaw, action);
            if (blocked)
            {
                _isAnimating = false;
                _actionDurationSeconds = 0f;
                ForwardBlocked?.Invoke(startPose);
                ActionCompleted?.Invoke(new RobotActionResult(action, startPose, startPose,
                    false, true, 0f));
                return false;
            }

            CountAction(action);
            _isAnimating = true;
            ActionStarted?.Invoke(action);
            return true;
        }

        public bool MoveForward() { return TryExecuteAction(RobotAction.Forward); }
        public bool TurnLeft() { return TryExecuteAction(RobotAction.TurnLeft); }
        public bool TurnRight() { return TryExecuteAction(RobotAction.TurnRight); }

        /// <summary>Accepts finite positive multipliers; 1x=0.2s, 2x=0.1s, 20x=0.01s.</summary>
        public bool TrySetSpeedMultiplier(float multiplier)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier) || multiplier <= 0f)
                return false;
            var resultingDuration = BaseActionDurationSeconds / multiplier;
            if (float.IsNaN(resultingDuration) || float.IsInfinity(resultingDuration) || resultingDuration <= 0f)
                return false;
            speedMultiplier = multiplier;
            return true;
        }

        private void CompleteActiveAction()
        {
            var completedAction = _activeAction;
            var startPose = _actionStartPose;
            _pose = _actionEndPose;
            RecordCompletedAction(completedAction, _pose);
            transform.position = _targetPosition;
            transform.rotation = Quaternion.Euler(0f, _pose.HeadingDegrees, 0f);
            _isAnimating = false;
            if (!_isReplayMode && completedAction == RobotAction.Forward)
                maze.MarkVisited(_pose.Cell);
            PoseChanged?.Invoke(_pose);
            ActionCompleted?.Invoke(new RobotActionResult(completedAction, startPose, _pose,
                true, false, _actionDurationSeconds));
        }

        private void CountAction(RobotAction action)
        {
            if (_isReplayMode)
            {
                _replayStepCount++;
                switch (action)
                {
                    case RobotAction.Forward: _replayForwardStepCount++; break;
                    case RobotAction.TurnLeft: _replayLeftTurnStepCount++; break;
                    case RobotAction.TurnRight: _replayRightTurnStepCount++; break;
                }
            }
            else
            {
                _totalStepCount++;
                switch (action)
                {
                    case RobotAction.Forward: _forwardStepCount++; break;
                    case RobotAction.TurnLeft: _leftTurnStepCount++; break;
                    case RobotAction.TurnRight: _rightTurnStepCount++; break;
                }
            }
        }

        private void RecordCompletedAction(RobotAction action, RobotPose endPose)
        {
            if (!_isReplayMode)
            {
                _executedActions.Add(action);
                _executedPoses.Add(endPose);
            }
        }

        private static RobotPose CalculateEndPose(RobotPose pose, RobotAction action)
        {
            switch (action)
            {
                case RobotAction.Forward:
                    int dx;
                    int dz;
                    WallDirection.TryGetOffset(WallDirection.FromHeading(pose.Heading), out dx, out dz);
                    return new RobotPose(new Vector2Int(pose.Cell.x + dx, pose.Cell.y + dz), pose.Heading);
                case RobotAction.TurnLeft:
                    return new RobotPose(pose.Cell, (RobotHeading)(((pose.HeadingDegrees + 270) % 360)));
                case RobotAction.TurnRight:
                    return new RobotPose(pose.Cell, (RobotHeading)(((pose.HeadingDegrees + 90) % 360)));
                default:
                    return pose;
            }
        }

        private static float GetTargetYaw(float startYaw, RobotAction action)
        {
            if (action == RobotAction.TurnLeft)
                return startYaw - 90f;
            if (action == RobotAction.TurnRight)
                return startYaw + 90f;
            return startYaw;
        }

        private static bool IsValidHeading(RobotHeading heading)
        {
            return heading == RobotHeading.North || heading == RobotHeading.East ||
                   heading == RobotHeading.South || heading == RobotHeading.West;
        }

        private static bool IsValidAction(RobotAction action)
        {
            return action == RobotAction.Forward || action == RobotAction.TurnLeft ||
                   action == RobotAction.TurnRight;
        }
    }
}

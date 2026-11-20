using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Loads local station data after the planner mode is known and handles cell-entry events.</summary>
    public sealed class StateMachineTrigger : MonoBehaviour
    {
        private const string EvaluationFileName = "eval.json";
        private const string MockEvaluationFileName = "mock_evaluation.json";
        private const string TriggerDiscoveredType = "trigger_discovered";
        private const string DeliveryRejectedType = "delivery_rejected";

        [SerializeField] private RobotController robot;
        [SerializeField] private PlannerHttpClient plannerClient;
        [SerializeField] private LidarSimulator lidar;
        [SerializeField] private Camera recognitionCamera;
        [SerializeField, Range(64, 1920)] private int recognitionImageWidth = 320;
        [SerializeField, Range(64, 1920)] private int recognitionImageHeight = 240;

        private readonly List<StationRuntimeState> _stations = new List<StationRuntimeState>(2);
        private readonly Dictionary<string, string> _truthRoles = new Dictionary<string, string>();
        private readonly Queue<NextActionEventDto> _pendingEvents = new Queue<NextActionEventDto>();
        private PlannerProgressDto _progress;
        private long _observationSequence;
        private bool _runtimeEventsSubscribed;
        private int _recognitionsInFlight;
        private bool _isReady;
        private Vector2Int? _rejectedCellUntilLeave;
        private RobotPose _initialPose;

        public event Action<StationDiscovery> StationDiscovered;
        public event Action<StationRuntimeState> RecognitionCompleted;
        public event Action<SlotSwitchEvent> SlotSwitched;
        public event Action<DeliveryValidationEvent> DeliveryValidated;
        public event Action<string> StatusChanged;
        public event Action<bool, string> InitializationCompleted;
        public event Action<string> ReplayCarryStateUpdated;

        private string _replayCarrySlot;
        private readonly Dictionary<string, GameObject> _replayStands = new Dictionary<string, GameObject>();
        private readonly Dictionary<string, Vector2Int> _replayStationCells = new Dictionary<string, Vector2Int>();
        private readonly Dictionary<string, bool> _replayDelivered = new Dictionary<string, bool>();

        public bool IsReady { get { return _isReady; } }
        public long ObservationSequence { get { return _observationSequence; } }
        public string StatusMessage { get; private set; } = "Waiting for planner service availability check.";
        public string ErrorMessage { get; private set; }
        public string CarrySlot { get; private set; }
        public RobotPose InitialPose { get { return _initialPose; } }
        public IEnumerable<StationRuntimeState> Stations { get { return _stations; } }
        public int PendingEventCount { get { return _pendingEvents.Count; } }
        public bool IsReplayMode { get; set; }

        private void Start()
        {
            if (robot == null)
                robot = FindFirstObjectByType<RobotController>();
            if (plannerClient == null)
                plannerClient = FindFirstObjectByType<PlannerHttpClient>();
            if (lidar == null)
                lidar = FindFirstObjectByType<LidarSimulator>();

            if (robot == null || plannerClient == null || lidar == null)
            {
                SetError("StateMachineTrigger requires RobotController, PlannerHttpClient, and LidarSimulator references.");
                InitializationCompleted?.Invoke(false, ErrorMessage);
                return;
            }

            plannerClient.InitialModeResolved += OnInitialModeResolved;
            if (plannerClient.IsInitialModeResolved)
                LoadConfiguration(plannerClient.Mode);
            else
            {
                SetStatus("Checking planner service availability to choose Live or Mock station data.");
                StartCoroutine(plannerClient.ResolveInitialMode());
            }
        }

        private void OnDestroy()
        {
            if (plannerClient != null)
                plannerClient.InitialModeResolved -= OnInitialModeResolved;
            if (robot != null && _runtimeEventsSubscribed)
                robot.PoseChanged -= OnPoseChanged;
            foreach (var kvp in _replayStands)
            {
                if (kvp.Value != null)
                    Destroy(kvp.Value);
            }
            _replayStands.Clear();
        }

        /// <summary>
        /// Builds a request from the robot's current pose and observed masks only. A slot is included only
        /// after its cell has been entered and its image has been recognized.
        /// </summary>
        public NextActionRequestDto BuildNextActionRequest(ObservedMaskSnapshot snapshot, int stepCount)
        {
            return BuildNextActionRequest(snapshot, stepCount, _observationSequence, _progress);
        }

        public NextActionRequestDto BuildNextActionRequest(ObservedMaskSnapshot snapshot, int stepCount,
            long observationSequence, PlannerProgressDto progress)
        {
            if (!_isReady)
                throw new InvalidOperationException("Station configuration is not ready: " + StatusMessage);
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));
            if (robot == null || robot.Map == null || snapshot.Width != robot.Map.Width ||
                snapshot.Height != robot.Map.Height)
                throw new InvalidOperationException("Observed wall snapshot dimensions do not match the maze.");

            var pose = robot.CurrentPose;
            var request = new NextActionRequestDto
            {
                pose = new PoseDto { x = pose.Cell.x, z = pose.Cell.y, theta = pose.HeadingDegrees },
                seen_walls = ToXMajor(snapshot.CopySeenWalls(), snapshot.Width, snapshot.Height),
                known_walls = ToXMajor(snapshot.CopyKnownWalls(), snapshot.Width, snapshot.Height),
                step_count = stepCount,
                carry_slot = CarrySlot,
                slots = new Dictionary<string, SlotDto>(),
                @event = _pendingEvents.Count == 0 ? null : _pendingEvents.Peek(),
                visited_cells = ToCellPairs(robot.Map.CopyVisitedCells()),
                observation_seq = observationSequence,
                progress = progress
            };

            for (var i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];
                if (!station.IsDiscovered || !station.IsRecognized || string.IsNullOrEmpty(station.PredictedRole))
                    continue;

                if (station.SlotId == CarrySlot && station.State == "carried")
                {
                    station.Cell = pose.Cell;
                }

                request.slots[station.SlotId] = new SlotDto
                {
                    x = station.Cell.x,
                    z = station.Cell.y,
                    pred_role = station.PredictedRole,
                    verified = station.IsVerified,
                    state = station.State,
                    recognition_id = station.RecognitionId,
                    recognition_source = station.RecognitionSource
                };
            }
            return request;
        }

        /// <summary>Removes an event only after a valid planner response has been applied.</summary>
        public bool AcknowledgePendingEvent(NextActionEventDto submittedEvent)
        {
            if (submittedEvent == null || _pendingEvents.Count == 0 ||
                !ReferenceEquals(_pendingEvents.Peek(), submittedEvent))
                return false;
            _pendingEvents.Dequeue();
            return true;
        }

        public bool HasPendingRecognition
        {
            get
            {
                for (var i = 0; i < _stations.Count; i++)
                    if (_stations[i].IsDiscovered && !_stations[i].IsRecognized &&
                        string.IsNullOrEmpty(_stations[i].ImageError))
                        return true;
                return false;
            }
        }

        public bool ValidateCurrentDelivery()
        {
            if (IsReplayMode)
                return true;
            if (string.IsNullOrEmpty(CarrySlot) || robot == null || robot.Map == null)
                return false;
            var cell = robot.CurrentPose.Cell;
            if (cell != robot.Map.FirstDestination && cell != robot.Map.SecondDestination)
                return false;
            var previousCarry = CarrySlot;
            ValidateDeliveryAt(cell);
            return CarrySlot != previousCarry || PendingEventCount > 0;
        }

        public void ReportPlannerStatus(string message)
        {
            SetStatus(message);
        }

        public void ReportPlannerError(string message)
        {
            SetError(message);
        }

        public void StorePlannerSnapshot(long observationSequence, PlannerProgressDto progress)
        {
            _observationSequence = observationSequence;
            _progress = progress;
        }

        public void RetryErrorState()
        {
            ErrorMessage = null;
            SetStatus("Retrying the current planner snapshot.");
            for (var i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];
                if (!station.IsDiscovered || station.IsRecognized || string.IsNullOrEmpty(station.ImageError))
                    continue;
                station.ImageError = null;
                StartCoroutine(RecognizeStation(station));
            }
        }

        public void ClearPlannerError()
        {
            ErrorMessage = null;
        }

        /// <summary>Builds the summary request using recognized station data after both slots are discovered.</summary>
        public PlanSummaryRequestDto BuildPlanSummaryRequest(ObservedMaskSnapshot snapshot)
        {
            if (!_isReady)
                throw new InvalidOperationException("Station configuration is not ready: " + StatusMessage);
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            var targets = new Dictionary<string, PoseCellDto>();
            for (var i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];
                if (!station.IsDiscovered || !station.IsRecognized || string.IsNullOrEmpty(station.PredictedRole))
                    throw new InvalidOperationException("Both station locations must be discovered and recognized before requesting a summary.");
                if (targets.ContainsKey(station.PredictedRole))
                    throw new InvalidOperationException("The recognized slots do not have distinct roles; summary targets are ambiguous.");
                var targetCell = station.HasRecognitionCell ? station.RecognitionCell : station.Cell;
                targets.Add(station.PredictedRole, new PoseCellDto(targetCell.x, targetCell.y));
            }

            return new PlanSummaryRequestDto
            {
                start_pose = new PoseDto
                {
                    x = _initialPose.Cell.x,
                    z = _initialPose.Cell.y,
                    theta = _initialPose.HeadingDegrees
                },
                seen_walls = ToXMajor(snapshot.CopySeenWalls(), snapshot.Width, snapshot.Height),
                known_walls = ToXMajor(snapshot.CopyKnownWalls(), snapshot.Width, snapshot.Height),
                initial_targets = targets
            };
        }

        /// <summary>Applies backend carry and slot state, exposing switch details to the UI layer.</summary>
        public void ApplyNextActionResponse(NextActionResponseDto response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            var previousCarry = CarrySlot;
            CarrySlot = response.carry_slot;
            var currentCell = robot == null ? Vector2Int.zero : robot.CurrentPose.Cell;

            if (response.decision != null && response.decision.switched &&
                !string.IsNullOrEmpty(previousCarry) && previousCarry != response.carry_slot)
            {
                var previousStation = FindStation(previousCarry);
                if (previousStation != null)
                {
                    previousStation.Cell = currentCell;
                    previousStation.State = "waiting";
                }
            }

            if (response.slots != null)
            {
                foreach (var entry in response.slots)
                {
                    var station = FindStation(entry.Key);
                    var backendSlot = entry.Value;
                    if (station == null || !station.IsDiscovered || backendSlot == null)
                        continue;
                    station.Cell = new Vector2Int(backendSlot.x, backendSlot.z);
                    if (!string.IsNullOrEmpty(backendSlot.pred_role))
                        station.PredictedRole = backendSlot.pred_role;
                    station.IsVerified = backendSlot.verified;
                    station.State = backendSlot.state;
                    if (!string.IsNullOrEmpty(backendSlot.recognition_id))
                        station.RecognitionId = backendSlot.recognition_id;
                    if (!string.IsNullOrEmpty(backendSlot.recognition_source))
                        station.RecognitionSource = backendSlot.recognition_source;
                }
            }

            if (!string.IsNullOrEmpty(CarrySlot))
            {
                var carriedStation = FindStation(CarrySlot);
                if (carriedStation == null)
                    throw new InvalidOperationException("Planner returned an unknown carried slot: " + CarrySlot);
                carriedStation.Cell = currentCell;
                carriedStation.State = "carried";
            }

            if (response.decision != null && response.decision.switched)
            {
                var detail = new SlotSwitchEvent(previousCarry, response.carry_slot,
                    currentCell, response.decision);
                SlotSwitched?.Invoke(detail);
                SetStatus("Planner switched carried slot at (" + currentCell.x + "," + currentCell.y + ").");
            }
        }

        private void OnInitialModeResolved(PlannerClientMode mode)
        {
            LoadConfiguration(mode);
        }

        private void LoadConfiguration(PlannerClientMode mode)
        {
            if (_isReady || !string.IsNullOrEmpty(ErrorMessage))
                return;

            var fileName = mode == PlannerClientMode.Mock ? MockEvaluationFileName : EvaluationFileName;
            var path = Path.Combine(Application.streamingAssetsPath,
                mode == PlannerClientMode.Mock ? "Mock" : string.Empty, fileName);
            string json;
            try
            {
                if (!File.Exists(path))
                {
                    if (mode == PlannerClientMode.Mock)
                        throw new FileNotFoundException("Fixed Mock station fixture is missing.", path);
                    throw new FileNotFoundException(
                        "Local evaluation config is missing. Copy eval.example.json to eval.json and fill both stations.", path);
                }
                json = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                SetError("Could not load station configuration " + path + ": " + exception.Message);
                InitializationCompleted?.Invoke(false, ErrorMessage);
                return;
            }

            try
            {
                var config = JsonConvert.DeserializeObject<EvaluationConfig>(json);
                ValidateConfig(config, mode == PlannerClientMode.Mock);
                AddStation("s1", config.s1);
                AddStation("s2", config.s2);
                _initialPose = robot.CurrentPose;
                _isReady = true;
                robot.PoseChanged += OnPoseChanged;
                _runtimeEventsSubscribed = true;
                SetStatus(mode == PlannerClientMode.Mock
                    ? "MOCK mode ready with fixed illustrative station fixtures."
                    : "Live mode ready with local evaluation station config.");
                OnPoseChanged(robot.CurrentPose);
                InitializationCompleted?.Invoke(true, StatusMessage);
                lidar.Scan();
            }
            catch (Exception exception)
            {
                SetError("Invalid station configuration " + path + ": " + exception.Message);
                InitializationCompleted?.Invoke(false, ErrorMessage);
            }
        }

        private void AddStation(string slotId, EvaluationStationConfig config)
        {
            var state = new StationRuntimeState(slotId, new Vector2Int(config.x.Value, config.z.Value),
                config.image_path);
            _stations.Add(state);
            _truthRoles.Add(slotId, config.role);
        }

        private void ValidateConfig(EvaluationConfig config, bool requireFixtureMarker)
        {
            if (config == null)
                throw new JsonSerializationException("The configuration contains JSON null.");
            if (config.template_only == true)
                throw new JsonSerializationException("This file is a template. Fill it and set _template_only to false.");
            if (requireFixtureMarker && config.fixture_only != true)
                throw new JsonSerializationException("Mock station data must set _fixture_only to true.");
            if (config.s1 == null || config.s2 == null)
                throw new JsonSerializationException("Both s1 and s2 station entries are required.");
            ValidateStation("s1", config.s1);
            ValidateStation("s2", config.s2);
            if (config.s1.x == config.s2.x && config.s1.z == config.s2.z)
                throw new JsonSerializationException("The two state-machine positions must be distinct.");
        }

        private void ValidateStation(string slotId, EvaluationStationConfig station)
        {
            if (!station.x.HasValue || !station.z.HasValue)
                throw new JsonSerializationException(slotId + " needs integer x and z coordinates.");
            if (!robot.Map.IsInBounds(new Vector2Int(station.x.Value, station.z.Value)))
                throw new JsonSerializationException(slotId + " coordinates are outside the " +
                    robot.Map.Width + "x" + robot.Map.Height + " maze.");
            if (station.role != "quyuan" && station.role != "zhaojun")
                throw new JsonSerializationException(slotId + " role must be exactly quyuan or zhaojun.");
            if (recognitionCamera == null && string.IsNullOrWhiteSpace(station.image_path))
                throw new JsonSerializationException(slotId + " needs image_path when no recognition camera is assigned.");
        }

        private void OnPoseChanged(RobotPose pose)
        {
            if (!_isReady || IsReplayMode)
                return;

            if (!string.IsNullOrEmpty(CarrySlot))
            {
                var carried = FindStation(CarrySlot);
                if (carried != null && carried.State == "carried")
                {
                    carried.Cell = pose.Cell;
                }
            }

            if (_rejectedCellUntilLeave.HasValue && pose.Cell != _rejectedCellUntilLeave.Value)
                _rejectedCellUntilLeave = null;
            DiscoverStationsAt(pose.Cell);
            ValidateDeliveryAt(pose.Cell);
        }

        private void DiscoverStationsAt(Vector2Int cell)
        {
            for (var i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];
                if (station.IsDiscovered || station.Cell != cell)
                    continue;

                station.IsDiscovered = true;
                station.State = "discovered";
                station.RecognitionId = Guid.NewGuid().ToString("N");
                station.RecognitionCell = cell;
                station.HasRecognitionCell = true;
                StartCoroutine(RecognizeStation(station));
            }
        }

        private IEnumerator RecognizeStation(StationRuntimeState station)
        {
            _recognitionsInFlight++;
            byte[] imageBytes = null;
            string imageError = null;
            if (recognitionCamera != null)
                yield return CaptureCameraImage(recognitionCamera,
                    value => imageBytes = value, error => imageError = error);
            else if (!TryReadImageBytes(station.ImagePath, out imageBytes, out imageError))
                imageBytes = null;

            if (imageBytes == null || imageBytes.Length == 0)
            {
                station.ImageError = imageError ?? "The captured station image is empty.";
                StationDiscovered?.Invoke(new StationDiscovery(station, null, station.ImageError));
                _recognitionsInFlight--;
                SetError(station.ImageError);
                yield break;
            }

            station.ImageBytes = imageBytes;
            StationDiscovered?.Invoke(new StationDiscovery(station, imageBytes, null));
            var request = new RecognizeRequestDto { image_base64 = Convert.ToBase64String(imageBytes) };
            ApiResult<RecognizeResponseDto> result = null;
            yield return plannerClient.Recognize(request, value => result = value);
            if (result == null || !result.Succeeded || result.Value == null)
            {
                var detail = result == null ? "Recognition request did not return a result." : result.Error;
                station.ImageError = detail;
                _recognitionsInFlight--;
                SetError("Could not recognize " + station.SlotId + ": " + detail);
                yield break;
            }

            if (result.Value.pred_role != "quyuan" && result.Value.pred_role != "zhaojun")
            {
                _recognitionsInFlight--;
                SetError("Recognition returned an unsupported role for " + station.SlotId + ".");
                yield break;
            }

            station.PredictedRole = result.Value.pred_role;
            station.Confidence = result.Value.confidence;
            station.RecognitionSource = plannerClient.Mode == PlannerClientMode.Mock ? "mock" : "onnx";
            station.IsRecognized = true;
            station.State = "waiting";
            _pendingEvents.Enqueue(new NextActionEventDto
            {
                type = TriggerDiscoveredType,
                slot_id = station.SlotId,
                x = station.Cell.x,
                z = station.Cell.y,
                pred_role = station.PredictedRole,
                confidence = station.Confidence,
                recognition_id = station.RecognitionId
            });
            RecognitionCompleted?.Invoke(station);
            SetStatus("Recognized " + station.SlotId + " as " + station.PredictedRole + " (" +
                      station.Confidence.ToString("P1") + ").");
            _recognitionsInFlight--;
        }

        private IEnumerator CaptureCameraImage(Camera source, Action<byte[]> completed, Action<string> failed)
        {
            yield return new WaitForEndOfFrame();

            var width = Mathf.Clamp(recognitionImageWidth, 64, 1920);
            var height = Mathf.Clamp(recognitionImageHeight, 64, 1920);
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = source.targetTexture;
            var previousActive = RenderTexture.active;
            Texture2D image = null;
            try
            {
                source.targetTexture = target;
                source.Render();
                RenderTexture.active = target;
                image = new Texture2D(width, height, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                image.Apply(false, false);
                completed?.Invoke(image.EncodeToPNG());
            }
            catch (Exception exception)
            {
                failed?.Invoke("Could not capture the recognition camera frame: " + exception.Message);
            }
            finally
            {
                source.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                if (image != null)
                    Destroy(image);
                target.Release();
                Destroy(target);
            }
        }

        private void ValidateDeliveryAt(Vector2Int cell)
        {
            if (string.IsNullOrEmpty(CarrySlot) ||
                (cell != robot.Map.FirstDestination && cell != robot.Map.SecondDestination) ||
                (_rejectedCellUntilLeave.HasValue && cell == _rejectedCellUntilLeave.Value))
                return;

            var station = FindStation(CarrySlot);
            if (station == null || !station.IsRecognized)
                return;

            var destinationRole = cell == robot.Map.FirstDestination ? "quyuan" : "zhaojun";
            var truthRole = _truthRoles[station.SlotId];
            var accepted = truthRole == destinationRole;
            if (accepted)
            {
                station.State = "delivered";
                station.IsVerified = true;
                CarrySlot = null;
                DeliveryValidated?.Invoke(new DeliveryValidationEvent(station.SlotId, cell, true, null));
                SetStatus(station.SlotId + " accepted at its local destination.");
                return;
            }

            _pendingEvents.Enqueue(new NextActionEventDto
            {
                type = DeliveryRejectedType,
                slot_id = station.SlotId,
                rejected_at = new PoseCellDto(cell.x, cell.y)
            });
            _rejectedCellUntilLeave = cell;
            station.State = "carried";
            var message = "Delivery rejected for " + station.SlotId + " at (" + cell.x + "," + cell.y + ").";
            DeliveryValidated?.Invoke(new DeliveryValidationEvent(station.SlotId, cell, false, message));
            SetStatus(message);
        }

        private bool TryReadImageBytes(string relativePath, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            if (Path.IsPathRooted(relativePath))
            {
                error = "Image path must be relative to StreamingAssets: " + relativePath;
                return false;
            }

            try
            {
                var root = Path.GetFullPath(Application.streamingAssetsPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var imagePath = Path.GetFullPath(Path.Combine(root, relativePath));
                var rootPrefix = root + Path.DirectorySeparatorChar;
                if (!imagePath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Image path must stay under StreamingAssets: " + relativePath;
                    return false;
                }
                if (!File.Exists(imagePath))
                {
                    error = "Station image is missing: " + imagePath;
                    return false;
                }
                bytes = File.ReadAllBytes(imagePath);
                if (bytes.Length == 0)
                {
                    error = "Station image is empty: " + imagePath;
                    bytes = null;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not read station image " + relativePath + ": " + exception.Message;
                return false;
            }
        }

        private StationRuntimeState FindStation(string slotId)
        {
            for (var i = 0; i < _stations.Count; i++)
                if (_stations[i].SlotId == slotId)
                    return _stations[i];
            return null;
        }

        private static int[][] ToXMajor(byte[] flattened, int width, int height)
        {
            if (flattened == null || flattened.Length != width * height)
                throw new InvalidOperationException("Observed mask snapshot has an invalid flattened length.");

            var result = new int[width][];
            for (var x = 0; x < width; x++)
            {
                result[x] = new int[height];
                for (var z = 0; z < height; z++)
                    result[x][z] = flattened[z * width + x];
            }
            return result;
        }

        private static int[][] ToCellPairs(Vector2Int[] cells)
        {
            if (cells == null)
                return new int[0][];
            var result = new int[cells.Length][];
            for (var i = 0; i < cells.Length; i++)
                result[i] = new[] { cells[i].x, cells[i].y };
            return result;
        }

        private void SetStatus(string message)
        {
            StatusMessage = message;
            StatusChanged?.Invoke(message);
        }

        private void SetError(string message)
        {
            ErrorMessage = message;
            SetStatus(message);
        }

        public void BeginReplayStationVisuals()
        {
            _replayCarrySlot = null;
            _replayDelivered.Clear();
            _replayStationCells.Clear();

            InitReplayStands();

            for (var i = 0; i < _stations.Count; i++)
            {
                var s = _stations[i];
                var cell = s.HasRecognitionCell ? s.RecognitionCell : s.Cell;
                _replayStationCells[s.SlotId] = cell;
                _replayDelivered[s.SlotId] = false;

                if (_replayStands.TryGetValue(s.SlotId, out var stand) && stand != null)
                {
                    stand.transform.position = new Vector3(cell.x + 0.5f, 0.22f, cell.y + 0.5f);
                    stand.SetActive(true);
                }
            }

            ReplayCarryStateUpdated?.Invoke("复盘携带: 未携带人物");
        }

        public void StepReplayStationVisuals(Vector2Int currentCell, bool isSwitchStep)
        {
            // 1. Delivery check
            if (!string.IsNullOrEmpty(_replayCarrySlot))
            {
                var role = GetStationRole(_replayCarrySlot);
                var dest = GetDestinationForRole(role);
                if (dest == currentCell)
                {
                    var deliveredSlot = _replayCarrySlot;
                    _replayDelivered[deliveredSlot] = true;
                    _replayStationCells[deliveredSlot] = currentCell;
                    if (_replayStands.TryGetValue(deliveredSlot, out var stand) && stand != null)
                    {
                        stand.transform.position = new Vector3(currentCell.x + 0.5f, 0.22f, currentCell.y + 0.5f);
                        stand.SetActive(true);
                    }
                    _replayCarrySlot = null;
                }
            }

            // 2. Pickup / Switch check
            string stationAtCell = null;
            for (var i = 0; i < _stations.Count; i++)
            {
                var s = _stations[i];
                var sId = s.SlotId;
                if (_replayDelivered.TryGetValue(sId, out var del) && del)
                    continue;
                if (_replayCarrySlot == sId)
                    continue;
                if (_replayStationCells.TryGetValue(sId, out var sCell) && sCell == currentCell)
                {
                    stationAtCell = sId;
                    break;
                }
            }

            if (stationAtCell != null)
            {
                if (string.IsNullOrEmpty(_replayCarrySlot))
                {
                    // Pick up
                    _replayCarrySlot = stationAtCell;
                    if (_replayStands.TryGetValue(stationAtCell, out var stand) && stand != null)
                    {
                        stand.SetActive(false);
                    }
                }
                else if (_replayCarrySlot != stationAtCell && isSwitchStep)
                {
                    // Swap: drop old stand at currentCell
                    var oldSlot = _replayCarrySlot;
                    _replayStationCells[oldSlot] = currentCell;
                    if (_replayStands.TryGetValue(oldSlot, out var oldStand) && oldStand != null)
                    {
                        oldStand.transform.position = new Vector3(currentCell.x + 0.5f, 0.22f, currentCell.y + 0.5f);
                        oldStand.SetActive(true);
                    }

                    _replayCarrySlot = stationAtCell;
                    if (_replayStands.TryGetValue(stationAtCell, out var newStand) && newStand != null)
                    {
                        newStand.SetActive(false);
                    }
                }
            }

            // 3. UI Carry state update
            if (!string.IsNullOrEmpty(_replayCarrySlot))
            {
                var r = GetStationRole(_replayCarrySlot);
                var name = r == "quyuan" ? "屈原" : (r == "zhaojun" ? "王昭君" : _replayCarrySlot);
                var destStr = r == "quyuan" ? " [目标 楚国(8,8)]" : (r == "zhaojun" ? " [目标 西汉(7,7)]" : "");
                ReplayCarryStateUpdated?.Invoke("复盘携带: " + name + destStr);
            }
            else
            {
                ReplayCarryStateUpdated?.Invoke("复盘携带: 未携带人物");
            }
        }

        public void EndReplayStationVisuals()
        {
            _replayCarrySlot = null;
            foreach (var kvp in _replayStands)
            {
                if (kvp.Value != null)
                    Destroy(kvp.Value);
            }
            _replayStands.Clear();
            _replayStationCells.Clear();
            _replayDelivered.Clear();
            ReplayCarryStateUpdated?.Invoke("复盘携带: 未携带人物");
        }

        private void InitReplayStands()
        {
            foreach (var kvp in _replayStands)
            {
                if (kvp.Value != null)
                    Destroy(kvp.Value);
            }
            _replayStands.Clear();

            var shader = Shader.Find("Standard") ?? Shader.Find("Sprites/Default");

            for (var i = 0; i < _stations.Count; i++)
            {
                var station = _stations[i];
                var stand = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                stand.name = "ReplayStand_" + station.SlotId;
                var col = stand.GetComponent<Collider>();
                if (col != null)
                    Destroy(col);

                stand.transform.localScale = new Vector3(0.38f, 0.22f, 0.38f);

                var renderer = stand.GetComponent<Renderer>();
                if (renderer != null && shader != null)
                {
                    var mat = new Material(shader);
                    var role = GetStationRole(station.SlotId);
                    if (role == "quyuan")
                    {
                        mat.color = new Color(1.0f, 0.65f, 0.15f, 1f);
                    }
                    else if (role == "zhaojun")
                    {
                        mat.color = new Color(0.2f, 0.88f, 0.6f, 1f);
                    }
                    else
                    {
                        mat.color = i == 0 ? new Color(1.0f, 0.65f, 0.15f, 1f) : new Color(0.2f, 0.88f, 0.6f, 1f);
                    }
                    renderer.material = mat;
                }

                _replayStands[station.SlotId] = stand;
            }
        }

        private string GetStationRole(string slotId)
        {
            var station = FindStation(slotId);
            if (station != null && !string.IsNullOrEmpty(station.PredictedRole))
                return station.PredictedRole.ToLowerInvariant();
            if (_truthRoles.TryGetValue(slotId, out var truthRole) && !string.IsNullOrEmpty(truthRole))
                return truthRole.ToLowerInvariant();
            return "";
        }

        private static Vector2Int GetDestinationForRole(string role)
        {
            if (role.IndexOf("quyuan", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Vector2Int(8, 8);
            if (role.IndexOf("zhaojun", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Vector2Int(7, 7);
            return new Vector2Int(-1, -1);
        }
    }

    public sealed class StationRuntimeState
    {
        public string SlotId { get; private set; }
        public Vector2Int Cell { get; internal set; }
        public string ImagePath { get; private set; }
        public byte[] ImageBytes { get; internal set; }
        public string RecognitionId { get; internal set; }
        public string RecognitionSource { get; internal set; }
        public Vector2Int RecognitionCell { get; internal set; }
        public bool HasRecognitionCell { get; internal set; }
        public string PredictedRole { get; internal set; }
        public float Confidence { get; internal set; }
        public bool IsDiscovered { get; internal set; }
        public bool IsRecognized { get; internal set; }
        public bool IsVerified { get; internal set; }
        public string State { get; internal set; }
        public string ImageError { get; internal set; }

        internal StationRuntimeState(string slotId, Vector2Int cell, string imagePath)
        {
            SlotId = slotId;
            Cell = cell;
            ImagePath = imagePath;
            State = "waiting";
        }
    }

    public sealed class StationDiscovery
    {
        public StationRuntimeState Station { get; private set; }
        public byte[] ImageBytes { get; private set; }
        public string Error { get; private set; }
        public string RecognitionId { get { return Station == null ? null : Station.RecognitionId; } }
        public Vector2Int RecognitionCell { get { return Station == null ? Vector2Int.zero : Station.RecognitionCell; } }

        internal StationDiscovery(StationRuntimeState station, byte[] imageBytes, string error)
        {
            Station = station;
            ImageBytes = imageBytes;
            Error = error;
        }
    }

    public sealed class SlotSwitchEvent
    {
        public string PreviousCarrySlot { get; private set; }
        public string NewCarrySlot { get; private set; }
        public Vector2Int Cell { get; private set; }
        public PlannerDecisionDto Decision { get; private set; }

        internal SlotSwitchEvent(string previousCarrySlot, string newCarrySlot,
            Vector2Int cell, PlannerDecisionDto decision)
        {
            PreviousCarrySlot = previousCarrySlot;
            NewCarrySlot = newCarrySlot;
            Cell = cell;
            Decision = decision;
        }
    }

    public sealed class DeliveryValidationEvent
    {
        public string SlotId { get; private set; }
        public Vector2Int Cell { get; private set; }
        public bool Accepted { get; private set; }
        public string Message { get; private set; }

        internal DeliveryValidationEvent(string slotId, Vector2Int cell, bool accepted, string message)
        {
            SlotId = slotId;
            Cell = cell;
            Accepted = accepted;
            Message = message;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class EvaluationConfig
    {
        [JsonProperty("_template_only")] public bool? template_only;
        [JsonProperty("_fixture_only")] public bool? fixture_only;
        [JsonProperty("s1")] public EvaluationStationConfig s1;
        [JsonProperty("s2")] public EvaluationStationConfig s2;
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class EvaluationStationConfig
    {
        [JsonProperty("x")] public int? x;
        [JsonProperty("z")] public int? z;
        [JsonProperty("role")] public string role;
        [JsonProperty("image_path")] public string image_path;
    }
}

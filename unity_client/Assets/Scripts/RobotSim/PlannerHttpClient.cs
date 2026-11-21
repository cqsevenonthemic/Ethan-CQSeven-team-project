using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace Ethan.CQSeven.RobotSim
{
    public enum PlannerClientMode
    {
        Initializing,
        Live,
        Mock
    }

    /// <summary>Coroutine client for the frozen planner API, with isolated StreamingAssets mock fixtures.</summary>
    public sealed class PlannerHttpClient : MonoBehaviour
    {
        private const string ApiBaseUrl = "http://127.0.0.1:8000";
        private const string MockDirectory = "Mock";

        [SerializeField, Min(1)] private int timeoutSeconds = 5;

        private int _mockRecognitionIndex;
        private MockActionScript _mockActionScript;

        public event Action<PlannerClientMode, string> ModeChanged;
        public event Action<PlannerClientMode> InitialModeResolved;
        public event Action<string> StatusChanged;

        public PlannerClientMode Mode { get; private set; } = PlannerClientMode.Initializing;
        public bool IsInitialModeResolved { get; private set; }
        public string StatusMessage { get; private set; } = "Checking planner service availability.";
        public string ModeLabel
        {
            get
            {
                switch (Mode)
                {
                    case PlannerClientMode.Live: return "LIVE API";
                    case PlannerClientMode.Mock: return "MOCK (offline fixture)";
                    default: return "CONNECTING";
                }
            }
        }
        public string BaseUrl { get { return ApiBaseUrl; } }

        public IEnumerator NextAction(NextActionRequestDto payload,
            Action<ApiResult<NextActionResponseDto>> completed)
        {
            yield return PostJson("/next_action", payload, completed, () => GetMockNextAction(payload));
        }

        /// <summary>Resolves offline-fixture mode without consuming a planner action or recognition fixture.</summary>
        public IEnumerator ResolveInitialMode()
        {
            if (IsInitialModeResolved)
                yield break;

            using (var request = UnityWebRequest.Get(ApiBaseUrl + "/openapi.json"))
            {
                request.timeout = Mathf.Max(1, timeoutSeconds);
                yield return request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.ConnectionError && request.responseCode == 0)
                {
                    SetMode(PlannerClientMode.Mock,
                        "Planner service is unreachable; using the fixed offline Mock fixtures. " + request.error);
                }
                else
                {
                    // Any HTTP response proves the local server is reachable. A 404 is still Live mode;
                    // the required API endpoints report their own errors and never trigger a mock replay.
                    SetMode(PlannerClientMode.Live,
                        "Planner service responded at " + ApiBaseUrl + "; Live mode selected.");
                }
                CompleteInitialModeResolution();
            }
        }

        public IEnumerator Recognize(RecognizeRequestDto payload,
            Action<ApiResult<RecognizeResponseDto>> completed)
        {
            yield return PostJson("/recognize", payload, completed, GetMockRecognition);
        }

        public IEnumerator PlanSummary(PlanSummaryRequestDto payload,
            Action<ApiResult<PlanSummaryResponseDto>> completed)
        {
            yield return PostJson("/plan_summary", payload, completed, GetMockSummary);
        }

        private IEnumerator PostJson<TRequest, TResponse>(string route, TRequest payload,
            Action<ApiResult<TResponse>> completed, Func<ApiResult<TResponse>> mockResponse)
        {
            if (!IsInitialModeResolved)
            {
                if (completed != null)
                    completed(ApiResult<TResponse>.Failure("Planner mode has not been resolved.", 0, string.Empty));
                yield break;
            }

            if (Mode == PlannerClientMode.Mock)
            {
                if (completed != null)
                    completed(mockResponse());
                yield break;
            }

            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            var bytes = Encoding.UTF8.GetBytes(json);
            using (var request = new UnityWebRequest(ApiBaseUrl + route, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = Mathf.Max(1, timeoutSeconds);
                request.SetRequestHeader("Content-Type", "application/json");
                yield return request.SendWebRequest();

                var body = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
                if (request.result != UnityWebRequest.Result.Success)
                {
                    var detail = "POST " + route + " failed (HTTP " + request.responseCode + "): " +
                                 request.error + (string.IsNullOrEmpty(body) ? string.Empty : "\n" + body);
                    if (!IsInitialModeResolved && request.result == UnityWebRequest.Result.ConnectionError &&
                        request.responseCode == 0)
                    {
                        SetMode(PlannerClientMode.Mock,
                            "Planner unavailable on the first API request; using fixed Mock fixtures. " + request.error);
                        CompleteInitialModeResolution();
                        if (completed != null)
                            completed(mockResponse());
                        yield break;
                    }

                    if (!IsInitialModeResolved && request.responseCode > 0)
                    {
                        SetMode(PlannerClientMode.Live,
                            "Planner responded at " + ApiBaseUrl + "; keeping this session in Live mode.");
                        CompleteInitialModeResolution();
                    }
                    SetStatus(detail);
                    if (completed != null)
                        completed(ApiResult<TResponse>.Failure(detail, request.responseCode, body));
                    yield break;
                }

                try
                {
                    var response = JsonConvert.DeserializeObject<TResponse>(body);
                    if (response == null)
                        throw new JsonSerializationException("The API returned a JSON null response.");
                    if (!IsInitialModeResolved)
                    {
                        SetMode(PlannerClientMode.Live,
                            "Connected to planner service at " + ApiBaseUrl + ".");
                        CompleteInitialModeResolution();
                    }
                    if (completed != null)
                        completed(ApiResult<TResponse>.Success(response, request.responseCode, body));
                }
                catch (Exception exception)
                {
                    if (!IsInitialModeResolved && request.responseCode > 0)
                    {
                        SetMode(PlannerClientMode.Live,
                            "Planner responded at " + ApiBaseUrl + "; keeping this session in Live mode.");
                        CompleteInitialModeResolution();
                    }
                    var detail = "POST " + route + " returned invalid JSON: " + exception.Message +
                                 (string.IsNullOrEmpty(body) ? string.Empty : "\n" + body);
                    SetStatus(detail);
                    if (completed != null)
                        completed(ApiResult<TResponse>.Failure(detail, request.responseCode, body));
                }
            }
        }

        private ApiResult<NextActionResponseDto> GetMockNextAction(NextActionRequestDto request)
        {
            if (request == null || request.pose == null)
                return ApiResult<NextActionResponseDto>.Failure("Mock next_action needs a complete pose snapshot.", 0, string.Empty);
            if (!TryLoadActionScript(out var error))
                return ApiResult<NextActionResponseDto>.Failure(error, 0, string.Empty);

            var script = _mockActionScript;
            var actionIndex = Mathf.Max(0, request.step_count);
            var checkpoint = script.checkpoints == null || script.checkpoints.Count == 0
                ? null
                : script.checkpoints[0];
            if (script.checkpoints != null)
            {
                for (var i = 0; i < script.checkpoints.Count; i++)
                {
                    if (script.checkpoints[i].action_index > actionIndex)
                        break;
                    checkpoint = script.checkpoints[i];
                }
            }

            var hasAction = script.actions != null && actionIndex < script.actions.Count;
            var action = hasAction ? script.actions[actionIndex] : null;
            var endPose = new PoseDto { x = request.pose.x, z = request.pose.z, theta = request.pose.theta };
            if (action == ProtocolConstants.Actions.Forward)
            {
                switch (endPose.theta)
                {
                    case 0: endPose.z++; break;
                    case 90: endPose.x++; break;
                    case 180: endPose.z--; break;
                    case 270: endPose.x--; break;
                }
            }
            else if (action == ProtocolConstants.Actions.TurnLeft)
                endPose.theta = (endPose.theta + 270) % 360;
            else if (action == ProtocolConstants.Actions.TurnRight)
                endPose.theta = (endPose.theta + 90) % 360;

            var slots = checkpoint == null || checkpoint.slots == null
                ? new Dictionary<string, SlotDto>()
                : CloneSlots(checkpoint.slots);
            if (checkpoint != null && !string.IsNullOrEmpty(checkpoint.carry_slot))
            {
                SlotDto carriedSlot;
                if (slots.TryGetValue(checkpoint.carry_slot, out carriedSlot) && carriedSlot != null &&
                    carriedSlot.state == ProtocolConstants.SlotStates.Carried)
                {
                    carriedSlot.x = request.pose.x;
                    carriedSlot.z = request.pose.z;
                }
            }
            var deliveredCount = 0;
            foreach (var entry in slots)
                if (entry.Value != null && entry.Value.state == ProtocolConstants.SlotStates.Delivered)
                    deliveredCount++;

            var decision = checkpoint == null || checkpoint.decision == null
                ? new PlannerDecisionDto { switched = false, reason = "Offline Mock action fixture." }
                : checkpoint.decision;
            if (string.IsNullOrEmpty(decision.cost_scope))
                decision.cost_scope = decision.cost_keep.HasValue || decision.cost_switch.HasValue
                    ? "confirmed_map"
                    : "not_evaluated";

            var done = !hasAction && script.finish_when_actions_end && deliveredCount == 2;
            var trajectoryPoses = new List<PoseDto> { new PoseDto { x = request.pose.x, z = request.pose.z, theta = request.pose.theta } };
            var trajectoryActions = new List<string>();
            if (hasAction)
            {
                trajectoryActions.Add(action);
                trajectoryPoses.Add(endPose);
            }
            var response = new NextActionResponseDto
            {
                action = action,
                carry_slot = checkpoint == null ? null : checkpoint.carry_slot,
                slots = slots,
                plan_path = ToCellPath(trajectoryPoses),
                decision = decision,
                done = done,
                status = done ? ProtocolConstants.PlannerStatuses.Completed : (hasAction
                    ? (request.slots != null && request.slots.Count > 0 ? ProtocolConstants.PlannerStatuses.Planning : ProtocolConstants.PlannerStatuses.Exploring)
                    : ProtocolConstants.PlannerStatuses.AwaitingObservation),
                remaining_tasks = 2 - deliveredCount,
                trajectory = new TrajectoryDto
                {
                    poses = trajectoryPoses,
                    actions = trajectoryActions,
                    steps = trajectoryActions.Count
                },
                observation_seq = request.observation_seq,
                progress = new PlannerProgressDto
                {
                    fingerprint = new string('0', 64),
                    last_observation_seq = request.observation_seq,
                    last_step_count = request.step_count,
                    scan_attempts = request.progress == null ? 0 : request.progress.scan_attempts,
                    pending_scan = false
                }
            };

            var body = JsonConvert.SerializeObject(response, Formatting.None);
            SetStatus("Mock next_action fixture " + actionIndex + "/" + script.actions.Count + ".");
            return ApiResult<NextActionResponseDto>.Success(response, 200, body);
        }

        private ApiResult<RecognizeResponseDto> GetMockRecognition()
        {
            var fileName = _mockRecognitionIndex == 0
                ? "sample_recognition.json"
                : "sample_recognition_second.json";
            _mockRecognitionIndex++;
            return LoadMockResponse<RecognizeResponseDto>(fileName);
        }

        private ApiResult<PlanSummaryResponseDto> GetMockSummary()
        {
            return LoadMockResponse<PlanSummaryResponseDto>("sample_summary.json");
        }

        private ApiResult<T> LoadMockResponse<T>(string fileName)
        {
            string path;
            string body;
            if (!TryReadMockText(fileName, out path, out body))
                return ApiResult<T>.Failure("Mock fixture missing or unreadable: " + path, 0, string.Empty);

            try
            {
                var response = JsonConvert.DeserializeObject<T>(body);
                if (response == null)
                    throw new JsonSerializationException("Fixture JSON contains null.");
                SetStatus("Loaded Mock fixture " + fileName + ".");
                return ApiResult<T>.Success(response, 200, body);
            }
            catch (Exception exception)
            {
                var error = "Mock fixture " + path + " is invalid: " + exception.Message;
                SetStatus(error);
                return ApiResult<T>.Failure(error, 0, body);
            }
        }

        private bool TryLoadActionScript(out string error)
        {
            if (_mockActionScript != null)
            {
                error = null;
                return true;
            }

            string path;
            string body;
            if (!TryReadMockText("action_sequence.json", out path, out body))
            {
                error = "Mock action sequence missing or unreadable: " + path;
                SetStatus(error);
                return false;
            }

            try
            {
                _mockActionScript = JsonConvert.DeserializeObject<MockActionScript>(body);
                if (_mockActionScript == null || _mockActionScript.actions == null ||
                    _mockActionScript.checkpoints == null || !_mockActionScript.fixture_only)
                    throw new JsonSerializationException("Fixture must contain actions, checkpoints, and fixture_only=true.");
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Mock action sequence " + path + " is invalid: " + exception.Message;
                SetStatus(error);
                return false;
            }
        }

        private bool TryReadMockText(string fileName, out string path, out string body)
        {
            path = Path.Combine(Application.streamingAssetsPath, MockDirectory, fileName);
            body = null;
            try
            {
                if (!File.Exists(path))
                    return false;
                body = File.ReadAllText(path, Encoding.UTF8);
                return true;
            }
            catch (Exception exception)
            {
                SetStatus("Could not read Mock fixture " + path + ": " + exception.Message);
                return false;
            }
        }

        private static Dictionary<string, SlotDto> CloneSlots(Dictionary<string, SlotDto> source)
        {
            return JsonConvert.DeserializeObject<Dictionary<string, SlotDto>>(
                JsonConvert.SerializeObject(source, Formatting.None));
        }

        private static int[][] ClonePath(int[][] source)
        {
            return JsonConvert.DeserializeObject<int[][]>(JsonConvert.SerializeObject(source, Formatting.None));
        }

        private static int[][] ToCellPath(List<PoseDto> poses)
        {
            var result = new int[poses.Count][];
            for (var i = 0; i < poses.Count; i++)
                result[i] = new[] { poses[i].x, poses[i].z };
            return result;
        }

        private void SetMode(PlannerClientMode mode, string message)
        {
            Mode = mode;
            SetStatus(message);
            Debug.Log("Planner mode: " + ModeLabel + ". " + StatusMessage, this);
            ModeChanged?.Invoke(Mode, StatusMessage);
        }

        private void CompleteInitialModeResolution()
        {
            if (IsInitialModeResolved)
                return;
            IsInitialModeResolved = true;
            InitialModeResolved?.Invoke(Mode);
        }

        private void SetStatus(string message)
        {
            StatusMessage = message;
            StatusChanged?.Invoke(message);
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class MockActionScript
    {
        [JsonProperty("fixture_only")] public bool fixture_only;
        [JsonProperty("finish_when_actions_end")] public bool finish_when_actions_end;
        [JsonProperty("actions")] public List<string> actions;
        [JsonProperty("checkpoints")] public List<MockActionCheckpoint> checkpoints;
    }

    [JsonObject(MemberSerialization.OptIn)]
    internal sealed class MockActionCheckpoint
    {
        [JsonProperty("action_index")] public int action_index;
        [JsonProperty("carry_slot")] public string carry_slot;
        [JsonProperty("slots")] public Dictionary<string, SlotDto> slots;
        [JsonProperty("plan_path")] public int[][] plan_path;
        [JsonProperty("decision")] public PlannerDecisionDto decision;
    }
}

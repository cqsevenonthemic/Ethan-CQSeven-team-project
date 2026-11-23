using System.Collections.Generic;
using Newtonsoft.Json;

namespace Ethan.CQSeven.RobotSim
{
    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PoseDto
    {
        [JsonProperty("x")] public int x;
        [JsonProperty("z")] public int z;
        [JsonProperty("theta")] public int theta;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class SlotDto
    {
        [JsonProperty("x")] public int x;
        [JsonProperty("z")] public int z;
        [JsonProperty("pred_role")] public string pred_role;
        [JsonProperty("verified")] public bool verified;
        [JsonProperty("state")] public string state;
        [JsonProperty("recognition_id")] public string recognition_id;
        [JsonProperty("recognition_source")] public string recognition_source;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class NextActionEventDto
    {
        [JsonProperty("type")] public string type;
        [JsonProperty("slot_id", NullValueHandling = NullValueHandling.Ignore)] public string slot_id;
        [JsonProperty("x", NullValueHandling = NullValueHandling.Ignore)] public int? x;
        [JsonProperty("z", NullValueHandling = NullValueHandling.Ignore)] public int? z;
        [JsonProperty("pred_role", NullValueHandling = NullValueHandling.Ignore)] public string pred_role;
        [JsonProperty("confidence", NullValueHandling = NullValueHandling.Ignore)] public float? confidence;
        [JsonProperty("rejected_at", NullValueHandling = NullValueHandling.Ignore)] public PoseCellDto rejected_at;
        [JsonProperty("recognition_id", NullValueHandling = NullValueHandling.Ignore)] public string recognition_id;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PoseCellDto
    {
        [JsonProperty("x")] public int x;
        [JsonProperty("z")] public int z;

        public PoseCellDto() { }

        public PoseCellDto(int cellX, int cellZ)
        {
            x = cellX;
            z = cellZ;
        }
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class NextActionRequestDto
    {
        [JsonProperty("pose")] public PoseDto pose;
        [JsonProperty("seen_walls")] public int[][] seen_walls;
        [JsonProperty("known_walls")] public int[][] known_walls;
        [JsonProperty("step_count")] public int step_count;
        [JsonProperty("carry_slot", NullValueHandling = NullValueHandling.Include)] public string carry_slot;
        [JsonProperty("slots")] public Dictionary<string, SlotDto> slots;
        [JsonProperty("event", NullValueHandling = NullValueHandling.Include)] public NextActionEventDto @event;
        [JsonProperty("visited_cells", NullValueHandling = NullValueHandling.Ignore)] public int[][] visited_cells;
        [JsonProperty("observation_seq")] public long observation_seq;
        [JsonProperty("progress", NullValueHandling = NullValueHandling.Ignore)] public PlannerProgressDto progress;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlannerDecisionDto
    {
        [JsonProperty("switched")] public bool switched;
        [JsonProperty("cost_keep")] public int? cost_keep;
        [JsonProperty("cost_switch")] public int? cost_switch;
        [JsonProperty("cost_scope")] public string cost_scope;
        [JsonProperty("reason")] public string reason;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class NextActionResponseDto
    {
        [JsonProperty("action")] public string action;
        [JsonProperty("carry_slot")] public string carry_slot;
        [JsonProperty("slots")] public Dictionary<string, SlotDto> slots;
        [JsonProperty("plan_path")] public int[][] plan_path;
        [JsonProperty("decision")] public PlannerDecisionDto decision;
        [JsonProperty("done")] public bool done;
        [JsonProperty("status")] public string status;
        [JsonProperty("remaining_tasks")] public int remaining_tasks;
        [JsonProperty("trajectory")] public TrajectoryDto trajectory;
        [JsonProperty("observation_seq")] public long observation_seq;
        [JsonProperty("progress")] public PlannerProgressDto progress;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlannerProgressDto
    {
        [JsonProperty("fingerprint")] public string fingerprint;
        [JsonProperty("last_observation_seq")] public long last_observation_seq;
        [JsonProperty("last_step_count")] public int last_step_count;
        [JsonProperty("scan_attempts")] public int scan_attempts;
        [JsonProperty("pending_scan")] public bool pending_scan;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class TrajectoryDto
    {
        [JsonProperty("poses")] public List<PoseDto> poses;
        [JsonProperty("actions")] public List<string> actions;
        [JsonProperty("steps")] public int steps;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class RecognizeRequestDto
    {
        [JsonProperty("image_base64")] public string image_base64;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class RecognizeResponseDto
    {
        [JsonProperty("pred_role")] public string pred_role;
        [JsonProperty("confidence")] public float confidence;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlanSummaryRequestDto
    {
        [JsonProperty("start_pose")] public PoseDto start_pose;
        [JsonProperty("seen_walls")] public int[][] seen_walls;
        [JsonProperty("known_walls")] public int[][] known_walls;
        [JsonProperty("initial_targets")] public Dictionary<string, PoseCellDto> initial_targets;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class OptimalStepsDto
    {
        [JsonProperty("total")] public int total;
        [JsonProperty("forward")] public int forward;
        [JsonProperty("turn")] public int turn;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class EpisodeTrajectoryDto
    {
        [JsonProperty("poses")] public List<PoseDto> poses;
        [JsonProperty("actions")] public List<string> actions;
        [JsonProperty("steps")] public int steps;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class EpisodeRecordDto
    {
        [JsonProperty("episode")] public int episode;
        [JsonProperty("behavior")] public string behavior;
        [JsonProperty("epsilon")] public float epsilon;
        [JsonProperty("reward")] public float reward;
        [JsonProperty("completed")] public bool completed;
        [JsonProperty("failure")] public string failure;
        [JsonProperty("turns")] public int turns;
        [JsonProperty("switch_steps")] public List<int> switch_steps;
        [JsonProperty("trajectory")] public EpisodeTrajectoryDto trajectory;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class PlanSummaryResponseDto
    {
        [JsonProperty("explored_trajectories")] public List<int[][]> explored_trajectories;
        [JsonProperty("episode_steps")] public List<int> episode_steps;
        [JsonProperty("optimal_path")] public int[][] optimal_path;
        [JsonProperty("optimal_actions")] public List<string> optimal_actions;
        [JsonProperty("optimal_steps")] public OptimalStepsDto optimal_steps;
        [JsonProperty("episode_records")] public List<EpisodeRecordDto> episode_records;
        [JsonProperty("optimal_episode")] public int optimal_episode;
        [JsonProperty("training_seed")] public int? training_seed;
        [JsonProperty("training_episodes")] public int? training_episodes;
        [JsonProperty("exact_steps")] public int? exact_steps;
        [JsonProperty("map_scope")] public string map_scope;
        [JsonProperty("optimality_proven")] public bool optimality_proven;
        [JsonProperty("switch_steps")] public List<int> switch_steps;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ReplayAuditPoseDto
    {
        [JsonProperty("cell")] public int[] cell;
        [JsonProperty("world_position")] public float[] world_position;
        [JsonProperty("heading")] public string heading;
        [JsonProperty("heading_degrees")] public float heading_degrees;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ReplayAuditStateDto
    {
        [JsonProperty("carried_slot")] public string carried_slot;
        [JsonProperty("carried_role")] public string carried_role;
        [JsonProperty("target_character")] public string target_character;
        [JsonProperty("station_cells")] public Dictionary<string, int[]> station_cells;
        [JsonProperty("delivered_status")] public Dictionary<string, bool> delivered_status;
        [JsonProperty("is_success")] public bool is_success;
        [JsonProperty("rejection_or_error")] public string rejection_or_error;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ReplayAuditEventDto
    {
        [JsonProperty("event_type")] public string event_type;
        [JsonProperty("step_index")] public int step_index;
        [JsonProperty("step_index_0based")] public int step_index_0based;
        [JsonProperty("step_index_basis")] public string step_index_basis = "1-based action step";
        [JsonProperty("timestamp")] public string timestamp;
        [JsonProperty("details")] public string details;
        [JsonProperty("pose")] public ReplayAuditPoseDto pose;
        [JsonProperty("state")] public ReplayAuditStateDto state;
    }

    [JsonObject(MemberSerialization.OptIn)]
    public sealed class ReplayAuditReportDto
    {
        [JsonProperty("report_time")] public string report_time;
        [JsonProperty("mode")] public string mode;
        [JsonProperty("total_actions")] public int total_actions;
        [JsonProperty("switch_steps_configured")] public List<int> switch_steps_configured;
        [JsonProperty("all_delivered")] public bool all_delivered;
        [JsonProperty("passed")] public bool passed;
        [JsonProperty("failure_reason")] public string failure_reason;
        [JsonProperty("events")] public List<ReplayAuditEventDto> events;
    }

    public sealed class ApiResult<T>
    {
        public bool Succeeded { get; private set; }
        public T Value { get; private set; }
        public string Error { get; private set; }
        public long StatusCode { get; private set; }
        public string ResponseBody { get; private set; }

        private ApiResult(bool succeeded, T value, string error, long statusCode, string responseBody)
        {
            Succeeded = succeeded;
            Value = value;
            Error = error;
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }

        public static ApiResult<T> Success(T value, long statusCode, string responseBody)
        {
            return new ApiResult<T>(true, value, null, statusCode, responseBody);
        }

        public static ApiResult<T> Failure(string error, long statusCode, string responseBody)
        {
            return new ApiResult<T>(false, default(T), error, statusCode, responseBody);
        }
    }
}

using System;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Centralized protocol constants and string identifiers across the RobotSim domain.
    /// Eliminates magic strings and provides compile-time safety.
    /// </summary>
    public static class ProtocolConstants
    {
        public static class Roles
        {
            public const string QuYuan = "quyuan";
            public const string ZhaoJun = "zhaojun";

            public static string ToDisplayName(string role, string fallback = null)
            {
                if (string.IsNullOrEmpty(role)) return fallback ?? string.Empty;
                if (role.IndexOf(QuYuan, StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("屈原", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "屈原";
                if (role.IndexOf(ZhaoJun, StringComparison.OrdinalIgnoreCase) >= 0 || role.IndexOf("昭君", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "王昭君";
                return role;
            }
        }

        public static class SlotIds
        {
            public const string S1 = "s1";
            public const string S2 = "s2";
        }

        public static class SlotStates
        {
            public const string Discovered = "discovered";
            public const string Waiting = "waiting";
            public const string Carried = "carried";
            public const string Delivered = "delivered";
        }

        public static class Actions
        {
            public const string Forward = "forward";
            public const string TurnLeft = "turn_left";
            public const string TurnRight = "turn_right";
        }

        public static class PlannerStatuses
        {
            public const string Planning = "planning";
            public const string Exploring = "exploring";
            public const string AwaitingDelivery = "awaiting_delivery";
            public const string AwaitingObservation = "awaiting_observation";
            public const string Completed = "completed";
            public const string Unreachable = "unreachable";
        }

        public static class MapScopes
        {
            public const string CompleteMap = "complete_map";
            public const string ConfirmedSubgraph = "confirmed_subgraph";
        }

        public static class EventTypes
        {
            public const string TriggerDiscovered = "trigger_discovered";
            public const string DeliveryRejected = "delivery_rejected";
        }

        public static class RecognitionSources
        {
            public const string Mock = "mock";
            public const string Onnx = "onnx";
        }
    }
}

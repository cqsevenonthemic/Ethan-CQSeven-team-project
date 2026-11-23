using System;
using System.Collections.Generic;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Rejects malformed or stale planner snapshots before Unity applies state or executes an action.</summary>
    public static class PlannerResponseValidator
    {
        private const string Planning = ProtocolConstants.PlannerStatuses.Planning;
        private const string Exploring = ProtocolConstants.PlannerStatuses.Exploring;
        private const string AwaitingDelivery = ProtocolConstants.PlannerStatuses.AwaitingDelivery;
        private const string AwaitingObservation = ProtocolConstants.PlannerStatuses.AwaitingObservation;
        private const string Completed = ProtocolConstants.PlannerStatuses.Completed;
        private const string Unreachable = ProtocolConstants.PlannerStatuses.Unreachable;

        public static bool TryValidate(NextActionRequestDto request, NextActionResponseDto response,
            out string error)
        {
            error = null;
            if (request == null || request.pose == null)
                return Fail("The request snapshot or pose is missing.", out error);
            if (response == null || response.slots == null || response.decision == null ||
                response.trajectory == null || response.trajectory.poses == null ||
                response.trajectory.actions == null || response.progress == null || response.plan_path == null)
                return Fail("The planner response is missing a required contract field.", out error);
            if (response.observation_seq != request.observation_seq ||
                response.progress.last_observation_seq != request.observation_seq ||
                response.progress.last_step_count != request.step_count)
                return Fail("The response refers to a different observation or step snapshot.", out error);
            if (string.IsNullOrEmpty(response.progress.fingerprint) || response.progress.fingerprint.Length != 64 ||
                response.progress.scan_attempts < 0 || response.progress.scan_attempts > 4)
                return Fail("The response progress is malformed.", out error);
            for (var i = 0; i < response.progress.fingerprint.Length; i++)
                if (!Uri.IsHexDigit(response.progress.fingerprint[i]))
                    return Fail("The response progress fingerprint must be a SHA-256 hex string.", out error);
            if (response.remaining_tasks < 0 || response.remaining_tasks > 2)
                return Fail("remaining_tasks must be between zero and two.", out error);

            var canExecute = response.status == Planning || response.status == Exploring;
            if (!canExecute && response.status != AwaitingDelivery && response.status != AwaitingObservation &&
                response.status != Completed && response.status != Unreachable)
                return Fail("The planner returned an unknown status: " + response.status, out error);
            if (response.done != (response.status == Completed))
                return Fail("done must be true exactly when status is completed.", out error);

            if (response.trajectory.steps < 0 ||
                response.trajectory.actions.Count != response.trajectory.steps ||
                response.trajectory.poses.Count != response.trajectory.steps + 1)
                return Fail("The trajectory pose, action, and step counts do not agree.", out error);
            if (response.trajectory.poses.Count == 0 ||
                !SamePose(request.pose, response.trajectory.poses[0]))
                return Fail("The trajectory preview does not begin at the request pose.", out error);
            for (var i = 0; i < response.trajectory.poses.Count; i++)
                if (!IsValidPose(response.trajectory.poses[i]))
                    return Fail("The trajectory contains an out-of-bounds or invalid pose.", out error);

            for (var i = 0; i < response.trajectory.actions.Count; i++)
                if (!MatchesTransition(response.trajectory.poses[i], response.trajectory.poses[i + 1],
                    response.trajectory.actions[i]))
                    return Fail("The trajectory preview contains an action/pose mismatch.", out error);

            if (canExecute)
            {
                if (!IsAction(response.action) || response.trajectory.steps == 0 ||
                    response.trajectory.actions[0] != response.action)
                    return Fail("An executable status must provide a valid first action and matching trajectory preview.", out error);
            }
            else if (response.action != null || response.trajectory.steps != 0 ||
                     response.trajectory.actions.Count != 0 || response.trajectory.poses.Count != 1)
            {
                return Fail("Waiting and terminal statuses must have action:null and a zero-step trajectory.", out error);
            }

            if (response.decision.cost_scope != "not_evaluated" && response.decision.cost_scope != "confirmed_map")
                return Fail("The planner returned an unknown decision cost_scope.", out error);
            if ((response.decision.cost_keep.HasValue && response.decision.cost_keep.Value < 0) ||
                (response.decision.cost_switch.HasValue && response.decision.cost_switch.Value < 0))
                return Fail("Decision costs cannot be negative.", out error);

            var carriedCount = 0;
            var deliveredCount = 0;
            var verifiedRoles = new HashSet<string>();
            foreach (KeyValuePair<string, SlotDto> entry in response.slots)
            {
                var slot = entry.Value;
                if ((entry.Key != ProtocolConstants.SlotIds.S1 && entry.Key != ProtocolConstants.SlotIds.S2) || slot == null)
                    return Fail("The response contains an invalid slot.", out error);
                if (slot.pred_role != ProtocolConstants.Roles.QuYuan && slot.pred_role != ProtocolConstants.Roles.ZhaoJun)
                    return Fail("The response contains an invalid predicted role for " + entry.Key + ".", out error);
                if (slot.x < 0 || slot.x >= 16 || slot.z < 0 || slot.z >= 16)
                    return Fail("The response contains an out-of-bounds slot for " + entry.Key + ".", out error);
                if (slot.state != ProtocolConstants.SlotStates.Waiting && slot.state != ProtocolConstants.SlotStates.Carried && slot.state != ProtocolConstants.SlotStates.Delivered)
                    return Fail("The response contains an invalid state for " + entry.Key + ".", out error);
                var requestHadSlot = request.slots != null && request.slots.ContainsKey(entry.Key);
                var discoveryEventMatches = request.@event != null && request.@event.type == ProtocolConstants.EventTypes.TriggerDiscovered &&
                                            request.@event.slot_id == entry.Key;
                if (!requestHadSlot && !discoveryEventMatches)
                    return Fail("The response introduced a slot that was not in the request snapshot.", out error);
                if (slot.state == ProtocolConstants.SlotStates.Carried)
                {
                    carriedCount++;
                    if (entry.Key != response.carry_slot || slot.x != request.pose.x || slot.z != request.pose.z)
                        return Fail("The carried slot must match carry_slot and the current pose.", out error);
                }
                if (slot.state == ProtocolConstants.SlotStates.Delivered)
                {
                    deliveredCount++;
                    if (!slot.verified)
                        return Fail("A delivered slot must be verified.", out error);
                    var dest = MazeLayout.GetDestinationForRole(slot.pred_role);
                    var expected = new PoseCellDto(dest.x, dest.y);
                    if (slot.x != expected.x || slot.z != expected.z)
                        return Fail("A delivered slot must be at the correct destination.", out error);
                }
                if (slot.verified && !verifiedRoles.Add(slot.pred_role))
                    return Fail("Two verified slots cannot have the same role.", out error);
                if (slot.recognition_source != null && slot.recognition_source != ProtocolConstants.RecognitionSources.Mock &&
                    slot.recognition_source != ProtocolConstants.RecognitionSources.Onnx)
                    return Fail("The response contains an unknown recognition_source.", out error);
            }

            if ((response.carry_slot == null && carriedCount != 0) ||
                (response.carry_slot != null && carriedCount != 1))
                return Fail("carry_slot must identify the only carried slot, if any.", out error);
            if (response.remaining_tasks != 2 - deliveredCount)
                return Fail("remaining_tasks does not match the delivered slot count.", out error);
            if (response.status == Completed && (response.remaining_tasks != 0 || response.carry_slot != null))
                return Fail("A completed response must have no remaining tasks or carried slot.", out error);
            if (response.status == AwaitingDelivery && response.carry_slot == null)
                return Fail("awaiting_delivery requires a carried slot.", out error);

            return true;
        }

        private static bool IsAction(string action)
        {
            return action == ProtocolConstants.Actions.Forward ||
                   action == ProtocolConstants.Actions.TurnLeft ||
                   action == ProtocolConstants.Actions.TurnRight;
        }

        private static bool SamePose(PoseDto left, PoseDto right)
        {
            return right != null && left.x == right.x && left.z == right.z && left.theta == right.theta;
        }

        private static bool IsValidPose(PoseDto pose)
        {
            return pose != null && pose.x >= 0 && pose.x < 16 && pose.z >= 0 && pose.z < 16 &&
                   (pose.theta == 0 || pose.theta == 90 || pose.theta == 180 || pose.theta == 270);
        }

        private static bool MatchesTransition(PoseDto start, PoseDto end, string action)
        {
            if (start == null || end == null)
                return false;
            var x = start.x;
            var z = start.z;
            var theta = start.theta;
            switch (action)
            {
                case ProtocolConstants.Actions.Forward:
                    if (theta == 0) z++;
                    else if (theta == 90) x++;
                    else if (theta == 180) z--;
                    else x--;
                    break;
                case ProtocolConstants.Actions.TurnLeft: theta = (theta + 270) % 360; break;
                case ProtocolConstants.Actions.TurnRight: theta = (theta + 90) % 360; break;
                default: return false;
            }
            return end.x == x && end.z == z && end.theta == theta;
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}

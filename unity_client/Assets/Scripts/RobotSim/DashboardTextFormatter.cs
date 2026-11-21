using System;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Pure static formatter and translator for dashboard UI strings, status mapping,
    /// route qualification titles, error condition detection, and RL summary generation.
    /// Eliminates magic strings and hardcoded coordinates.
    /// </summary>
    public static class DashboardTextFormatter
    {
        public static string GetRouteQualificationTitle(PlanSummaryResponseDto summary)
        {
            if (summary != null && summary.optimality_proven && summary.map_scope == ProtocolConstants.MapScopes.CompleteMap)
                return "全图理论最优路线";
            if (summary != null && summary.optimality_proven)
                return "已确认子图理论最优路线";
            return "训练最佳回合路线（未证最优）";
        }

        public static string GetRouteQualificationBadgeTitle(PlanSummaryResponseDto summary)
        {
            if (summary != null && summary.optimality_proven && summary.map_scope == ProtocolConstants.MapScopes.CompleteMap)
                return "第三幕: 全图最优复盘";
            if (summary != null && summary.optimality_proven)
                return "第三幕: 子图最优复盘";
            return "第三幕: 最佳回合复盘";
        }

        public static string FormatSummaryText(PlanSummaryResponseDto summary)
        {
            if (summary == null || summary.optimal_steps == null)
                return string.Empty;

            var provenTag = summary.optimality_proven
                ? "<color=#50fa7b>[理论最优·已数学证明]</color>"
                : "<color=#ffb86c>[训练最佳回合·未证最优]</color>";
            var scopeTag = summary.map_scope == ProtocolConstants.MapScopes.CompleteMap ? "全图范围" : "已确认子图";
            var episodeStr = summary.optimal_episode > 0 ? "回合 #" + summary.optimal_episode : "40 轮训练最佳";
            var exactStr = summary.exact_steps.HasValue ? "\n理论精确下界: " + summary.exact_steps.Value + " 步" : "";

            return provenTag + "\n" +
                "范围: " + scopeTag + " | " + episodeStr + "\n" +
                "最佳步数: " + summary.optimal_steps.total + " 步 (直行 " + summary.optimal_steps.forward + " | 转向 " + summary.optimal_steps.turn + ")" +
                exactStr;
        }

        public static string TranslateStatusMessage(string message, PlanSummaryResponseDto summary)
        {
            if (string.IsNullOrEmpty(message)) return string.Empty;

            var routeTitle = GetRouteQualificationTitle(summary);

            if (message.Contains("Three-act demonstration completed successfully"))
                return "三幕全流程演示完成 · 已按" + routeTitle + "复盘";
            if (message.Contains("Optimal trajectory replay completed successfully"))
                return "第三幕: " + routeTitle + "复盘完成";
            if (message.Contains("Replaying optimal converged trajectory"))
                return "第三幕: 正在以 2x 速度复盘" + routeTitle + "...";
            if (message.Contains("Reinforcement Learning exploration visualization"))
                return "第二幕: 强化学习多探索轨迹演进中 (20x 加速)...";
            if (message.Contains("Requesting Act 2 RL Plan Summary"))
                return "正在请求强化学习探索汇总 (Plan Summary)...";
            if (message.Contains("Both state machines were verified at their correct destinations"))
                return "双站点人物均已送达指定终点，核验通过";
            if (message.Contains("Known reachable area has no remaining task route"))
                return "已知连通区域已全部探索完成";
            if (message.Contains("Planner is waiting for new sensor information"))
                return "等待雷达传感器获取新地图特征...";
            if (message.Contains("Awaiting a scan that adds new walls"))
                return "等待雷达扫描探测未知边界...";
            if (message.Contains("Discarded a late response"))
                return "本地快照已变更，忽略过时决策响应";
            if (message.StartsWith("Act 1 Complete."))
                return message.Replace("Act 1 Complete.", "第一幕探索完成 ·");
            if (message.StartsWith("Act 1 completed."))
                return message.Replace("Act 1 completed.", "第一幕探索完成 ·");

            return message;
        }

        public static bool IsErrorStatus(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            return message.Contains("failed") || message.Contains("stopped") ||
                   message.Contains("error") || message.Contains("501") || message.Contains("422") ||
                   message.Contains("失败") || message.Contains("遇阻");
        }

        public static string FormatRoleChinese(string predictedRole)
        {
            if (string.Equals(predictedRole, ProtocolConstants.Roles.QuYuan, StringComparison.OrdinalIgnoreCase))
                return "屈原 (Qu Yuan)";
            if (string.Equals(predictedRole, ProtocolConstants.Roles.ZhaoJun, StringComparison.OrdinalIgnoreCase))
                return "昭君 (Zhao Jun)";
            return predictedRole ?? string.Empty;
        }

        public static string FormatStationCarryText(string slotId, string predictedRole)
        {
            var destCell = MazeLayout.GetDestinationForRole(predictedRole);
            var destStr = MazeLayout.FormatCellCoord(destCell);
            return "携带状态: [Slot " + slotId + " 已装载] -> 目标 (" + destStr + ")";
        }

        public static string FormatStationLocation(string slotId, Vector2Int cell, string suffix)
        {
            return "站点位置: " + slotId + " (" + cell.x + ", " + cell.y + ") [" + suffix + "]";
        }

        public static string FormatStepStats(RobotController robot)
        {
            if (robot == null) return string.Empty;
            if (robot.IsReplayMode)
            {
                return "复盘步数: " + robot.ReplayStepCount +
                       " | 直行: " + robot.ReplayForwardStepCount +
                       " | 左转: " + robot.ReplayLeftTurnStepCount +
                       " | 右转: " + robot.ReplayRightTurnStepCount;
            }
            return "探索步数: " + robot.TotalStepCount +
                   " | 直行: " + robot.ForwardStepCount +
                   " | 左转: " + robot.LeftTurnStepCount +
                   " | 右转: " + robot.RightTurnStepCount;
        }

        public static string FormatPoseText(RobotPose pose)
        {
            return "位姿监控: 坐标 (" + pose.Cell.x + ", " + pose.Cell.y +
                   ") | 朝向 " + pose.HeadingDegrees + "° (" + pose.Heading + ")";
        }

        public static string FormatRlProgress(int currentEpisode, int totalEpisodes, int currentSteps, int bestSteps)
        {
            return "[强化学习寻优中 (Q-Learning)]\n" +
                   "Episode " + currentEpisode + " / " + totalEpisodes + " 轮\n" +
                   "本轮步数: " + currentSteps + " 步 -> 历史最优: " + bestSteps + " 步";
        }
    }
}

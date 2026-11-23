using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Cardinal heading degrees are fixed by the simulation contract.</summary>
    public enum RobotHeading
    {
        North = 0,
        East = 90,
        South = 180,
        West = 270
    }

    public enum RobotAction
    {
        Forward,
        TurnLeft,
        TurnRight
    }

    /// <summary>Logical pose. Cell.x is east-west; Cell.y is the maze's north-south grid coordinate.</summary>
    public struct RobotPose
    {
        public Vector2Int Cell { get; private set; }
        public RobotHeading Heading { get; private set; }
        public int HeadingDegrees { get { return (int)Heading; } }

        public RobotPose(Vector2Int cell, RobotHeading heading)
        {
            Cell = cell;
            Heading = heading;
        }

        public static RobotHeading HeadingFromDegrees(int degrees)
        {
            var normalized = ((degrees % 360) + 360) % 360;
            switch (normalized)
            {
                case 90: return RobotHeading.East;
                case 180: return RobotHeading.South;
                case 270: return RobotHeading.West;
                default: return RobotHeading.North;
            }
        }
    }

    public struct RobotActionResult
    {
        public RobotAction Action { get; private set; }
        public RobotPose StartPose { get; private set; }
        public RobotPose EndPose { get; private set; }
        public bool Succeeded { get; private set; }
        public bool BlockedByWall { get; private set; }
        public float DurationSeconds { get; private set; }

        public RobotActionResult(RobotAction action, RobotPose startPose, RobotPose endPose,
            bool succeeded, bool blockedByWall, float durationSeconds)
        {
            Action = action;
            StartPose = startPose;
            EndPose = endPose;
            Succeeded = succeeded;
            BlockedByWall = blockedByWall;
            DurationSeconds = durationSeconds;
        }
    }
}

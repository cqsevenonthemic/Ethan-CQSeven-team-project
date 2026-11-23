namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Four-bit cell-wall encoding: north=1, east=2, south=4, west=8.</summary>
    public static class WallDirection
    {
        public const byte North = 1;
        public const byte East = 2;
        public const byte South = 4;
        public const byte West = 8;
        public const byte All = North | East | South | West;

        public static byte Opposite(byte direction)
        {
            switch (direction)
            {
                case North: return South;
                case East: return West;
                case South: return North;
                case West: return East;
                default: return 0;
            }
        }

        public static bool TryGetOffset(byte direction, out int x, out int z)
        {
            switch (direction)
            {
                case North: x = 0; z = 1; return true;
                case East: x = 1; z = 0; return true;
                case South: x = 0; z = -1; return true;
                case West: x = -1; z = 0; return true;
                default: x = 0; z = 0; return false;
            }
        }

        public static byte FromHeading(RobotHeading heading)
        {
            switch (heading)
            {
                case RobotHeading.North: return North;
                case RobotHeading.East: return East;
                case RobotHeading.South: return South;
                case RobotHeading.West: return West;
                default: return 0;
            }
        }
    }
}

using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Independent snapshot of observed state only; it contains no actual maze masks.</summary>
    public sealed class ObservedMaskSnapshot
    {
        private readonly byte[] _seenWalls;
        private readonly byte[] _knownWalls;

        public int Width { get; private set; }
        public int Height { get; private set; }

        internal ObservedMaskSnapshot(int width, int height, byte[] seenWalls, byte[] knownWalls)
        {
            Width = width;
            Height = height;
            _seenWalls = (byte[])seenWalls.Clone();
            _knownWalls = (byte[])knownWalls.Clone();
        }

        public byte[] CopySeenWalls()
        {
            return (byte[])_seenWalls.Clone();
        }

        public byte[] CopyKnownWalls()
        {
            return (byte[])_knownWalls.Clone();
        }

        public bool TryGetSeenWalls(Vector2Int cell, out byte mask)
        {
            return TryGetMask(_seenWalls, cell, out mask);
        }

        public bool TryGetKnownWalls(Vector2Int cell, out byte mask)
        {
            return TryGetMask(_knownWalls, cell, out mask);
        }

        private bool TryGetMask(byte[] masks, Vector2Int cell, out byte mask)
        {
            if (cell.x < 0 || cell.x >= Width || cell.y < 0 || cell.y >= Height)
            {
                mask = 0;
                return false;
            }

            mask = masks[cell.y * Width + cell.x];
            return true;
        }
    }
}

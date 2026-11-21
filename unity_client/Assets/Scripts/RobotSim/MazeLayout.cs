using System;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Fixed 16x16 actual maze. Rows are ordered south-to-north and columns west-to-east.
    /// These masks are Unity simulation data and must never be copied into network DTOs.
    /// </summary>
    internal static class MazeLayout
    {
        internal const int Width = 16;
        internal const int Height = 16;
        internal const float CellSize = 1.0f;
        internal static readonly Vector2Int StartCell = new Vector2Int(0, 0);
        internal const int DestinationCount = 2;

        public static readonly Vector2Int QuYuanDestination = new Vector2Int(8, 8);
        public static readonly Vector2Int ZhaoJunDestination = new Vector2Int(7, 7);

        private static readonly Vector2Int[] Destinations =
        {
            QuYuanDestination,
            ZhaoJunDestination
        };

        internal static Vector2Int GetDestination(int index)
        {
            if (index < 0 || index >= Destinations.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Destinations[index];
        }

        public static Vector2Int GetDestinationForRole(string role)
        {
            if (string.IsNullOrEmpty(role))
                return new Vector2Int(-1, -1);
            if (role.IndexOf(ProtocolConstants.Roles.QuYuan, StringComparison.OrdinalIgnoreCase) >= 0 ||
                role.IndexOf("屈原", StringComparison.OrdinalIgnoreCase) >= 0)
                return QuYuanDestination;
            if (role.IndexOf(ProtocolConstants.Roles.ZhaoJun, StringComparison.OrdinalIgnoreCase) >= 0 ||
                role.IndexOf("昭君", StringComparison.OrdinalIgnoreCase) >= 0)
                return ZhaoJunDestination;
            return new Vector2Int(-1, -1);
        }

        public static string FormatCellCoord(Vector2Int cell)
        {
            return cell.x + "," + cell.y;
        }

        public static string GetDestinationDisplayName(string role)
        {
            if (string.IsNullOrEmpty(role))
                return string.Empty;
            if (role.IndexOf(ProtocolConstants.Roles.QuYuan, StringComparison.OrdinalIgnoreCase) >= 0 ||
                role.IndexOf("屈原", StringComparison.OrdinalIgnoreCase) >= 0)
                return "楚国 (" + FormatCellCoord(QuYuanDestination) + ")";
            if (role.IndexOf(ProtocolConstants.Roles.ZhaoJun, StringComparison.OrdinalIgnoreCase) >= 0 ||
                role.IndexOf("昭君", StringComparison.OrdinalIgnoreCase) >= 0)
                return "西汉 (" + FormatCellCoord(ZhaoJunDestination) + ")";
            return string.Empty;
        }

        // Literal four-bit wall masks (N=1, E=2, S=4, W=8). Fixed for every run.
        private const string FixedWallMaskRows =
            "D56D545455555556" +
            "C69569796C546C6A" +
            "AAC53C553843BA93" +
            "83AC7856EAAC6956" +
            "85386843A82A8552" +
            "956BAAAC3AAA956A" +
            "C6843A8053A95693" +
            "AAA853A955057956" +
            "AAAAC43C5696C553" +
            "ABA8114150692D46" +
            "843AD6856A96AC13" +
            "83E843AE9053A856" +
            "96A83C385155007A" +
            "E8116AEAC5540153" +
            "83C6BA83BC691556" +
            "9539539553955553";

        private static readonly byte[] WallMasks = ParseFixedWallMasks();

        internal static byte GetWallMask(Vector2Int cell)
        {
            if (cell.x < 0 || cell.x >= Width || cell.y < 0 || cell.y >= Height)
                return WallDirection.All;
            return WallMasks[cell.y * Width + cell.x];
        }

        private static byte[] ParseFixedWallMasks()
        {
            if (FixedWallMaskRows.Length != Width * Height)
                throw new InvalidOperationException("The fixed maze must contain exactly 256 masks.");

            var masks = new byte[FixedWallMaskRows.Length];
            for (var i = 0; i < FixedWallMaskRows.Length; i++)
                masks[i] = Convert.ToByte(FixedWallMaskRows[i].ToString(), 16);
            return masks;
        }
    }
}

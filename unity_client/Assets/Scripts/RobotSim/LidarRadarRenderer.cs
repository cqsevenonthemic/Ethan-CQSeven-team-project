using System;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Renders the real-time 2D LiDAR radar scope texture (312x190) with scan rays, hit point clouds,
    /// distance grid arcs, blind zone indicator, angle guide lines, and robot heading cursor.
    /// Manages the underlying Texture2D lifecycle cleanly.
    /// </summary>
    public sealed class LidarRadarRenderer : IDisposable
    {
        public const int RadarWidth = 312;
        public const int RadarHeight = 190;

        private Texture2D _radarTexture;
        private Color32[] _radarPixels;

        public Texture2D RadarTexture => _radarTexture;

        public void InitRadarTexture()
        {
            if (_radarTexture != null)
                Dispose();

            _radarTexture = new Texture2D(RadarWidth, RadarHeight, TextureFormat.RGBA32, false);
            _radarTexture.filterMode = FilterMode.Point;
            _radarPixels = new Color32[RadarWidth * RadarHeight];
        }

        public void DrawEmptyRadarScope()
        {
            RenderRadarScope(null, 4.5f, 0.15f);
        }

        public void RenderRadarScope(LidarRaySample[] rays, float maxRange, float minRange)
        {
            if (_radarPixels == null || _radarTexture == null)
                return;

            var bgColor = new Color32(8, 14, 22, 255);
            var gridColor = new Color32(28, 55, 82, 100);
            var blindColor = new Color32(160, 40, 40, 180);
            var beamColor = new Color32(35, 110, 150, 40);
            var robotColor = new Color32(0, 240, 255, 255);

            // Fill background
            for (var i = 0; i < _radarPixels.Length; i++)
                _radarPixels[i] = bgColor;

            var cx = RadarWidth / 2;
            var cy = 16;
            var maxRadius = 160f;
            var scale = maxRadius / Mathf.Max(0.1f, maxRange);

            // Draw semi-circular grid rings: 1m, 2m, 3m, 4.5m
            float[] ringDistances = { 1.0f, 2.0f, 3.0f, 4.5f };
            for (var d = 0; d < ringDistances.Length; d++)
            {
                var r = ringDistances[d] * scale;
                DrawSemiCircleRing(cx, cy, r, gridColor);
            }

            // Draw blind zone ring (0.15m)
            var blindR = minRange * scale;
            DrawSemiCircleRing(cx, cy, blindR, blindColor);

            // Draw radial angle guide lines: -90, -45, 0, +45, +90
            float[] guideAngles = { -90f, -45f, 0f, 45f, 90f };
            for (var a = 0; a < guideAngles.Length; a++)
            {
                var rad = guideAngles[a] * Mathf.Deg2Rad;
                var gx = Mathf.RoundToInt(cx + maxRadius * Mathf.Sin(rad));
                var gy = Mathf.RoundToInt(cy + maxRadius * Mathf.Cos(rad));
                DrawLine(cx, cy, gx, gy, gridColor);
            }

            // Draw LiDAR rays and hit point cloud
            if (rays != null)
            {
                for (var i = 0; i < rays.Length; i++)
                {
                    var ray = rays[i];
                    var rad = ray.RelativeAngleDegrees * Mathf.Deg2Rad;
                    var dist = ray.HasHit ? ray.Distance : maxRange;
                    var r = Mathf.Min(dist, maxRange) * scale;
                    var hx = Mathf.RoundToInt(cx + r * Mathf.Sin(rad));
                    var gy = Mathf.RoundToInt(cy + r * Mathf.Cos(rad));

                    // Faint ray beam
                    DrawLine(cx, cy, hx, gy, beamColor);

                    // Bright hit point cloud dot
                    if (ray.HasHit && ray.Distance >= minRange)
                    {
                        Color32 dotColor;
                        if (ray.Distance < 1.2f)
                            dotColor = new Color32(255, 85, 25, 255);   // Close: bright orange-red
                        else if (ray.Distance < 2.6f)
                            dotColor = new Color32(255, 185, 30, 255);  // Mid: golden amber
                        else
                            dotColor = new Color32(30, 235, 160, 255);  // Far: bright cyan-green

                        DrawDot(hx, gy, 2, dotColor);
                    }
                }
            }

            // Draw robot forward triangle at bottom center
            DrawTriangle(cx, cy + 10, cx - 6, cy, cx + 6, cy, robotColor);

            _radarTexture.SetPixels32(_radarPixels);
            _radarTexture.Apply(false);
        }

        private void DrawSemiCircleRing(int cx, int cy, float radius, Color32 color)
        {
            var rInt = Mathf.RoundToInt(radius);
            if (rInt <= 0) return;

            for (var deg = -90; deg <= 90; deg += 2)
            {
                var rad = deg * Mathf.Deg2Rad;
                var x = Mathf.RoundToInt(cx + radius * Mathf.Sin(rad));
                var y = Mathf.RoundToInt(cy + radius * Mathf.Cos(rad));
                SetPixelSafe(x, y, color);
            }
        }

        private void DrawLine(int x0, int y0, int x1, int y1, Color32 color)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy, e2;

            while (true)
            {
                SetPixelSafe(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private void DrawDot(int cx, int cy, int radius, Color32 color)
        {
            for (var y = -radius; y <= radius; y++)
            for (var x = -radius; x <= radius; x++)
            {
                if (x * x + y * y <= radius * radius + 1)
                    SetPixelSafe(cx + x, cy + y, color);
            }
        }

        private void DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Color32 color)
        {
            DrawLine(x0, y0, x1, y1, color);
            DrawLine(x1, y1, x2, y2, color);
            DrawLine(x2, y2, x0, y0, color);
            SetPixelSafe(x0, y0 - 1, color);
            SetPixelSafe(x0, y0 - 2, color);
        }

        private void SetPixelSafe(int x, int y, Color32 color)
        {
            if (x >= 0 && x < RadarWidth && y >= 0 && y < RadarHeight)
            {
                var idx = y * RadarWidth + x;
                if (color.a == 255)
                    _radarPixels[idx] = color;
                else
                {
                    var bg = _radarPixels[idx];
                    var a = color.a / 255f;
                    _radarPixels[idx] = new Color32(
                        (byte)(color.r * a + bg.r * (1f - a)),
                        (byte)(color.g * a + bg.g * (1f - a)),
                        (byte)(color.b * a + bg.b * (1f - a)),
                        255);
                }
            }
        }

        public void Dispose()
        {
            if (_radarTexture != null)
            {
                UnityEngine.Object.Destroy(_radarTexture);
                _radarTexture = null;
            }
            _radarPixels = null;
        }
    }
}

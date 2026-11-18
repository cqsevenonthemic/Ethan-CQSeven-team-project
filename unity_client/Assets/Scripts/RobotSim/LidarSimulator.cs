using System;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    public struct LidarRaySample
    {
        public float RelativeAngleDegrees { get; private set; }
        public bool HasHit { get; private set; }
        public float Distance { get; private set; }
        public Vector3 HitPoint { get; private set; }

        public LidarRaySample(float relativeAngleDegrees, bool hasHit, float distance, Vector3 hitPoint)
        {
            RelativeAngleDegrees = relativeAngleDegrees;
            HasHit = hasHit;
            Distance = distance;
            HitPoint = hitPoint;
        }
    }

    /// <summary>Immutable ray collection from one scan. Contains no actual maze masks.</summary>
    public sealed class LidarScan
    {
        private readonly LidarRaySample[] _rays;
        public int RayCount { get { return _rays.Length; } }
        public float FieldOfViewDegrees { get; private set; }
        public float MinimumRange { get; private set; }
        public float MaximumRange { get; private set; }

        internal LidarScan(LidarRaySample[] rays, float fieldOfViewDegrees, float minimumRange, float maximumRange)
        {
            _rays = (LidarRaySample[])rays.Clone();
            FieldOfViewDegrees = fieldOfViewDegrees;
            MinimumRange = minimumRange;
            MaximumRange = maximumRange;
        }

        public LidarRaySample GetRay(int index)
        {
            return _rays[index];
        }

        public LidarRaySample[] CopyRays()
        {
            return (LidarRaySample[])_rays.Clone();
        }
    }

    /// <summary>180-degree forward lidar using physics rays and observed-edge updates.</summary>
    [RequireComponent(typeof(RobotController))]
    public sealed class LidarSimulator : MonoBehaviour
    {
        private const float FanDegrees = 180f;
        private const int MaximumRayCount = 181;

        [SerializeField] private RobotController robot;
        [SerializeField, Range(3, MaximumRayCount)] private int rayCount = 37;
        [SerializeField, Min(0.01f)] private float minimumRange = 0.15f;
        [SerializeField, Min(0.1f)] private float maximumRange = 4.5f;
        [SerializeField, Min(0.01f)] private float scanIntervalSeconds = 0.1f;
        [SerializeField, Min(0f)] private float lidarHeight = 0.35f;
        [SerializeField, Min(0.01f)] private float hitMarkerSize = 0.045f;
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [SerializeField] private Color rayColor = new Color(0.2f, 0.88f, 1f, 0.65f);
        [SerializeField] private Color hitColor = new Color(1f, 0.38f, 0.13f);
        [SerializeField, Min(0.01f)] private float edgeHitTolerance = 0.15f;

        private LineRenderer[] _rayLines;
        private GameObject[] _hitMarkers;
        private Material _rayMaterial;
        private Material _hitMaterial;
        private LidarScan _lastScan;
        private float _scanTimer;
        private bool _subscribed;

        public event Action<LidarScan> ScanCompleted;
        public LidarScan LastScan { get { return _lastScan; } }
        public int RayCount { get { return _rayLines == null ? Mathf.Clamp(rayCount, 3, MaximumRayCount) : _rayLines.Length; } }
        public float MinimumRange { get { return minimumRange; } }
        public float MaximumRange { get { return maximumRange; } }
        public float FieldOfViewDegrees { get { return FanDegrees; } }

        private void Awake()
        {
            if (robot == null)
                robot = GetComponent<RobotController>();
            if (robot == null)
                robot = FindFirstObjectByType<RobotController>();
            CreateVisualPools();
        }

        private void OnEnable()
        {
            SubscribeToRobot();
        }

        private void Start()
        {
            if (robot == null)
                robot = FindFirstObjectByType<RobotController>();
            SubscribeToRobot();
            Scan();
        }

        private void OnDisable()
        {
            if (robot != null && _subscribed)
                robot.ActionCompleted -= OnRobotActionCompleted;
            _subscribed = false;
        }

        private void Update()
        {
            _scanTimer += Time.deltaTime;
            if (_scanTimer >= scanIntervalSeconds)
            {
                _scanTimer = 0f;
                Scan();
            }
        }

        /// <summary>Performs one scan, updates only edges crossed or directly hit, and returns observed rays.</summary>
        public LidarScan Scan()
        {
            if (robot == null)
                robot = FindFirstObjectByType<RobotController>();
            var map = robot == null ? null : robot.Map;
            if (robot == null || map == null || _rayLines == null)
                return _lastScan;

            var samples = new LidarRaySample[_rayLines.Length];
            var rayOrigin = robot.transform.position + Vector3.up * lidarHeight;
            var forward = robot.transform.forward;
            for (var i = 0; i < _rayLines.Length; i++)
            {
                var angle = Mathf.Lerp(-FanDegrees * 0.5f, FanDegrees * 0.5f,
                    i / (float)(_rayLines.Length - 1));
                var direction = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                RaycastHit hit;
                var hasPhysicsHit = Physics.Raycast(rayOrigin, direction, out hit, maximumRange,
                    obstacleLayers, QueryTriggerInteraction.Ignore);
                var usableHit = hasPhysicsHit && hit.distance >= minimumRange;
                var distance = usableHit ? Mathf.Clamp(hit.distance, minimumRange, maximumRange) : maximumRange;
                var hitPoint = usableHit ? hit.point : rayOrigin + direction * distance;

                _rayLines[i].SetPosition(0, rayOrigin);
                _rayLines[i].SetPosition(1, hitPoint);
                _rayLines[i].enabled = true;
                UpdateHitMarker(i, usableHit, hitPoint);
                samples[i] = new LidarRaySample(angle, usableHit, distance, usableHit ? hit.point : Vector3.zero);

                TraceObservedEdges(map, rayOrigin, direction, distance, usableHit, hitPoint);
            }

            _lastScan = new LidarScan(samples, FanDegrees, minimumRange, maximumRange);
            ScanCompleted?.Invoke(_lastScan);
            return _lastScan;
        }

        private void TraceObservedEdges(MazeBuilder map, Vector3 origin, Vector3 direction,
            float measuredDistance, bool hitWall, Vector3 hitPoint)
        {
            Vector2Int cell;
            if (!map.TryWorldToCell(origin, out cell))
                return;

            var dx = direction.x;
            var dz = direction.z;
            var stepX = dx > 0f ? 1 : (dx < 0f ? -1 : 0);
            var stepZ = dz > 0f ? 1 : (dz < 0f ? -1 : 0);
            var deltaX = stepX == 0 ? float.PositiveInfinity : 1f / Mathf.Abs(dx);
            var deltaZ = stepZ == 0 ? float.PositiveInfinity : 1f / Mathf.Abs(dz);
            var maxX = stepX > 0 ? ((cell.x + 1f) - origin.x) / dx :
                (stepX < 0 ? (origin.x - cell.x) / -dx : float.PositiveInfinity);
            var maxZ = stepZ > 0 ? ((cell.y + 1f) - origin.z) / dz :
                (stepZ < 0 ? (origin.z - cell.y) / -dz : float.PositiveInfinity);

            // A ray can cross at most width+height grid edges through this bounded maze.
            for (var iteration = 0; iteration < map.Width + map.Height + 2; iteration++)
            {
                var crossesX = maxX <= maxZ;
                var crossingDistance = crossesX ? maxX : maxZ;
                if (float.IsInfinity(crossingDistance) || crossingDistance > maximumRange + edgeHitTolerance)
                    return;

                var edge = crossesX ? (stepX > 0 ? WallDirection.East : WallDirection.West) :
                    (stepZ > 0 ? WallDirection.North : WallDirection.South);
                var edgePlane = crossesX ? (stepX > 0 ? cell.x + 1f : cell.x) :
                    (stepZ > 0 ? cell.y + 1f : cell.y);
                var hitOnThisPlane = hitWall && (crossesX
                    ? Mathf.Abs(hitPoint.x - edgePlane) <= edgeHitTolerance
                    : Mathf.Abs(hitPoint.z - edgePlane) <= edgeHitTolerance);

                if (hitOnThisPlane)
                {
                    map.UpdateObservedEdge(cell, edge, true);
                    return;
                }

                if (crossingDistance > measuredDistance)
                    return;

                // Only crossings reached before a farther hit are known to be open.
                map.UpdateObservedEdge(cell, edge, false);
                if (crossesX)
                {
                    cell.x += stepX;
                    maxX += deltaX;
                }
                else
                {
                    cell.y += stepZ;
                    maxZ += deltaZ;
                }
                if (!map.IsInBounds(cell))
                    return;
            }
        }

        private void CreateVisualPools()
        {
            var boundedCount = Mathf.Clamp(rayCount, 3, MaximumRayCount);
            _rayLines = new LineRenderer[boundedCount];
            _hitMarkers = new GameObject[boundedCount];
            _rayMaterial = CreateMaterial("Lidar_Rays", rayColor, true);
            _hitMaterial = CreateMaterial("Lidar_Hits", hitColor, false);

            for (var i = 0; i < boundedCount; i++)
            {
                var rayObject = new GameObject("LidarRay_" + i);
                rayObject.transform.SetParent(transform, false);
                var line = rayObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = 0.009f;
                line.endWidth = 0.004f;
                line.sharedMaterial = _rayMaterial;
                line.startColor = rayColor;
                line.endColor = rayColor;
                line.enabled = false;
                _rayLines[i] = line;

                var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                marker.name = "LidarHit_" + i;
                marker.transform.SetParent(transform, false);
                marker.transform.localScale = Vector3.one * hitMarkerSize;
                marker.GetComponent<Renderer>().sharedMaterial = _hitMaterial;
                var collider = marker.GetComponent<Collider>();
                if (collider != null)
                    Destroy(collider);
                marker.SetActive(false);
                _hitMarkers[i] = marker;
            }
        }

        private void UpdateHitMarker(int index, bool visible, Vector3 position)
        {
            var marker = _hitMarkers[index];
            if (visible)
            {
                marker.transform.position = position;
                if (!marker.activeSelf)
                    marker.SetActive(true);
            }
            else if (marker.activeSelf)
            {
                marker.SetActive(false);
            }
        }

        private void SubscribeToRobot()
        {
            if (robot != null && !_subscribed)
            {
                robot.ActionCompleted += OnRobotActionCompleted;
                _subscribed = true;
            }
        }

        private void OnRobotActionCompleted(RobotActionResult result)
        {
            Scan();
        }

        private static Material CreateMaterial(string materialName, Color color, bool transparent)
        {
            var shader = Shader.Find(transparent ? "Sprites/Default" : "Standard");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            return new Material(shader) { name = materialName, color = color };
        }
    }
}

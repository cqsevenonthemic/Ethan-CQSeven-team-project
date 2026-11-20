using System;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    public struct LidarRaySample
    {
        public float RelativeAngleDegrees { get; private set; }
        public bool HasHit { get; private set; }
        /// <summary>Zero means an occluding hit was inside the blind zone and has no reportable range.</summary>
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
        private const int MaximumRayCount = 361;

        [SerializeField] private RobotController robot;
        [SerializeField, Range(3, MaximumRayCount)] private int rayCount = 121;
        [SerializeField, Min(0.01f)] private float minimumRange = 0.15f;
        [SerializeField, Min(0.1f)] private float maximumRange = 4.5f;
        [SerializeField, Min(0.01f)] private float scanIntervalSeconds = 0.1f;
        [SerializeField, Min(0f)] private float lidarHeight = 0.35f;
        [SerializeField, Min(0.01f)] private float hitMarkerSize = 0.11f;
        [SerializeField] private LayerMask obstacleLayers = ~0;
        [SerializeField] private Color rayColor = new Color(0.2f, 0.88f, 1f, 0.65f);
        [SerializeField] private Color hitColor = new Color(1f, 0.38f, 0.13f);
        [SerializeField, Min(0.01f)] private float edgeHitTolerance = 0.08f;

        private LineRenderer[] _rayLines;
        private GameObject[] _hitMarkers;
        private ParticleSystem _pointCloudSystem;
        private ParticleSystem.Particle[] _particles;
        private Transform _visualRoot;
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

        private void OnDestroy()
        {
            if (_visualRoot != null)
                DestroyRuntimeObject(_visualRoot.gameObject);
            DestroyRuntimeMaterial(_rayMaterial);
            DestroyRuntimeMaterial(_hitMaterial);
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
            if (robot == null || map == null)
                return _lastScan;

            if (_rayLines == null || _rayLines.Length == 0 || _rayLines[0] == null)
                CreateVisualPools();

            if (_rayLines == null || _rayLines.Length == 0)
                return _lastScan;

            var samples = new LidarRaySample[_rayLines.Length];
            var rayOrigin = robot.transform.position + Vector3.up * lidarHeight;
            var forward = robot.transform.forward;
            var hitParticleIndex = 0;

            if (_pointCloudSystem != null && (_particles == null || _particles.Length != _rayLines.Length))
            {
                _particles = new ParticleSystem.Particle[_rayLines.Length];
            }

            for (var i = 0; i < _rayLines.Length; i++)
            {
                var angle = Mathf.Lerp(-FanDegrees * 0.5f, FanDegrees * 0.5f,
                    i / (float)(_rayLines.Length - 1));
                var direction = Quaternion.AngleAxis(angle, Vector3.up) * forward;
                RaycastHit hit;
                var hasPhysicsHit = Physics.Raycast(rayOrigin, direction, out hit, maximumRange,
                    obstacleLayers, QueryTriggerInteraction.Ignore);
                var hasReportableHit = hasPhysicsHit && hit.distance >= minimumRange;
                var traceDistance = hasPhysicsHit ? hit.distance : maximumRange;
                var physicalHitPoint = hasPhysicsHit ? hit.point : rayOrigin + direction * maximumRange;
                var reportDistance = hasReportableHit ? Mathf.Clamp(hit.distance, minimumRange, maximumRange) :
                    (hasPhysicsHit ? 0f : maximumRange);

                // Blind-zone hits still occlude the ray and map trace, but their point is not reported or marked.
                _rayLines[i].SetPosition(0, rayOrigin);
                _rayLines[i].SetPosition(1, physicalHitPoint);
                _rayLines[i].enabled = true;

                if (_pointCloudSystem != null)
                {
                    if (hasReportableHit && hitParticleIndex < _particles.Length)
                    {
                        var distNorm = Mathf.Clamp01((hit.distance - minimumRange) / Mathf.Max(0.1f, maximumRange - minimumRange));
                        var pColor = Color.Lerp(new Color(1f, 0.35f, 0.1f, 1f), new Color(0.15f, 0.95f, 0.85f, 1f), distNorm);
                        _particles[hitParticleIndex].position = physicalHitPoint;
                        _particles[hitParticleIndex].startColor = pColor;
                        _particles[hitParticleIndex].startSize = hitMarkerSize;
                        _particles[hitParticleIndex].remainingLifetime = 1000f;
                        hitParticleIndex++;
                    }
                }
                else
                {
                    UpdateHitMarker(i, hasReportableHit, physicalHitPoint);
                }

                samples[i] = new LidarRaySample(angle, hasReportableHit, reportDistance,
                    hasReportableHit ? physicalHitPoint : Vector3.zero);

                TraceObservedEdges(map, rayOrigin, direction, traceDistance, hasPhysicsHit, physicalHitPoint, hit.normal);
            }

            if (_pointCloudSystem != null && _particles != null)
            {
                _pointCloudSystem.SetParticles(_particles, hitParticleIndex);
            }

            _lastScan = new LidarScan(samples, FanDegrees, minimumRange, maximumRange);
            ScanCompleted?.Invoke(_lastScan);
            return _lastScan;
        }

        private void TraceObservedEdges(MazeBuilder map, Vector3 origin, Vector3 direction,
            float measuredDistance, bool hasPhysicalHit, Vector3 hitPoint, Vector3 hitNormal)
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

                const float cornerMargin = 0.08f;
                var distanceClose = Mathf.Abs(crossingDistance - measuredDistance) <= edgeHitTolerance;
                var planeDistanceClose = crossesX
                    ? Mathf.Abs(hitPoint.x - edgePlane) <= edgeHitTolerance
                    : Mathf.Abs(hitPoint.z - edgePlane) <= edgeHitTolerance;

                var withinSegment = crossesX
                    ? (hitPoint.z >= cell.y + cornerMargin && hitPoint.z <= cell.y + 1f - cornerMargin)
                    : (hitPoint.x >= cell.x + cornerMargin && hitPoint.x <= cell.x + 1f - cornerMargin);

                var normalMatchesEdge = crossesX
                    ? (Mathf.Abs(hitNormal.x) > 0.5f && hitNormal.x * dx < 0f)
                    : (Mathf.Abs(hitNormal.z) > 0.5f && hitNormal.z * dz < 0f);

                if (hasPhysicalHit && distanceClose && planeDistanceClose)
                {
                    if (normalMatchesEdge && withinSegment)
                    {
                        map.UpdateObservedEdge(cell, edge, true);
                        return;
                    }
                    else
                    {
                        // Ray hit corner/pillar where normal does not match crossed edge or is on end faces.
                        // Conservatively terminate to prevent ghost walls and false open edges.
                        return;
                    }
                }

                if (crossingDistance >= measuredDistance - 0.01f)
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
            _visualRoot = new GameObject("LidarVisuals").transform;
            _visualRoot.SetParent(transform, false);
            var boundedCount = Mathf.Clamp(rayCount, 3, MaximumRayCount);
            _rayLines = new LineRenderer[boundedCount];
            _rayMaterial = CreateMaterial("Lidar_Rays", rayColor, true);
            _hitMaterial = CreateMaterial("Lidar_Hits", hitColor, false);

            var highDensity = boundedCount > 60;
            if (highDensity)
            {
                var psObject = new GameObject("LidarPointCloud");
                psObject.transform.SetParent(_visualRoot, false);
                _pointCloudSystem = psObject.AddComponent<ParticleSystem>();
                var main = _pointCloudSystem.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.maxParticles = boundedCount;
                main.startSpeed = 0f;
                main.startLifetime = float.MaxValue;
                main.loop = false;
                main.playOnAwake = false;
                var emission = _pointCloudSystem.emission;
                emission.enabled = false;
                var shape = _pointCloudSystem.shape;
                shape.enabled = false;
                var psRenderer = psObject.GetComponent<ParticleSystemRenderer>();
                psRenderer.sharedMaterial = _hitMaterial;
                _particles = new ParticleSystem.Particle[boundedCount];
            }
            else
            {
                _hitMarkers = new GameObject[boundedCount];
            }

            var startWidth = highDensity ? 0.016f : 0.02f;
            var endWidth = highDensity ? 0.008f : 0.01f;

            for (var i = 0; i < boundedCount; i++)
            {
                var rayObject = new GameObject("LidarRay_" + i);
                rayObject.transform.SetParent(_visualRoot, false);
                var line = rayObject.AddComponent<LineRenderer>();
                line.useWorldSpace = true;
                line.positionCount = 2;
                line.startWidth = startWidth;
                line.endWidth = endWidth;
                line.sharedMaterial = _rayMaterial;
                line.startColor = rayColor;
                line.endColor = rayColor;
                line.enabled = false;
                _rayLines[i] = line;

                if (!highDensity)
                {
                    var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    marker.name = "LidarHit_" + i;
                    marker.transform.SetParent(_visualRoot, false);
                    marker.transform.localScale = Vector3.one * hitMarkerSize;
                    marker.GetComponent<Renderer>().sharedMaterial = _hitMaterial;
                    var collider = marker.GetComponent<Collider>();
                    if (collider != null)
                        Destroy(collider);
                    marker.SetActive(false);
                    _hitMarkers[i] = marker;
                }
            }
        }

        private void UpdateHitMarker(int index, bool visible, Vector3 position)
        {
            if (_hitMarkers == null || index < 0 || index >= _hitMarkers.Length)
                return;
            var marker = _hitMarkers[index];
            if (marker == null)
                return;
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

        private void DestroyRuntimeMaterial(Material material)
        {
            if (material == null)
                return;
            if (Application.isPlaying)
                Destroy(material);
            else
                DestroyImmediate(material);
        }

        private static void DestroyRuntimeObject(GameObject instance)
        {
            if (instance == null)
                return;
            if (Application.isPlaying)
                Destroy(instance);
            else
                DestroyImmediate(instance);
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

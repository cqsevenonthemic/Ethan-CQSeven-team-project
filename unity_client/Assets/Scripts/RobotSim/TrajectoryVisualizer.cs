using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Renders three-act trajectories:
    /// Act 1: Live exploration & preview paths.
    /// Act 2: RL multi-path dynamic exploration network.
    /// Act 3: Golden optimal route with dimmed exploration backdrop.
    /// </summary>
    public sealed class TrajectoryVisualizer : MonoBehaviour
    {
        [SerializeField] private PlannerTaskDriver driver;
        [SerializeField] private MazeBuilder maze;

        [Header("Act 1 Visuals")]
        [SerializeField] private Color actualPathColor = new Color(0f, 0.92f, 1f, 0.95f);
        [SerializeField] private Color previewPathColor = new Color(1f, 1f, 1f, 0.45f);
        [SerializeField] private float actualPathWidth = 0.038f;
        [SerializeField] private float previewPathWidth = 0.02f;
        [SerializeField] private float actualPathHeight = 0.04f;
        [SerializeField] private float previewPathHeight = 0.045f;

        [Header("Act 2 RL Visuals")]
        [SerializeField] private Color rlPathColor = new Color(0.2f, 0.72f, 1f, 0.45f);
        [SerializeField] private float rlPathWidth = 0.018f;
        [SerializeField] private float rlBaseHeight = 0.05f;

        [Header("Act 3 Optimal Visuals")]
        [SerializeField] private Color optimalPathColor = new Color(1f, 0.82f, 0.18f, 1f);
        [SerializeField] private float optimalPathWidth = 0.11f;
        [SerializeField] private float optimalPathHeight = 0.09f;

        private Transform _visualRoot;
        private Transform _rlContainer;
        private LineRenderer _actualPathLine;
        private LineRenderer _previewPathLine;
        private LineRenderer _optimalPathLine;
        private Material _actualPathMaterial;
        private Material _previewPathMaterial;
        private Material _rlPathMaterial;
        private Material _dimmedRlMaterial;
        private Material _optimalPathMaterial;
        private readonly List<LineRenderer> _rlLines = new List<LineRenderer>();
        private bool _subscribed;

        private void Awake()
        {
            if (driver == null)
                driver = FindFirstObjectByType<PlannerTaskDriver>();
            if (maze == null)
                maze = FindFirstObjectByType<MazeBuilder>();
            InitializeVisuals();
        }

        private void Start()
        {
            Subscribe();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void OnDestroy()
        {
            Unsubscribe();
            if (_visualRoot != null)
                DestroyRuntimeObject(_visualRoot.gameObject);
            DestroyRuntimeMaterial(_actualPathMaterial);
            DestroyRuntimeMaterial(_previewPathMaterial);
            DestroyRuntimeMaterial(_rlPathMaterial);
            DestroyRuntimeMaterial(_dimmedRlMaterial);
            DestroyRuntimeMaterial(_optimalPathMaterial);
        }

        private void Subscribe()
        {
            if (_subscribed || driver == null)
                return;

            driver.ActualPathChanged += OnActualPathChanged;
            driver.PlannerTrajectoryPreviewChanged += OnPreviewChanged;
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || driver == null)
                return;

            driver.ActualPathChanged -= OnActualPathChanged;
            driver.PlannerTrajectoryPreviewChanged -= OnPreviewChanged;
            _subscribed = false;
        }

        private void InitializeVisuals()
        {
            if (_visualRoot != null)
                return;

            _visualRoot = new GameObject("TrajectoryVisualizerRoot").transform;
            _visualRoot.SetParent(transform, false);

            _actualPathMaterial = CreateMaterial("ActualPath_Mat", actualPathColor);
            _previewPathMaterial = CreateMaterial("PreviewPath_Mat", previewPathColor);
            _rlPathMaterial = CreateMaterial("RLPath_Mat", rlPathColor);
            _dimmedRlMaterial = CreateMaterial("RLPath_Dimmed_Mat", new Color(rlPathColor.r, rlPathColor.g, rlPathColor.b, 0.20f));
            _optimalPathMaterial = CreateMaterial("OptimalPath_Mat", optimalPathColor);

            _actualPathLine = CreateLine("ActualPathLine", _visualRoot, _actualPathMaterial, actualPathWidth);
            _previewPathLine = CreateLine("PreviewPathLine", _visualRoot, _previewPathMaterial, previewPathWidth);
            _optimalPathLine = CreateLine("OptimalPathLine", _visualRoot, _optimalPathMaterial, optimalPathWidth);

            var rlObj = new GameObject("RL_ExplorationContainer");
            rlObj.transform.SetParent(_visualRoot, false);
            _rlContainer = rlObj.transform;
        }

        private void OnActualPathChanged(RobotPose[] poses)
        {
            if (_actualPathLine == null || poses == null || poses.Length == 0 || maze == null)
                return;

            _actualPathLine.positionCount = poses.Length;
            for (var i = 0; i < poses.Length; i++)
            {
                Vector3 worldCenter;
                maze.TryCellToWorld(poses[i].Cell, out worldCenter);
                worldCenter.y = actualPathHeight;
                _actualPathLine.SetPosition(i, worldCenter);
            }
            _actualPathLine.enabled = poses.Length > 1;
        }

        private void OnPreviewChanged(TrajectoryDto trajectory)
        {
            if (_previewPathLine == null)
                return;

            if (trajectory == null || trajectory.poses == null || trajectory.poses.Count <= 1 || maze == null)
            {
                _previewPathLine.enabled = false;
                return;
            }

            _previewPathLine.positionCount = trajectory.poses.Count;
            for (var i = 0; i < trajectory.poses.Count; i++)
            {
                var p = trajectory.poses[i];
                Vector3 worldCenter;
                maze.TryCellToWorld(new Vector2Int(p.x, p.z), out worldCenter);
                worldCenter.y = previewPathHeight;
                _previewPathLine.SetPosition(i, worldCenter);
            }
            _previewPathLine.enabled = true;
        }

        /// <summary>
        /// Act 2: Dynamically grows all RL exploration episodes into an intertwined mesh.
        /// </summary>
        public IEnumerator PlayRlTrajectories(PlanSummaryResponseDto summary,
            Action<int, int, int, int> onEpisodeProgress)
        {
            if (summary == null || summary.explored_trajectories == null || summary.explored_trajectories.Count == 0)
                yield break;

            if (_previewPathLine != null)
                _previewPathLine.enabled = false;

            ClearRlLines();

            var trajectories = summary.explored_trajectories;
            var episodeSteps = summary.episode_steps;
            var total = trajectories.Count;
            var bestSteps = int.MaxValue;

            for (var k = 0; k < total; k++)
            {
                var traj = trajectories[k];
                var curSteps = (episodeSteps != null && k < episodeSteps.Count) ? episodeSteps[k] : traj.Length;
                if (curSteps < bestSteps)
                    bestSteps = curSteps;

                onEpisodeProgress?.Invoke(k + 1, total, curSteps, bestSteps);

                if (traj.Length > 1 && maze != null)
                {
                    var yOffset = rlBaseHeight + (k % 25) * 0.0012f;
                    var line = CreateLine("RL_Episode_" + (k + 1), _rlContainer, _rlPathMaterial, rlPathWidth);
                    _rlLines.Add(line);

                    // Dynamic growth animation (approx 0.02s - 0.05s per batch of steps)
                    line.positionCount = traj.Length;
                    var worldPoints = new Vector3[traj.Length];
                    for (var i = 0; i < traj.Length; i++)
                    {
                        Vector3 worldCenter;
                        maze.TryCellToWorld(new Vector2Int(traj[i][0], traj[i][1]), out worldCenter);
                        worldCenter.y = yOffset;
                        worldPoints[i] = worldCenter;
                    }

                    // Animate trajectory growth smoothly
                    var stepBatch = Mathf.Max(1, traj.Length / 6);
                    for (var step = stepBatch; step <= traj.Length; step += stepBatch)
                    {
                        line.positionCount = step;
                        for (var p = 0; p < step; p++)
                            line.SetPosition(p, worldPoints[p]);
                        line.enabled = true;
                        yield return new WaitForSeconds(0.015f);
                    }

                    line.positionCount = traj.Length;
                    line.SetPositions(worldPoints);
                    line.enabled = true;
                }

                yield return new WaitForSeconds(0.02f);
            }
        }

        /// <summary>
        /// Act 3: Dims Act 2 exploration mesh into background and renders golden optimal path.
        /// </summary>
        public void ShowOptimalPath(int[][] optimalPath)
        {
            // Dim all RL exploration trajectories
            for (var i = 0; i < _rlLines.Count; i++)
            {
                if (_rlLines[i] != null)
                    _rlLines[i].sharedMaterial = _dimmedRlMaterial;
            }

            if (_previewPathLine != null)
                _previewPathLine.enabled = false;

            if (_optimalPathLine == null || optimalPath == null || optimalPath.Length <= 1 || maze == null)
                return;

            _optimalPathLine.positionCount = optimalPath.Length;
            for (var i = 0; i < optimalPath.Length; i++)
            {
                Vector3 worldCenter;
                maze.TryCellToWorld(new Vector2Int(optimalPath[i][0], optimalPath[i][1]), out worldCenter);
                worldCenter.y = optimalPathHeight;
                _optimalPathLine.SetPosition(i, worldCenter);
            }
            _optimalPathLine.enabled = true;
        }

        public void ClearRlLines()
        {
            for (var i = 0; i < _rlLines.Count; i++)
            {
                if (_rlLines[i] != null)
                    DestroyRuntimeObject(_rlLines[i].gameObject);
            }
            _rlLines.Clear();
            if (_optimalPathLine != null)
                _optimalPathLine.enabled = false;
        }

        private static LineRenderer CreateLine(string name, Transform parent, Material mat, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.sharedMaterial = mat;
            line.startWidth = width;
            line.endWidth = width;
            line.numCapVertices = 4;
            line.numCornerVertices = 4;
            line.enabled = false;
            return line;
        }

        private static Material CreateMaterial(string name, Color color)
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            if (shader == null)
                shader = Shader.Find("Standard");
            return new Material(shader) { name = name, color = color };
        }

        private static void DestroyRuntimeObject(GameObject obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }

        private static void DestroyRuntimeMaterial(Material mat)
        {
            if (mat == null) return;
            if (Application.isPlaying) Destroy(mat);
            else DestroyImmediate(mat);
        }
    }
}

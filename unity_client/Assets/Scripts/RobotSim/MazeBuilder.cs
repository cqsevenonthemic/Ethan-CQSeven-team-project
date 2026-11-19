using System.Collections.Generic;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>Builds the fixed Unity maze and owns the simulation's observed-map state.</summary>
    [DefaultExecutionOrder(-100)]
    public sealed class MazeBuilder : MonoBehaviour
    {
        [SerializeField] private float wallHeight = 1.0f;
        [SerializeField] private float wallThickness = 0.08f;
        [SerializeField] private Color wallColor = new Color(0.18f, 0.22f, 0.28f);
        [SerializeField] private Color unexploredColor = new Color(0.22f, 0.28f, 0.34f);
        [SerializeField] private Color exploredColor = new Color(0.48f, 0.62f, 0.68f);
        [SerializeField] private Color destinationColor = new Color(0.96f, 0.73f, 0.18f);
        [SerializeField] private Color secondDestinationColor = new Color(0.28f, 0.82f, 0.58f);

        private readonly byte[,] _seenWalls = new byte[MazeLayout.Height, MazeLayout.Width];
        private readonly byte[,] _knownWalls = new byte[MazeLayout.Height, MazeLayout.Width];
        private readonly bool[,] _visited = new bool[MazeLayout.Height, MazeLayout.Width];
        private Renderer[,] _cellRenderers;
        private Transform _geometryRoot;
        private Material _wallMaterial;
        private Material _unexploredMaterial;
        private Material _exploredMaterial;
        private Material _destinationMaterial;
        private Material _secondDestinationMaterial;
        private bool _built;

        public int Width { get { return MazeLayout.Width; } }
        public int Height { get { return MazeLayout.Height; } }
        public float CellSize { get { return MazeLayout.CellSize; } }
        public Vector2Int StartCell { get { return MazeLayout.StartCell; } }
        public Vector2Int FirstDestination { get { return MazeLayout.GetDestination(0); } }
        public Vector2Int SecondDestination { get { return MazeLayout.GetDestination(1); } }

        private void Awake()
        {
            MarkVisited(MazeLayout.StartCell);
            BuildMap();
        }

        /// <summary>Cell.x is world +x; Cell.y is world +z. The returned center has y=0.</summary>
        public bool TryCellToWorld(Vector2Int cell, out Vector3 worldPosition)
        {
            if (!IsInBounds(cell))
            {
                worldPosition = Vector3.zero;
                return false;
            }

            worldPosition = new Vector3((cell.x + 0.5f) * CellSize, 0f, (cell.y + 0.5f) * CellSize);
            return true;
        }

        /// <summary>Converts a world x/z position to the containing cell; points outside the map fail.</summary>
        public bool TryWorldToCell(Vector3 worldPosition, out Vector2Int cell)
        {
            var x = Mathf.FloorToInt(worldPosition.x / CellSize);
            var z = Mathf.FloorToInt(worldPosition.z / CellSize);
            cell = new Vector2Int(x, z);
            return IsInBounds(cell);
        }

        public bool IsInBounds(Vector2Int cell)
        {
            return cell.x >= 0 && cell.x < Width && cell.y >= 0 && cell.y < Height;
        }

        /// <summary>
        /// Unity simulation-only truth query. Keep this data inside Unity; never place it in network DTOs.
        /// </summary>
        private bool TryGetActualWalls(Vector2Int cell, out byte wallMask)
        {
            if (!IsInBounds(cell))
            {
                wallMask = 0;
                return false;
            }

            wallMask = MazeLayout.GetWallMask(cell);
            return true;
        }

        internal bool IsPassageOpen(Vector2Int cell, RobotHeading heading)
        {
            var wall = WallDirection.FromHeading(heading);
            int dx;
            int dz;
            if (!IsInBounds(cell) || wall == 0 || !WallDirection.TryGetOffset(wall, out dx, out dz))
                return false;

            var neighbor = new Vector2Int(cell.x + dx, cell.y + dz);
            byte currentMask;
            byte neighborMask;
            if (!IsInBounds(neighbor) || !TryGetActualWalls(cell, out currentMask) ||
                !TryGetActualWalls(neighbor, out neighborMask))
                return false;

            return (currentMask & wall) == 0 && (neighborMask & WallDirection.Opposite(wall)) == 0;
        }

        /// <summary>Marks both sides of a crossed edge as observed, and records whether it has a wall.</summary>
        public bool UpdateObservedEdge(Vector2Int cell, byte direction, bool hasWall)
        {
            var opposite = WallDirection.Opposite(direction);
            int dx;
            int dz;
            if (!IsInBounds(cell) || opposite == 0 || !WallDirection.TryGetOffset(direction, out dx, out dz))
                return false;

            ApplyObservedMask(_seenWalls, cell, direction, true);
            ApplyObservedMask(_knownWalls, cell, direction, hasWall);

            var neighbor = new Vector2Int(cell.x + dx, cell.y + dz);
            if (IsInBounds(neighbor))
            {
                ApplyObservedMask(_seenWalls, neighbor, opposite, true);
                ApplyObservedMask(_knownWalls, neighbor, opposite, hasWall);
            }
            return true;
        }

        public bool TryGetObservedWalls(Vector2Int cell, out byte seenWalls, out byte knownWalls)
        {
            if (!IsInBounds(cell))
            {
                seenWalls = 0;
                knownWalls = 0;
                return false;
            }

            seenWalls = _seenWalls[cell.y, cell.x];
            knownWalls = _knownWalls[cell.y, cell.x];
            return true;
        }

        public ObservedMaskSnapshot GetObservedMaskSnapshot()
        {
            var seen = new byte[Width * Height];
            var known = new byte[Width * Height];
            for (var z = 0; z < Height; z++)
            for (var x = 0; x < Width; x++)
            {
                var index = z * Width + x;
                seen[index] = _seenWalls[z, x];
                known[index] = _knownWalls[z, x];
            }
            return new ObservedMaskSnapshot(Width, Height, seen, known);
        }

        public bool MarkVisited(Vector2Int cell)
        {
            if (!IsInBounds(cell))
                return false;

            _visited[cell.y, cell.x] = true;
            if (_cellRenderers != null && _cellRenderers[cell.y, cell.x] != null)
                _cellRenderers[cell.y, cell.x].sharedMaterial = _exploredMaterial;
            return true;
        }

        public bool IsVisited(Vector2Int cell)
        {
            return IsInBounds(cell) && _visited[cell.y, cell.x];
        }

        /// <summary>Returns cells the robot actually entered, sorted by x then z for stable API snapshots.</summary>
        public Vector2Int[] CopyVisitedCells()
        {
            var cells = new List<Vector2Int>(Width * Height);
            for (var x = 0; x < Width; x++)
            for (var z = 0; z < Height; z++)
                if (_visited[z, x])
                    cells.Add(new Vector2Int(x, z));
            return cells.ToArray();
        }

        private static void ApplyObservedMask(byte[,] masks, Vector2Int cell, byte direction, bool set)
        {
            if (set)
                masks[cell.y, cell.x] |= direction;
            else
                masks[cell.y, cell.x] &= (byte)~direction;
        }

        private void OnDestroy()
        {
            if (_geometryRoot != null)
                DestroyRuntimeObject(_geometryRoot.gameObject);
            DestroyRuntimeMaterial(_wallMaterial);
            DestroyRuntimeMaterial(_unexploredMaterial);
            DestroyRuntimeMaterial(_exploredMaterial);
            DestroyRuntimeMaterial(_destinationMaterial);
            DestroyRuntimeMaterial(_secondDestinationMaterial);
        }

        private void BuildMap()
        {
            if (_built)
                return;
            if (!ValidateFixedLayout())
            {
                Debug.LogError("The fixed maze masks failed boundary, symmetry, or connectivity validation.", this);
                enabled = false;
                return;
            }

            CreateMaterials();
            _geometryRoot = new GameObject("GeneratedMaze").transform;
            _geometryRoot.SetParent(transform, false);
            _cellRenderers = new Renderer[Height, Width];

            CreatePrimitive("Floor", PrimitiveType.Cube,
                new Vector3(Width * CellSize * 0.5f, -0.05f, Height * CellSize * 0.5f),
                new Vector3(Width * CellSize, 0.1f, Height * CellSize), _unexploredMaterial, true);

            for (var z = 0; z < Height; z++)
            for (var x = 0; x < Width; x++)
            {
                var cell = new Vector2Int(x, z);
                var tile = CreatePrimitive("Cell_" + x + "_" + z, PrimitiveType.Cube,
                    new Vector3((x + 0.5f) * CellSize, 0.008f, (z + 0.5f) * CellSize),
                    new Vector3(CellSize * 0.94f, 0.016f, CellSize * 0.94f),
                    _visited[z, x] ? _exploredMaterial : _unexploredMaterial, false);
                _cellRenderers[z, x] = tile.GetComponent<Renderer>();

                byte walls;
                TryGetActualWalls(cell, out walls);
                if ((walls & WallDirection.North) != 0)
                    CreateWall("Wall_N_" + x + "_" + z,
                        new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, (z + 1f) * CellSize),
                        new Vector3(CellSize, wallHeight, wallThickness));
                if ((walls & WallDirection.East) != 0)
                    CreateWall("Wall_E_" + x + "_" + z,
                        new Vector3((x + 1f) * CellSize, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                        new Vector3(wallThickness, wallHeight, CellSize));
                if (z == 0 && (walls & WallDirection.South) != 0)
                    CreateWall("Wall_S_" + x, new Vector3((x + 0.5f) * CellSize, wallHeight * 0.5f, 0f),
                        new Vector3(CellSize, wallHeight, wallThickness));
                if (x == 0 && (walls & WallDirection.West) != 0)
                    CreateWall("Wall_W_" + z, new Vector3(0f, wallHeight * 0.5f, (z + 0.5f) * CellSize),
                        new Vector3(wallThickness, wallHeight, CellSize));
            }

            for (var i = 0; i < MazeLayout.DestinationCount; i++)
            {
                var cell = MazeLayout.GetDestination(i);
                var colorMaterial = i == 0 ? _destinationMaterial : _secondDestinationMaterial;
                CreatePrimitive("Destination_" + (i + 1), PrimitiveType.Cube,
                    new Vector3((cell.x + 0.5f) * CellSize, 0.022f, (cell.y + 0.5f) * CellSize),
                    new Vector3(CellSize * 0.68f, 0.025f, CellSize * 0.68f), colorMaterial, false);
            }
            _built = true;
        }

        private GameObject CreateWall(string objectName, Vector3 position, Vector3 scale)
        {
            return CreatePrimitive(objectName, PrimitiveType.Cube, position, scale, _wallMaterial, true);
        }

        private GameObject CreatePrimitive(string objectName, PrimitiveType type, Vector3 position,
            Vector3 scale, Material material, bool keepCollider)
        {
            var instance = GameObject.CreatePrimitive(type);
            instance.name = objectName;
            instance.transform.SetParent(_geometryRoot, false);
            instance.transform.position = position;
            instance.transform.localScale = scale;
            var renderer = instance.GetComponent<Renderer>();
            renderer.sharedMaterial = material;
            if (!keepCollider)
            {
                var collider = instance.GetComponent<Collider>();
                if (collider != null)
                    Destroy(collider);
            }
            return instance;
        }

        private void CreateMaterials()
        {
            _wallMaterial = NewMaterial("Maze_Wall", wallColor);
            _unexploredMaterial = NewMaterial("Maze_Unexplored", unexploredColor);
            _exploredMaterial = NewMaterial("Maze_Explored", exploredColor);
            _destinationMaterial = NewMaterial("Maze_Destination_A", destinationColor);
            _secondDestinationMaterial = NewMaterial("Maze_Destination_B", secondDestinationColor);
        }

        private static Material NewMaterial(string materialName, Color color)
        {
            var shader = Shader.Find("Standard");
            if (shader == null)
                shader = Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = materialName, color = color };
            return material;
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

        private bool ValidateFixedLayout()
        {
            var reachable = new bool[Height, Width];
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(MazeLayout.StartCell);
            reachable[0, 0] = true;
            var count = 0;
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                count++;
                byte mask;
                TryGetActualWalls(cell, out mask);
                for (var bit = WallDirection.North; bit <= WallDirection.West; bit <<= 1)
                {
                    int dx;
                    int dz;
                    WallDirection.TryGetOffset(bit, out dx, out dz);
                    var neighbor = new Vector2Int(cell.x + dx, cell.y + dz);
                    if (!IsInBounds(neighbor))
                    {
                        if ((mask & bit) == 0)
                            return false;
                        continue;
                    }

                    byte neighborMask;
                    TryGetActualWalls(neighbor, out neighborMask);
                    var neighborWall = WallDirection.Opposite(bit);
                    if (((mask & bit) != 0) != ((neighborMask & neighborWall) != 0))
                        return false;
                    if ((mask & bit) == 0 && !reachable[neighbor.y, neighbor.x])
                    {
                        reachable[neighbor.y, neighbor.x] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }
            return count == Width * Height;
        }
    }
}

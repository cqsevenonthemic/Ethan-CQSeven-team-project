using System;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    public enum CameraViewMode
    {
        TopDown,
        ThirdPersonFollow,
        FirstPerson
    }

    /// <summary>
    /// Smooth multi-view camera director and speed multiplier controller:
    /// - Key V or UI button: Toggle TopDown (isometric/overview) -> ThirdPersonFollow -> FirstPerson.
    /// - Keys 1/2/3 or UI buttons: Switch speed 1x (0.2s), 2x (0.1s), 20x (0.01s).
    /// - Act integration: Automatically ramps to 20x in Act 2, and steps down to 2x/1x in Act 3 replay.
    /// </summary>
    public sealed class CameraAndSpeedController : MonoBehaviour
    {
        [SerializeField] private Camera targetCamera;
        [SerializeField] private RobotController robot;
        [SerializeField] private DashboardUI dashboard;
        [SerializeField] private PlannerTaskDriver driver;

        [Header("View Settings")]
        [SerializeField] private CameraViewMode currentView = CameraViewMode.TopDown;
        [SerializeField, Min(1f)] private float followSmoothSpeed = 10f;
        [SerializeField] private Vector3 topDownPosition = new Vector3(8f, 25f, -8f);
        [SerializeField] private Vector3 topDownEuler = new Vector3(57.4f, 0f, 0f);

        [Header("Third Person Offsets")]
        [SerializeField] private float thirdPersonDistance = 2.4f;
        [SerializeField] private float thirdPersonHeight = 1.6f;
        [SerializeField] private float thirdPersonLookHeight = 0.35f;

        [Header("First Person Offsets")]
        [SerializeField] private float firstPersonHeight = 0.42f;
        [SerializeField] private float firstPersonForward = 0.16f;

        private Quaternion _topDownRotation;
        private bool _subscribed;

        public CameraViewMode CurrentView { get { return currentView; } }

        private void Awake()
        {
            FindDependencies();
            _topDownRotation = Quaternion.Euler(topDownEuler);
        }

        private void Start()
        {
            FindDependencies();
            Subscribe();
            UpdateDashboardLabels();
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
        }

        private void FindDependencies()
        {
            if (targetCamera == null)
                targetCamera = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (robot == null)
                robot = FindFirstObjectByType<RobotController>();
            if (dashboard == null)
                dashboard = FindFirstObjectByType<DashboardUI>();
            if (driver == null)
                driver = FindFirstObjectByType<PlannerTaskDriver>();

            if (targetCamera != null && currentView == CameraViewMode.TopDown)
            {
                topDownPosition = targetCamera.transform.position;
                topDownEuler = targetCamera.transform.eulerAngles;
                _topDownRotation = targetCamera.transform.rotation;
            }
        }

        private void Subscribe()
        {
            if (_subscribed) return;

            if (driver != null)
                driver.ActChanged += OnActChanged;

            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;

            if (driver != null)
                driver.ActChanged -= OnActChanged;

            _subscribed = false;
        }

        private void Update()
        {
            // Keyboard view toggle: V
            if (Input.GetKeyDown(KeyCode.V))
            {
                CycleView();
            }

            // Keyboard speed switches: 1, 2, 3
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            {
                SetSpeed(1f);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            {
                SetSpeed(2f);
            }
            else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            {
                SetSpeed(20f);
            }
        }

        private void LateUpdate()
        {
            if (targetCamera == null)
                return;

            Vector3 targetPosition;
            Quaternion targetRotation;
            CalculateViewTransform(out targetPosition, out targetRotation);

            var t = Time.deltaTime * followSmoothSpeed;
            targetCamera.transform.position = Vector3.Lerp(targetCamera.transform.position, targetPosition, t);
            targetCamera.transform.rotation = Quaternion.Slerp(targetCamera.transform.rotation, targetRotation, t);
        }

        private void CalculateViewTransform(out Vector3 position, out Quaternion rotation)
        {
            if (robot == null || currentView == CameraViewMode.TopDown)
            {
                position = topDownPosition;
                rotation = _topDownRotation;
                return;
            }

            var robotPos = robot.transform.position;
            var robotForward = robot.transform.forward;

            switch (currentView)
            {
                case CameraViewMode.ThirdPersonFollow:
                    position = robotPos - robotForward * thirdPersonDistance + Vector3.up * thirdPersonHeight;
                    var lookTarget = robotPos + Vector3.up * thirdPersonLookHeight;
                    rotation = Quaternion.LookRotation((lookTarget - position).normalized);
                    break;

                case CameraViewMode.FirstPerson:
                    position = robotPos + Vector3.up * firstPersonHeight + robotForward * firstPersonForward;
                    rotation = robot.transform.rotation;
                    break;

                default:
                    position = topDownPosition;
                    rotation = _topDownRotation;
                    break;
            }
        }

        public void CycleView()
        {
            switch (currentView)
            {
                case CameraViewMode.TopDown:
                    currentView = CameraViewMode.ThirdPersonFollow;
                    break;
                case CameraViewMode.ThirdPersonFollow:
                    currentView = CameraViewMode.FirstPerson;
                    break;
                case CameraViewMode.FirstPerson:
                    currentView = CameraViewMode.TopDown;
                    break;
            }

            UpdateDashboardLabels();
        }

        public void SetSpeed(float multiplier)
        {
            if (robot != null)
                robot.TrySetSpeedMultiplier(multiplier);

            if (dashboard != null)
                dashboard.HighlightSpeedButton(multiplier);
        }

        private void OnActChanged(DemoAct act)
        {
            switch (act)
            {
                case DemoAct.Act2_RlExploration:
                    SetSpeed(20f);
                    break;
                case DemoAct.Act3_OptimalReplay:
                    SetSpeed(2f);
                    break;
            }
        }

        private void UpdateDashboardLabels()
        {
            if (dashboard == null) return;
            string viewLabel;
            switch (currentView)
            {
                case CameraViewMode.TopDown: viewLabel = "俯视"; break;
                case CameraViewMode.ThirdPersonFollow: viewLabel = "跟随"; break;
                case CameraViewMode.FirstPerson: viewLabel = "第一人称"; break;
                default: viewLabel = "俯视"; break;
            }
            dashboard.UpdateViewButtonLabel(viewLabel);
        }
    }
}

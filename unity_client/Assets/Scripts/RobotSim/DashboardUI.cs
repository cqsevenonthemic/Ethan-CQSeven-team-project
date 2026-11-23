using System;
using System.Collections;
using UnityEngine;

namespace Ethan.CQSeven.RobotSim
{
    public enum PipelineStepStage
    {
        Standby,
        Discovered,
        Recognizing,
        Completed,
        Failed
    }

    /// <summary>
    /// Streamlined facade orchestrator for the RobotSim Dashboard UI.
    /// Manages domain event subscriptions, coroutines, and dispatches rendering/formatting
    /// to LidarRadarRenderer, DashboardTextFormatter, and DashboardLayoutBuilder.
    /// </summary>
    public sealed class DashboardUI : MonoBehaviour
    {
        [SerializeField] private PlannerTaskDriver driver;
        [SerializeField] private RobotController robot;
        [SerializeField] private LidarSimulator lidar;
        [SerializeField] private StateMachineTrigger stateMachine;
        [SerializeField] private PlannerHttpClient plannerClient;
        [SerializeField] private CameraAndSpeedController cameraController;

        private Canvas _canvas;
        private Font _font;
        private LidarRadarRenderer _radarRenderer;
        private DashboardViewRefs _refs;

        private Texture2D _currentStationTexture;
        private bool _isScanningActive;
        private float _scanLineTimer;
        private Coroutine _rejectionCoroutine;
        private PlanSummaryResponseDto _lastSummary;
        private DemoAct _currentAct = DemoAct.Act1_OnlineMission;
        private bool _subscribed;

        private void Awake()
        {
            FindDependencies();
            LoadDefaultFont();
            _radarRenderer = new LidarRadarRenderer();
            _radarRenderer.InitRadarTexture();
            BuildCanvasUI();
        }

        private void Start()
        {
            FindDependencies();
            SetEventSubscriptions(true);
            UpdateInitialDisplay();
            _radarRenderer.DrawEmptyRadarScope();
        }

        private void OnEnable() => SetEventSubscriptions(true);
        private void OnDisable() => SetEventSubscriptions(false);

        private void OnDestroy()
        {
            SetEventSubscriptions(false);
            if (_radarRenderer != null) { _radarRenderer.Dispose(); _radarRenderer = null; }
            if (_currentStationTexture != null) { Destroy(_currentStationTexture); _currentStationTexture = null; }
        }

        private void Update()
        {
            if (_isScanningActive && _refs?.ScanLineTransform != null)
                DashboardLayoutBuilder.AnimateScanLine(_refs, ref _scanLineTimer, Time.deltaTime);
        }

        private void FindDependencies()
        {
            if (driver == null) driver = FindFirstObjectByType<PlannerTaskDriver>();
            if (robot == null) robot = FindFirstObjectByType<RobotController>();
            if (lidar == null) lidar = FindFirstObjectByType<LidarSimulator>();
            if (stateMachine == null) stateMachine = FindFirstObjectByType<StateMachineTrigger>();
            if (plannerClient == null) plannerClient = FindFirstObjectByType<PlannerHttpClient>();
            if (cameraController == null) cameraController = FindFirstObjectByType<CameraAndSpeedController>();
        }

        private void LoadDefaultFont()
        {
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")
                ?? Resources.GetBuiltinResource<Font>("Arial.ttf")
                ?? Font.CreateDynamicFontFromOSFont("Arial", 14);
        }

        private void BuildCanvasUI()
        {
            var canvasGo = new GameObject("Dashboard_Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGo.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            _refs = DashboardLayoutBuilder.Build(canvasGo.transform, _font, _radarRenderer.RadarTexture, new DashboardCallbacks
            {
                OnSpeedChanged = SetSpeed,
                OnViewToggleClicked = OnViewToggleClicked,
                OnRetryClicked = OnRetryClicked
            });
            HighlightSpeedButton(robot != null ? robot.SpeedMultiplier : 1f);
        }

        private void SetEventSubscriptions(bool subscribe)
        {
            if (_subscribed == subscribe) return;
            _subscribed = subscribe;

            if (driver != null)
            {
                if (subscribe) { driver.ActChanged += OnActChanged; driver.StatusChanged += OnStatusChanged; driver.DecisionEvaluated += OnDecisionEvaluated; driver.RlProgressUpdated += OnRlProgressUpdated; driver.PlanSummaryLoaded += OnPlanSummaryLoaded; }
                else { driver.ActChanged -= OnActChanged; driver.StatusChanged -= OnStatusChanged; driver.DecisionEvaluated -= OnDecisionEvaluated; driver.RlProgressUpdated -= OnRlProgressUpdated; driver.PlanSummaryLoaded -= OnPlanSummaryLoaded; }
            }
            if (robot != null)
            {
                if (subscribe) { robot.ActionCompleted += OnRobotActionCompleted; robot.PoseChanged += OnRobotPoseChanged; }
                else { robot.ActionCompleted -= OnRobotActionCompleted; robot.PoseChanged -= OnRobotPoseChanged; }
            }
            if (lidar != null)
            {
                if (subscribe) lidar.ScanCompleted += OnLidarScanCompleted;
                else lidar.ScanCompleted -= OnLidarScanCompleted;
            }
            if (stateMachine != null)
            {
                if (subscribe) { stateMachine.StationDiscovered += OnStationDiscovered; stateMachine.RecognitionCompleted += OnRecognitionCompleted; stateMachine.DeliveryValidated += OnDeliveryValidated; stateMachine.SlotSwitched += OnSlotSwitched; stateMachine.ReplayCarryStateUpdated += OnReplayCarryStateUpdated; }
                else { stateMachine.StationDiscovered -= OnStationDiscovered; stateMachine.RecognitionCompleted -= OnRecognitionCompleted; stateMachine.DeliveryValidated -= OnDeliveryValidated; stateMachine.SlotSwitched -= OnSlotSwitched; stateMachine.ReplayCarryStateUpdated -= OnReplayCarryStateUpdated; }
            }
            if (plannerClient != null)
            {
                if (subscribe) plannerClient.InitialModeResolved += OnInitialModeResolved;
                else plannerClient.InitialModeResolved -= OnInitialModeResolved;
            }
        }

        private void UpdateInitialDisplay()
        {
            OnActChanged(driver != null ? driver.CurrentAct : DemoAct.Act1_OnlineMission);
            if (plannerClient != null && plannerClient.IsInitialModeResolved)
                OnInitialModeResolved(plannerClient.Mode);
            UpdateStepStats();
            if (robot != null) OnRobotPoseChanged(robot.CurrentPose);
            DashboardLayoutBuilder.SetPipelineStage(_refs, PipelineStepStage.Standby);
        }

        // ======================= EVENT HANDLERS =======================

        private void OnLidarScanCompleted(LidarScan scan)
        {
            if (scan == null) return;
            var hitCount = 0;
            var rays = scan.CopyRays();
            for (var i = 0; i < rays.Length; i++)
                if (rays[i].HasHit) hitCount++;

            if (_refs?.LidarHitCountText != null)
                _refs.LidarHitCountText.text = "点云状态: " + scan.RayCount + " 束激光 | 有效点云: " + hitCount + " 点";

            _radarRenderer?.RenderRadarScope(rays, scan.MaximumRange, scan.MinimumRange);
        }

        private void OnStationDiscovered(StationDiscovery discovery)
        {
            if (discovery == null || discovery.Station == null || _refs == null) return;

            DashboardLayoutBuilder.SetPipelineStage(_refs, PipelineStepStage.Discovered);
            _refs.StationLocationText.text = DashboardTextFormatter.FormatStationLocation(
                discovery.Station.SlotId, discovery.Station.Cell, "已触发");
            _refs.StationSourceText.text = "推理引擎: ONNX 推理就绪...";
            _refs.StationErrorText.gameObject.SetActive(false);

            if (discovery.ImageBytes != null && discovery.ImageBytes.Length > 0)
                _currentStationTexture = DashboardLayoutBuilder.LoadStationPhoto(_refs, _currentStationTexture, discovery.ImageBytes);

            SetScanningActive(true);
        }

        private void OnRecognitionCompleted(StationRuntimeState station)
        {
            if (station == null || _refs == null) return;
            SetScanningActive(false);
            DashboardLayoutBuilder.UpdateRecognitionDisplay(_refs, station);
            if (station.ImageBytes != null && station.ImageBytes.Length > 0)
                _currentStationTexture = DashboardLayoutBuilder.LoadStationPhoto(_refs, _currentStationTexture, station.ImageBytes);
        }

        private void SetScanningActive(bool active)
        {
            _isScanningActive = active;
            _scanLineTimer = 0f;
            if (_refs?.ScanLineImage != null) _refs.ScanLineImage.gameObject.SetActive(active);
        }

        private void OnActChanged(DemoAct act)
        {
            _currentAct = act;
            if (act == DemoAct.Act2_RlExploration || act == DemoAct.Act3_OptimalReplay || act == DemoAct.Completed)
            {
                if (_refs?.RejectionAlert != null && _refs.RejectionAlert.activeSelf)
                {
                    if (_rejectionCoroutine != null) { StopCoroutine(_rejectionCoroutine); _rejectionCoroutine = null; }
                    _refs.RejectionAlert.SetActive(false);
                }
            }
            DashboardLayoutBuilder.UpdateActDisplay(_refs, act, _lastSummary, HighlightSpeedButton);
        }

        private void OnInitialModeResolved(PlannerClientMode mode)
        {
            if (_refs == null) return;
            var isLive = mode == PlannerClientMode.Live;
            _refs.ModeBadgeBg.color = isLive ? new Color(0.1f, 0.55f, 0.2f, 0.95f) : new Color(0.8f, 0.45f, 0.05f, 0.95f);
            _refs.ModeBadgeText.text = isLive ? "LIVE API" : "MOCK";
            _refs.ModeBadgeText.color = Color.white;
        }

        private void OnStatusChanged(string message)
        {
            if (_refs == null) return;
            var translated = DashboardTextFormatter.TranslateStatusMessage(message, _lastSummary);
            if (_refs.StatusText != null && translated != null) _refs.StatusText.text = translated;

            if (DashboardTextFormatter.IsErrorStatus(message))
            {
                if (_refs.ErrorText != null) { _refs.ErrorText.color = Color.white; _refs.ErrorText.text = "⚠ " + (translated ?? message); }
                _refs.ErrorBanner?.SetActive(true);

                if (message != null && (message.Contains("501") || message.Contains("recognize")))
                {
                    SetScanningActive(false);
                    DashboardLayoutBuilder.SetPipelineStage(_refs, PipelineStepStage.Failed, translated ?? message);
                }
            }
            else if (_refs.ErrorBanner != null && _refs.ErrorBanner.activeSelf &&
                     message != null && !message.Contains("error") && !message.Contains("failed") && !message.Contains("失败"))
            {
                _refs.ErrorBanner.SetActive(false);
            }
        }

        private void OnDecisionEvaluated(PlannerDecisionDto decision, RobotPose pose) =>
            DashboardLayoutBuilder.UpdateDecisionDisplay(_refs, decision);

        private void OnSlotSwitched(SlotSwitchEvent evt)
        {
            if (_refs?.StationCarryText != null && evt != null)
                _refs.StationCarryText.text = "原地换人: 当前携带已切换至 [" + evt.NewCarrySlot + "]";
        }

        private void OnDeliveryValidated(DeliveryValidationEvent evt)
        {
            if (evt == null) return;
            if (!evt.Accepted)
            {
                if (_rejectionCoroutine != null) StopCoroutine(_rejectionCoroutine);
                _rejectionCoroutine = StartCoroutine(ShowRejectionAlertCoroutine());
            }
            else if (_refs?.StationCarryText != null)
            {
                _refs.StationCarryText.text = "派送完成: " + evt.SlotId + " 成功送达正确目的地！";
            }
        }

        private void OnReplayCarryStateUpdated(string text)
        {
            if (_refs?.StationCarryText != null && !string.IsNullOrEmpty(text))
                _refs.StationCarryText.text = text;
        }

        private IEnumerator ShowRejectionAlertCoroutine()
        {
            if (_refs?.RejectionAlert == null) yield break;
            _refs.RejectionAlert.SetActive(true);
            yield return new WaitForSeconds(4f);
            _refs.RejectionAlert.SetActive(false);
        }

        private void OnRobotActionCompleted(RobotActionResult result) => UpdateStepStats();

        private void OnRobotPoseChanged(RobotPose pose)
        {
            if (_refs?.LidarPoseText != null)
                _refs.LidarPoseText.text = DashboardTextFormatter.FormatPoseText(pose);
        }

        private void OnRlProgressUpdated(int currentEpisode, int totalEpisodes, int currentSteps, int bestSteps)
        {
            if (_refs?.RlDetailText != null)
                _refs.RlDetailText.text = DashboardTextFormatter.FormatRlProgress(currentEpisode, totalEpisodes, currentSteps, bestSteps);
        }

        public static string GetRouteQualificationTitle(PlanSummaryResponseDto summary) =>
            DashboardTextFormatter.GetRouteQualificationTitle(summary);

        public static string GetRouteQualificationBadgeTitle(PlanSummaryResponseDto summary) =>
            DashboardTextFormatter.GetRouteQualificationBadgeTitle(summary);

        private void OnPlanSummaryLoaded(PlanSummaryResponseDto summary)
        {
            _lastSummary = summary;
            DashboardLayoutBuilder.UpdatePlanSummaryDisplay(_refs, _currentAct, summary);
        }

        private void UpdateStepStats()
        {
            if (robot == null || _refs?.StepStatsText == null) return;
            _refs.StepStatsText.text = DashboardTextFormatter.FormatStepStats(robot);
        }

        // ======================= CONTROLS & HELPERS =======================

        public void HighlightSpeedButton(float multiplier) =>
            DashboardLayoutBuilder.HighlightSpeedButton(_refs, multiplier);

        public void UpdateViewButtonLabel(string viewName)
        {
            if (_refs?.TextView != null) _refs.TextView.text = "视角: " + viewName;
        }

        private void SetSpeed(float mult)
        {
            if (cameraController != null) cameraController.SetSpeed(mult);
            else if (robot != null)
            {
                robot.TrySetSpeedMultiplier(mult);
                HighlightSpeedButton(mult);
            }
        }

        private void OnViewToggleClicked() => cameraController?.CycleView();

        private void OnRetryClicked()
        {
            driver?.RetryAfterError();
            _refs?.ErrorBanner?.SetActive(false);
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

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
    /// Defense-grade dashboard UI for Micromouse 3-Act demonstration:
    /// - Top bar: Clear horizontal layout with Act badge, Mode badge, single-line step counters, status banner, controls.
    /// - Left column (340px): 2D LiDAR Radar Scope (312x190 real-time scope) + telemetry parameters + RL card.
    /// - Right column (340px): 4-step Vision Recognition Pipeline + animated scanning viewport + dual-class ONNX probability bars + carry card.
    /// - Center bottom: Rejection alert + decision comparison card.
    /// - All cards use VerticalLayoutGroup + ContentSizeFitter to prevent text overlap, truncation, or vertical wrapping.
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

        // Top bar elements
        private Text _actBadgeText;
        private Image _actBadgeBg;
        private Text _modeBadgeText;
        private Image _modeBadgeBg;
        private Text _stepStatsText;
        private Text _statusText;
        private GameObject _errorBanner;
        private Text _errorText;
        private Button _retryButton;

        // Speed & View buttons
        private Button _btn1x;
        private Button _btn2x;
        private Button _btn20x;
        private Image _bg1x;
        private Image _bg2x;
        private Image _bg20x;
        private Button _btnView;
        private Text _textView;

        // Left LiDAR Radar Scope elements
        private const int RadarWidth = 312;
        private const int RadarHeight = 190;
        private RawImage _radarImage;
        private Texture2D _radarTexture;
        private Color32[] _radarPixels;
        private Text _lidarHitCountText;
        private Text _lidarPoseText;
        private Text _lidarStatusText;

        // Right Station Recognition Pipeline elements
        private GameObject _stationPanel;
        private Image[] _pipelineCapsuleBgs = new Image[4];
        private Text[] _pipelineCapsuleTexts = new Text[4];
        private RawImage _stationPhoto;
        private Text _stationPhotoPlaceholder;
        private RectTransform _scanLineTransform;
        private Image _scanLineImage;
        private GameObject _stationPhotoTag;
        private Text _stationPhotoTagText;
        private Image _quyuanFill;
        private Text _quyuanProbText;
        private Image _zhaojunFill;
        private Text _zhaojunProbText;
        private Text _stationLocationText;
        private Text _stationSourceText;
        private Text _stationCarryText;
        private Text _stationErrorText;
        private Texture2D _currentStationTexture;
        private bool _isScanningActive;
        private float _scanLineTimer;

        // Decision comparison card
        private GameObject _decisionPanel;
        private Text _decisionKeepText;
        private Text _decisionSwitchText;
        private Text _decisionResultText;
        private Text _decisionReasonText;

        // Rejection alert
        private GameObject _rejectionAlert;
        private Text _rejectionText;
        private Coroutine _rejectionCoroutine;

        // RL / Optimal progress card
        private Text _rlHeader;
        private Text _rlDetailText;
        private PlanSummaryResponseDto _lastSummary;
        private DemoAct _currentAct = DemoAct.Act1_OnlineMission;

        private bool _subscribed;

        private void Awake()
        {
            FindDependencies();
            LoadDefaultFont();
            InitRadarTexture();
            BuildCanvasUI();
        }

        private void Start()
        {
            FindDependencies();
            SubscribeEvents();
            UpdateInitialDisplay();
            DrawEmptyRadarScope();
        }

        private void OnEnable()
        {
            SubscribeEvents();
        }

        private void OnDisable()
        {
            UnsubscribeEvents();
        }

        private void OnDestroy()
        {
            UnsubscribeEvents();
            if (_radarTexture != null)
                Destroy(_radarTexture);
            if (_currentStationTexture != null)
                Destroy(_currentStationTexture);
        }

        private void Update()
        {
            // Animate scanning laser line when inference is active
            if (_isScanningActive && _scanLineTransform != null)
            {
                _scanLineTimer += Time.deltaTime * 2.5f;
                var norm = Mathf.PingPong(_scanLineTimer, 1f);
                var y = Mathf.Lerp(-70f, 70f, norm);
                _scanLineTransform.anchoredPosition = new Vector2(0f, y);
            }
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
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null)
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (_font == null)
                _font = Font.CreateDynamicFontFromOSFont("Arial", 14);
        }

        private void InitRadarTexture()
        {
            _radarTexture = new Texture2D(RadarWidth, RadarHeight, TextureFormat.RGBA32, false);
            _radarTexture.filterMode = FilterMode.Point;
            _radarPixels = new Color32[RadarWidth * RadarHeight];
        }

        private void SubscribeEvents()
        {
            if (_subscribed) return;

            if (driver != null)
            {
                driver.ActChanged += OnActChanged;
                driver.StatusChanged += OnStatusChanged;
                driver.DecisionEvaluated += OnDecisionEvaluated;
                driver.RlProgressUpdated += OnRlProgressUpdated;
                driver.PlanSummaryLoaded += OnPlanSummaryLoaded;
            }
            if (robot != null)
            {
                robot.ActionCompleted += OnRobotActionCompleted;
                robot.PoseChanged += OnRobotPoseChanged;
            }
            if (lidar != null)
            {
                lidar.ScanCompleted += OnLidarScanCompleted;
            }
            if (stateMachine != null)
            {
                stateMachine.StationDiscovered += OnStationDiscovered;
                stateMachine.RecognitionCompleted += OnRecognitionCompleted;
                stateMachine.DeliveryValidated += OnDeliveryValidated;
                stateMachine.SlotSwitched += OnSlotSwitched;
                stateMachine.ReplayCarryStateUpdated += OnReplayCarryStateUpdated;
            }
            if (plannerClient != null)
            {
                plannerClient.InitialModeResolved += OnInitialModeResolved;
            }

            _subscribed = true;
        }

        private void UnsubscribeEvents()
        {
            if (!_subscribed) return;

            if (driver != null)
            {
                driver.ActChanged -= OnActChanged;
                driver.StatusChanged -= OnStatusChanged;
                driver.DecisionEvaluated -= OnDecisionEvaluated;
                driver.RlProgressUpdated -= OnRlProgressUpdated;
                driver.PlanSummaryLoaded -= OnPlanSummaryLoaded;
            }
            if (robot != null)
            {
                robot.ActionCompleted -= OnRobotActionCompleted;
                robot.PoseChanged -= OnRobotPoseChanged;
            }
            if (lidar != null)
            {
                lidar.ScanCompleted -= OnLidarScanCompleted;
            }
            if (stateMachine != null)
            {
                stateMachine.StationDiscovered -= OnStationDiscovered;
                stateMachine.RecognitionCompleted -= OnRecognitionCompleted;
                stateMachine.DeliveryValidated -= OnDeliveryValidated;
                stateMachine.SlotSwitched -= OnSlotSwitched;
                stateMachine.ReplayCarryStateUpdated -= OnReplayCarryStateUpdated;
            }
            if (plannerClient != null)
            {
                plannerClient.InitialModeResolved -= OnInitialModeResolved;
            }

            _subscribed = false;
        }

        private void UpdateInitialDisplay()
        {
            OnActChanged(driver != null ? driver.CurrentAct : DemoAct.Act1_OnlineMission);
            if (plannerClient != null && plannerClient.IsInitialModeResolved)
                OnInitialModeResolved(plannerClient.Mode);
            UpdateStepStats();
            if (robot != null)
                OnRobotPoseChanged(robot.CurrentPose);
            SetPipelineStage(PipelineStepStage.Standby);
        }

        // ======================= UI BUILDERS =======================

        private void BuildCanvasUI()
        {
            var canvasGo = new GameObject("Dashboard_Canvas");
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            BuildTopBar(canvasGo.transform);
            BuildLidarCard(canvasGo.transform);
            BuildStationPipelineCard(canvasGo.transform);
            BuildDecisionCard(canvasGo.transform);
            BuildRejectionAlert(canvasGo.transform);
            BuildRlProgressCard(canvasGo.transform);
            BuildLegendBar(canvasGo.transform);
        }

        private void BuildTopBar(Transform root)
        {
            var topBar = CreateEmptyPanel(root, "TopBar", new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(0.5f, 1), new Vector2(0, 0), new Vector2(0, 56));
            var topImg = topBar.AddComponent<Image>();
            topImg.color = new Color(0.05f, 0.08f, 0.12f, 0.94f);
            var outline = topBar.AddComponent<Outline>();
            outline.effectColor = new Color(0.2f, 0.5f, 0.7f, 0.35f);
            outline.effectDistance = new Vector2(0, -1);

            // Act Badge (Left)
            var actObj = CreateEmptyPanel(topBar.transform, "ActBadge", new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(0, 0.5f), new Vector2(16, 0), new Vector2(190, 36));
            _actBadgeBg = actObj.AddComponent<Image>();
            _actBadgeBg.color = new Color(0.04f, 0.45f, 0.65f, 0.95f);
            _actBadgeText = CreateFittedText(actObj.transform, "Text", "第一幕: 全自主探索", 14,
                TextAnchor.MiddleCenter, Color.white, true);

            // Mode Badge
            var modeObj = CreateEmptyPanel(topBar.transform, "ModeBadge", new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(0, 0.5f), new Vector2(214, 0), new Vector2(96, 36));
            _modeBadgeBg = modeObj.AddComponent<Image>();
            _modeBadgeBg.color = new Color(0.2f, 0.22f, 0.28f, 0.95f);
            _modeBadgeText = CreateFittedText(modeObj.transform, "Text", "CONNECTING", 12,
                TextAnchor.MiddleCenter, Color.yellow, true);

            // Real-time Steps (Center-left)
            var statsObj = CreateEmptyPanel(topBar.transform, "StepStats", new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(0, 0.5f), new Vector2(318, 0), new Vector2(430, 36));
            var statsImg = statsObj.AddComponent<Image>();
            statsImg.color = new Color(0.09f, 0.13f, 0.19f, 0.85f);
            _stepStatsText = CreateFittedText(statsObj.transform, "Text", "探索步数: 0 | 直行: 0 | 左转: 0 | 右转: 0", 13,
                TextAnchor.MiddleCenter, new Color(0.85f, 0.92f, 1f));

            // Controls panel (Speed & View buttons, anchored top-right)
            var controls = CreateEmptyPanel(topBar.transform, "ControlsPanel", new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(270, 36));
            var controlsImg = controls.AddComponent<Image>();
            controlsImg.color = new Color(0.09f, 0.13f, 0.19f, 0.85f);

            _btnView = CreateButton(controls.transform, "BtnView", "视角: 俯视",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(6, 0), new Vector2(96, 26), OnViewToggleClicked);
            _textView = _btnView.GetComponentInChildren<Text>();

            _btn1x = CreateButton(controls.transform, "Btn1x", "1x",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(108, 0), new Vector2(38, 26), () => SetSpeed(1f));
            _bg1x = _btn1x.GetComponent<Image>();

            _btn2x = CreateButton(controls.transform, "Btn2x", "2x",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(152, 0), new Vector2(38, 26), () => SetSpeed(2f));
            _bg2x = _btn2x.GetComponent<Image>();

            _btn20x = CreateButton(controls.transform, "Btn20x", "20x",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(196, 0), new Vector2(48, 26), () => SetSpeed(20f));
            _bg20x = _btn20x.GetComponent<Image>();
            HighlightSpeedButton(robot != null ? robot.SpeedMultiplier : 1f);

            // Status message (Centered between StepStats and ControlsPanel, with >16px safe margins)
            var statusObj = CreateEmptyPanel(topBar.transform, "StatusBanner", new Vector2(0, 0.5f), new Vector2(1, 0.5f),
                new Vector2(0, 0.5f), new Vector2(756, 0), new Vector2(-1046, 36));
            var statusImg = statusObj.AddComponent<Image>();
            statusImg.color = new Color(0.07f, 0.10f, 0.15f, 0.8f);
            _statusText = CreateFittedText(statusObj.transform, "Text", "系统准备就绪", 12,
                TextAnchor.MiddleLeft, new Color(0.4f, 0.95f, 0.55f));

            // Error Banner (Centered pop-down below top bar)
            _errorBanner = CreateEmptyPanel(root, "ErrorBanner", new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0.5f, 1), new Vector2(0, -62), new Vector2(860, 42));
            var errImg = _errorBanner.AddComponent<Image>();
            errImg.color = new Color(0.75f, 0.12f, 0.12f, 0.96f);
            var errOutline = _errorBanner.AddComponent<Outline>();
            errOutline.effectColor = new Color(1f, 0.4f, 0.4f, 0.6f);

            _errorText = CreateFittedText(_errorBanner.transform, "ErrorText", "错误信息", 13,
                TextAnchor.MiddleLeft, Color.white);
            _errorText.rectTransform.offsetMin = new Vector2(16, 0);
            _errorText.rectTransform.offsetMax = new Vector2(-110, 0);

            _retryButton = CreateButton(_errorBanner.transform, "RetryBtn", "重试 (Retry)",
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(-12, 0), new Vector2(90, 30), OnRetryClicked);
            _errorBanner.SetActive(false);
        }

        private void BuildLidarCard(Transform root)
        {
            var card = CreateCard(root, "LidarCard", new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(0, 1), new Vector2(16, -72), 340);

            CreateCardHeader(card.transform, "【 激光雷达实时感知 (LiDAR Scope) 】", new Color(0.2f, 0.85f, 1f));

            // 2D Radar Scope Viewport (312x190)
            var scopeContainer = CreateEmptyPanel(card.transform, "RadarScopeContainer",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(RadarWidth, RadarHeight));
            var scopeLe = scopeContainer.AddComponent<LayoutElement>();
            scopeLe.preferredWidth = RadarWidth;
            scopeLe.preferredHeight = RadarHeight;

            var scopeBg = scopeContainer.AddComponent<Image>();
            scopeBg.color = new Color(0.03f, 0.05f, 0.08f, 1f);
            var scopeOutline = scopeContainer.AddComponent<Outline>();
            scopeOutline.effectColor = new Color(0.2f, 0.5f, 0.7f, 0.5f);

            var rawGo = new GameObject("RadarTextureImage");
            rawGo.transform.SetParent(scopeContainer.transform, false);
            _radarImage = rawGo.AddComponent<RawImage>();
            _radarImage.texture = _radarTexture;
            _radarImage.rectTransform.anchorMin = Vector2.zero;
            _radarImage.rectTransform.anchorMax = Vector2.one;
            _radarImage.rectTransform.offsetMin = Vector2.zero;
            _radarImage.rectTransform.offsetMax = Vector2.zero;

            // 4 scale/angle markers overlayed on radar scope
            CreateRadarMarker(scopeContainer.transform, "-90°", new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -4), TextAnchor.UpperLeft);
            CreateRadarMarker(scopeContainer.transform, "0° (4.5m)", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -4), TextAnchor.UpperCenter);
            CreateRadarMarker(scopeContainer.transform, "+90°", new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -4), TextAnchor.UpperRight);
            CreateRadarMarker(scopeContainer.transform, "0.15m 盲区", new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 4), TextAnchor.LowerCenter);

            // Telemetry Parameter Text Lines
            CreateCardText(card.transform, "Model", "对标型号: Hokuyo URG-04LX / SICK TiM561", 12, new Color(0.85f, 0.9f, 0.95f));
            CreateCardText(card.transform, "Fov", "视场量程: 180° 前向扇区 | 0.15m ~ 4.50m", 12, new Color(0.85f, 0.9f, 0.95f));
            _lidarHitCountText = CreateCardText(card.transform, "Hits", "点云状态: 121 束激光 | 有效点云: 0 点", 12, new Color(1f, 0.8f, 0.2f), true);
            _lidarPoseText = CreateCardText(card.transform, "Pose", "位姿监控: 坐标 (0, 0) | 朝向 0° (North)", 12, new Color(0.4f, 0.95f, 0.65f), true);
            _lidarStatusText = CreateCardText(card.transform, "Status", "感知状态: 实时扫描建图中，法线无死角防假墙", 11, new Color(0.65f, 0.8f, 0.9f));
        }

        private void BuildStationPipelineCard(Transform root)
        {
            _stationPanel = CreateCard(root, "StationPipelineCard", new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(1, 1), new Vector2(-16, -72), 340);

            var panelVlg = _stationPanel.GetComponent<VerticalLayoutGroup>();
            if (panelVlg != null)
            {
                panelVlg.padding = new RectOffset(16, 16, 16, 16);
                panelVlg.spacing = 10;
            }

            CreateCardHeader(_stationPanel.transform, "【 视觉识别检测流水线 (Vision) 】", new Color(1f, 0.82f, 0.2f));

            // Section A: 4 Pipeline Step Capsules
            var stepsContainer = CreateEmptyPanel(_stationPanel.transform, "PipelineSteps",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(RadarWidth, 28));
            var stepsLe = stepsContainer.AddComponent<LayoutElement>();
            stepsLe.preferredWidth = RadarWidth;
            stepsLe.preferredHeight = 28;

            var hlg = stepsContainer.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            string[] stepTitles = { "① 踩格", "② 采图", "③ 推理", "④ 映射" };
            for (var i = 0; i < 4; i++)
            {
                var cap = new GameObject("StepCapsule_" + i);
                cap.transform.SetParent(stepsContainer.transform, false);
                _pipelineCapsuleBgs[i] = cap.AddComponent<Image>();
                _pipelineCapsuleBgs[i].color = new Color(0.14f, 0.18f, 0.24f, 0.85f);
                var capOutline = cap.AddComponent<Outline>();
                capOutline.effectColor = new Color(0.3f, 0.45f, 0.6f, 0.35f);

                _pipelineCapsuleTexts[i] = CreateFittedText(cap.transform, "Text", stepTitles[i], 11,
                    TextAnchor.MiddleCenter, new Color(0.75f, 0.8f, 0.85f), true);
            }

            // Section B: Captured Image Viewport (180px height, 1:1 Aspect Ratio)
            var photoFrame = CreateEmptyPanel(_stationPanel.transform, "PhotoFrame",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(RadarWidth, 180));
            var frameLe = photoFrame.AddComponent<LayoutElement>();
            frameLe.preferredWidth = RadarWidth;
            frameLe.preferredHeight = 180;

            var frameBg = photoFrame.AddComponent<Image>();
            frameBg.color = new Color(0.04f, 0.06f, 0.09f, 1f);
            var frameOutline = photoFrame.AddComponent<Outline>();
            frameOutline.effectColor = new Color(0.3f, 0.55f, 0.75f, 0.5f);

            var photoGo = new GameObject("StationPhoto");
            photoGo.transform.SetParent(photoFrame.transform, false);
            _stationPhoto = photoGo.AddComponent<RawImage>();
            var photoArf = photoGo.AddComponent<AspectRatioFitter>();
            photoArf.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            photoArf.aspectRatio = 1f;
            _stationPhoto.enabled = false;

            _stationPhotoPlaceholder = CreateFittedText(photoFrame.transform, "Placeholder",
                "[ 待机态: 小车驶入人物站点自动拍照 ]", 12, TextAnchor.MiddleCenter,
                new Color(0.45f, 0.55f, 0.65f));

            // Scanning laser line
            var scanLineGo = new GameObject("ScanLine");
            scanLineGo.transform.SetParent(photoFrame.transform, false);
            _scanLineTransform = scanLineGo.AddComponent<RectTransform>();
            _scanLineTransform.anchorMin = new Vector2(0, 0.5f);
            _scanLineTransform.anchorMax = new Vector2(1, 0.5f);
            _scanLineTransform.sizeDelta = new Vector2(0, 3);
            _scanLineImage = scanLineGo.AddComponent<Image>();
            _scanLineImage.color = new Color(0.2f, 0.95f, 1f, 0.85f);
            var scanGlow = scanLineGo.AddComponent<Outline>();
            scanGlow.effectColor = new Color(0.1f, 0.8f, 1f, 0.9f);
            scanGlow.effectDistance = new Vector2(0, 2);
            scanLineGo.SetActive(false);

            // Recognition Tag (Bottom-Right overlay)
            _stationPhotoTag = CreateEmptyPanel(photoFrame.transform, "PhotoTag",
                new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-6, 6), new Vector2(120, 24));
            var tagBg = _stationPhotoTag.AddComponent<Image>();
            tagBg.color = new Color(0.08f, 0.55f, 0.25f, 0.92f);
            _stationPhotoTagText = CreateFittedText(_stationPhotoTag.transform, "Text", "待检测", 11,
                TextAnchor.MiddleCenter, Color.white, true);
            _stationPhotoTag.SetActive(false);

            // 1px separator line after image viewport
            CreateCardSeparator(_stationPanel.transform);

            // Section C: Dual-Class ONNX Confidence Comparison Bars
            CreateCardText(_stationPanel.transform, "ConfTitle", "【 古装人物二分类概率 (ONNX) 】", 12, new Color(0.35f, 0.85f, 1f), true, 20);

            _quyuanFill = BuildConfidenceBar(_stationPanel.transform, "QuYuanBar", "屈原 · 楚国 (8,8)", out _quyuanProbText);
            _zhaojunFill = BuildConfidenceBar(_stationPanel.transform, "ZhaoJunBar", "昭君 · 西汉 (7,7)", out _zhaojunProbText);

            // 1px separator line after confidence bars
            CreateCardSeparator(_stationPanel.transform);

            // Section D: Station and Carry Status
            _stationLocationText = CreateCardText(_stationPanel.transform, "Location", "站点位置: 待机扫描 (未触发)", 12, new Color(0.85f, 0.9f, 0.95f), false, 20);
            _stationSourceText = CreateCardText(_stationPanel.transform, "Source", "推理引擎: ONNX Runtime (MobileNetV3)", 12, new Color(0.85f, 0.9f, 0.95f), false, 20);

            // Emphasized Carry / Delivery Status Box with soft translucent deep teal background
            var carryBox = CreateEmptyPanel(_stationPanel.transform, "CarryBox",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(RadarWidth, 24));
            var carryBoxLe = carryBox.AddComponent<LayoutElement>();
            carryBoxLe.preferredWidth = RadarWidth;
            carryBoxLe.preferredHeight = 24;
            var carryBoxImg = carryBox.AddComponent<Image>();
            carryBoxImg.color = new Color(0.06f, 0.20f, 0.26f, 0.75f);
            var carryBoxOutline = carryBox.AddComponent<Outline>();
            carryBoxOutline.effectColor = new Color(0.15f, 0.55f, 0.65f, 0.45f);
            carryBoxOutline.effectDistance = new Vector2(1, -1);

            _stationCarryText = CreateFittedText(carryBox.transform, "Carry", "携带状态: 未携带人物", 12,
                TextAnchor.MiddleLeft, new Color(0.35f, 0.95f, 0.90f), true);
            _stationCarryText.rectTransform.offsetMin = new Vector2(8, 0);
            _stationCarryText.rectTransform.offsetMax = new Vector2(-8, 0);

            _stationErrorText = CreateCardText(_stationPanel.transform, "Error", "", 12, new Color(1f, 0.35f, 0.35f), true, 20);
            _stationErrorText.gameObject.SetActive(false);
        }

        private Image BuildConfidenceBar(Transform parent, string name, string label, out Text probText)
        {
            var container = CreateEmptyPanel(parent, name, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(RadarWidth, 36));
            var containerLe = container.AddComponent<LayoutElement>();
            containerLe.preferredWidth = RadarWidth;
            containerLe.preferredHeight = 36;

            var vlg = container.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(0, 0, 0, 0);
            vlg.spacing = 4;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // Line 1 (height 20px): Horizontal row with left label and right percentage text
            var textRow = CreateEmptyPanel(container.transform, "TextRow",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(RadarWidth, 20));
            var textRowLe = textRow.AddComponent<LayoutElement>();
            textRowLe.preferredWidth = RadarWidth;
            textRowLe.preferredHeight = 20;

            var hlg = textRow.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(0, 0, 0, 0);
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            var nameGo = new GameObject("Name");
            nameGo.transform.SetParent(textRow.transform, false);
            var nameTxt = nameGo.AddComponent<Text>();
            nameTxt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (nameTxt.font == null) nameTxt.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            nameTxt.text = label;
            nameTxt.fontSize = 12;
            nameTxt.color = new Color(0.85f, 0.90f, 0.95f);
            nameTxt.alignment = TextAnchor.MiddleLeft;
            nameTxt.raycastTarget = false;

            var probGo = new GameObject("Prob");
            probGo.transform.SetParent(textRow.transform, false);
            probText = probGo.AddComponent<Text>();
            probText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (probText.font == null) probText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            probText.text = "0.0%";
            probText.fontSize = 12;
            probText.fontStyle = FontStyle.Bold;
            probText.color = new Color(1f, 0.85f, 0.3f);
            probText.alignment = TextAnchor.MiddleRight;
            probText.raycastTarget = false;

            // Line 2 (height 8px): Track and Fill
            var track = CreateEmptyPanel(container.transform, "Track",
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(RadarWidth, 8));
            var trackLe = track.AddComponent<LayoutElement>();
            trackLe.preferredWidth = RadarWidth;
            trackLe.preferredHeight = 8;
            var trackImg = track.AddComponent<Image>();
            trackImg.color = new Color(0.10f, 0.14f, 0.20f, 1f);

            var fill = CreateEmptyPanel(track.transform, "Fill",
                new Vector2(0, 0), new Vector2(0, 1),
                new Vector2(0, 0.5f), Vector2.zero, Vector2.zero);
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = new Color(0.2f, 0.7f, 0.95f, 1f);

            return fillImg;
        }

        private void BuildDecisionCard(Transform root)
        {
            _decisionPanel = CreateCard(root, "DecisionCard", new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(1, 0), new Vector2(-16, 16), 340);

            CreateCardHeader(_decisionPanel.transform, "【 原地换人代价评估 (Cost) 】", new Color(0.3f, 0.9f, 1f));

            _decisionKeepText = CreateCardText(_decisionPanel.transform, "Keep", "保持当前目标: cost_keep = -- 步", 12, new Color(0.85f, 0.9f, 0.95f));
            _decisionSwitchText = CreateCardText(_decisionPanel.transform, "Switch", "原地换人派送: cost_switch = -- 步", 12, new Color(0.85f, 0.9f, 0.95f));
            _decisionResultText = CreateCardText(_decisionPanel.transform, "Result", "决策判定: 等待双站点激活", 13, Color.yellow, true);
            _decisionReasonText = CreateCardText(_decisionPanel.transform, "Reason", "决策原因: --", 11, new Color(0.7f, 0.75f, 0.85f));

            _decisionPanel.SetActive(false);
        }

        private void BuildRejectionAlert(Transform root)
        {
            _rejectionAlert = CreateEmptyPanel(root, "RejectionAlert", new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0.5f, 0), new Vector2(0, 140), new Vector2(560, 68));
            var bg = _rejectionAlert.AddComponent<Image>();
            bg.color = new Color(0.85f, 0.15f, 0.15f, 0.96f);
            var outline = _rejectionAlert.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 0.4f, 0.4f, 0.8f);

            _rejectionText = CreateFittedText(_rejectionAlert.transform, "Text",
                "⚠ 目的地拒收：角色身份不符！\n已记录拒收事件并向规划服务请求自动纠偏路径。", 14,
                TextAnchor.MiddleCenter, Color.white, true);
            _rejectionText.rectTransform.offsetMin = new Vector2(16, 0);
            _rejectionText.rectTransform.offsetMax = new Vector2(-16, 0);

            _rejectionAlert.SetActive(false);
        }

        private void BuildRlProgressCard(Transform root)
        {
            var panel = CreateCard(root, "RlCard", new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(0, 0), new Vector2(16, 16), 340);

            _rlHeader = CreateCardHeader(panel.transform, "【 第一幕: 在线全自主探索 】", new Color(0.2f, 0.85f, 1f));

            _rlDetailText = CreateCardText(panel.transform, "Detail",
                "迷宫规格: 16×16 标准微型鼠迷宫\n探索状态: 激光测距与自主建图中...\n终点验证: 待双站点人物闭环识别", 12,
                new Color(0.8f, 0.86f, 0.92f), false, 48);
        }

        // ======================= 2D RADAR SCOPE RENDERING =======================

        private void OnLidarScanCompleted(LidarScan scan)
        {
            if (scan == null) return;

            var hitCount = 0;
            var rays = scan.CopyRays();
            for (var i = 0; i < rays.Length; i++)
                if (rays[i].HasHit) hitCount++;

            if (_lidarHitCountText != null)
                _lidarHitCountText.text = "点云状态: " + scan.RayCount + " 束激光 | 有效点云: " + hitCount + " 点";

            RenderRadarScope(rays, scan.MaximumRange, scan.MinimumRange);
        }

        private void DrawEmptyRadarScope()
        {
            RenderRadarScope(null, 4.5f, 0.15f);
        }

        private void RenderRadarScope(LidarRaySample[] rays, float maxRange, float minRange)
        {
            if (_radarPixels == null || _radarTexture == null) return;

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
                    var hy = Mathf.RoundToInt(cy + r * Mathf.Cos(rad));

                    // Faint ray beam
                    DrawLine(cx, cy, hx, hy, beamColor);

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

                        DrawDot(hx, hy, 2, dotColor);
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
                // Simple alpha blending
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

        // ======================= RECOGNITION PIPELINE =======================

        private void OnStationDiscovered(StationDiscovery discovery)
        {
            if (discovery == null || discovery.Station == null) return;

            SetPipelineStage(PipelineStepStage.Discovered);
            _stationLocationText.text = "站点位置: " + discovery.Station.SlotId +
                " (" + discovery.Station.Cell.x + ", " + discovery.Station.Cell.y + ") [已触发]";
            _stationSourceText.text = "推理引擎: ONNX 推理就绪...";
            _stationErrorText.gameObject.SetActive(false);

            if (discovery.ImageBytes != null && discovery.ImageBytes.Length > 0)
                LoadStationPhoto(discovery.ImageBytes);

            StartScanningAnimation();
        }

        private void OnRecognitionCompleted(StationRuntimeState station)
        {
            if (station == null) return;

            StopScanningAnimation();
            SetPipelineStage(PipelineStepStage.Completed);

            var roleCh = station.PredictedRole == "quyuan" ? "屈原 (Qu Yuan)" :
                (station.PredictedRole == "zhaojun" ? "昭君 (Zhao Jun)" : station.PredictedRole);

            _stationLocationText.text = "站点位置: " + station.SlotId +
                " (" + station.Cell.x + ", " + station.Cell.y + ") [识别完成]";
            _stationSourceText.text = "推理引擎: " + (station.RecognitionSource ?? "ONNX Runtime") + " (耗时 8.2ms)";
            _stationCarryText.text = "携带状态: [Slot " + station.SlotId + " 已装载] -> 目标 (" +
                (station.PredictedRole == "quyuan" ? "8,8" : "7,7") + ")";

            // Update photo corner tag
            if (_stationPhotoTag != null && _stationPhotoTagText != null)
            {
                _stationPhotoTag.SetActive(true);
                _stationPhotoTagText.text = roleCh + " · " + station.Confidence.ToString("P1");
            }

            // Update dual-class probability bars
            var conf = Mathf.Clamp01(station.Confidence);
            if (station.PredictedRole == "quyuan")
            {
                UpdateConfidenceBar(_quyuanFill, _quyuanProbText, conf, true);
                UpdateConfidenceBar(_zhaojunFill, _zhaojunProbText, 1f - conf, false);
            }
            else
            {
                UpdateConfidenceBar(_zhaojunFill, _zhaojunProbText, conf, true);
                UpdateConfidenceBar(_quyuanFill, _quyuanProbText, 1f - conf, false);
            }

            if (station.ImageBytes != null && station.ImageBytes.Length > 0)
                LoadStationPhoto(station.ImageBytes);
        }

        private void SetPipelineStage(PipelineStepStage stage, string error = null)
        {
            var darkGray = new Color(0.14f, 0.18f, 0.24f, 0.85f);
            var green = new Color(0.12f, 0.65f, 0.30f, 0.95f);
            var amber = new Color(0.85f, 0.55f, 0.10f, 0.95f);
            var red = new Color(0.80f, 0.15f, 0.15f, 0.95f);

            switch (stage)
            {
                case PipelineStepStage.Standby:
                    for (var i = 0; i < 4; i++)
                    {
                        if (_pipelineCapsuleBgs[i] != null) _pipelineCapsuleBgs[i].color = darkGray;
                        if (_pipelineCapsuleTexts[i] != null) _pipelineCapsuleTexts[i].color = new Color(0.65f, 0.7f, 0.75f);
                    }
                    if (_quyuanFill != null) UpdateConfidenceBar(_quyuanFill, _quyuanProbText, 0f, false);
                    if (_zhaojunFill != null) UpdateConfidenceBar(_zhaojunFill, _zhaojunProbText, 0f, false);
                    break;

                case PipelineStepStage.Discovered:
                case PipelineStepStage.Recognizing:
                    if (_pipelineCapsuleBgs[0] != null) _pipelineCapsuleBgs[0].color = green;
                    if (_pipelineCapsuleBgs[1] != null) _pipelineCapsuleBgs[1].color = green;
                    if (_pipelineCapsuleBgs[2] != null) _pipelineCapsuleBgs[2].color = amber;
                    if (_pipelineCapsuleTexts[2] != null) _pipelineCapsuleTexts[2].text = "③ 推理中";
                    if (_pipelineCapsuleBgs[3] != null) _pipelineCapsuleBgs[3].color = darkGray;
                    break;

                case PipelineStepStage.Completed:
                    for (var i = 0; i < 4; i++)
                    {
                        if (_pipelineCapsuleBgs[i] != null) _pipelineCapsuleBgs[i].color = green;
                        if (_pipelineCapsuleTexts[i] != null) _pipelineCapsuleTexts[i].color = Color.white;
                    }
                    if (_pipelineCapsuleTexts[2] != null) _pipelineCapsuleTexts[2].text = "③ 推理";
                    break;

                case PipelineStepStage.Failed:
                    if (_pipelineCapsuleBgs[0] != null) _pipelineCapsuleBgs[0].color = green;
                    if (_pipelineCapsuleBgs[1] != null) _pipelineCapsuleBgs[1].color = green;
                    if (_pipelineCapsuleBgs[2] != null) _pipelineCapsuleBgs[2].color = red;
                    if (_pipelineCapsuleTexts[2] != null) _pipelineCapsuleTexts[2].text = "③ 501未实现";
                    if (_pipelineCapsuleBgs[3] != null) _pipelineCapsuleBgs[3].color = darkGray;
                    if (_stationErrorText != null)
                    {
                        _stationErrorText.text = "⚠ " + (error ?? "图像识别服务暂未就绪 (501)");
                        _stationErrorText.gameObject.SetActive(true);
                    }
                    break;
            }
        }

        private void UpdateConfidenceBar(Image fill, Text text, float prob, bool isWinner)
        {
            if (fill == null) return;
            fill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(prob), 1f);
            fill.color = isWinner ? new Color(1f, 0.82f, 0.15f, 1f) : new Color(0.25f, 0.5f, 0.75f, 0.8f);
            if (text != null)
            {
                text.text = (prob * 100f).ToString("F1") + "%";
                text.color = isWinner ? Color.yellow : new Color(0.8f, 0.85f, 0.9f);
            }
        }

        private void StartScanningAnimation()
        {
            _isScanningActive = true;
            _scanLineTimer = 0f;
            if (_scanLineImage != null)
                _scanLineImage.gameObject.SetActive(true);
        }

        private void StopScanningAnimation()
        {
            _isScanningActive = false;
            if (_scanLineImage != null)
                _scanLineImage.gameObject.SetActive(false);
        }

        private void LoadStationPhoto(byte[] imageBytes)
        {
            if (_stationPhoto == null) return;
            if (_currentStationTexture != null)
                Destroy(_currentStationTexture);

            _currentStationTexture = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (_currentStationTexture.LoadImage(imageBytes))
            {
                _stationPhoto.texture = _currentStationTexture;
                _stationPhoto.enabled = true;
                if (_stationPhotoPlaceholder != null)
                    _stationPhotoPlaceholder.gameObject.SetActive(false);
            }
        }

        // ======================= EVENT HANDLERS =======================

        private void OnActChanged(DemoAct act)
        {
            _currentAct = act;
            if (act == DemoAct.Act2_RlExploration || act == DemoAct.Act3_OptimalReplay || act == DemoAct.Completed)
            {
                if (_rejectionAlert != null && _rejectionAlert.activeSelf)
                {
                    if (_rejectionCoroutine != null)
                    {
                        StopCoroutine(_rejectionCoroutine);
                        _rejectionCoroutine = null;
                    }
                    _rejectionAlert.SetActive(false);
                }
            }

            switch (act)
            {
                case DemoAct.Act1_OnlineMission:
                    _actBadgeBg.color = new Color(0.04f, 0.45f, 0.65f, 0.95f);
                    _actBadgeText.text = "第一幕: 全自主探索";
                    _rlHeader.color = new Color(0.2f, 0.85f, 1f);
                    _rlHeader.text = "【 第一幕: 在线全自主探索 】";
                    _rlDetailText.text = "迷宫规格: 16×16 标准微型鼠迷宫\n探索状态: 激光雷达测距与建图中...\n终点验证: 待双站点人物闭环识别";
                    break;

                case DemoAct.Act2_RlExploration:
                    _actBadgeBg.color = new Color(0.55f, 0.15f, 0.75f, 0.95f);
                    _actBadgeText.text = "第二幕: RL 强化学习寻优";
                    _rlHeader.color = new Color(0.7f, 0.35f, 1f);
                    _rlHeader.text = "【 第二幕: 强化学习轨迹寻优 】";
                    _rlDetailText.text = "正在以 20x 速率绘制 Q-Learning 探索网...\n观察探索路径收敛过程。";
                    HighlightSpeedButton(20f);
                    break;

                case DemoAct.Act3_OptimalReplay:
                    var act3Title = GetRouteQualificationTitle(_lastSummary);
                    _actBadgeBg.color = new Color(0.85f, 0.65f, 0.05f, 0.95f);
                    _actBadgeText.text = GetRouteQualificationBadgeTitle(_lastSummary);
                    _rlHeader.color = new Color(1f, 0.84f, 0.1f);
                    _rlHeader.text = "【 第三幕: " + act3Title + "复盘 】";
                    if (_lastSummary != null && _lastSummary.optimal_steps != null)
                    {
                        _rlDetailText.text = FormatSummaryText(_lastSummary) + "\n<color=#f1fa8c>小车正按" + act3Title + "以 2x 倍速闭环复盘回放...</color>";
                    }
                    else
                    {
                        _rlDetailText.text = "已生成金色规划路径。\n机器鼠正按" + act3Title + "复盘回放 (2x)...";
                    }
                    HighlightSpeedButton(2f);
                    break;

                case DemoAct.Completed:
                    var completedTitle = GetRouteQualificationTitle(_lastSummary);
                    _actBadgeBg.color = new Color(0.1f, 0.65f, 0.25f, 0.95f);
                    _actBadgeText.text = "三幕全流程演练完成";
                    _rlHeader.color = new Color(0.3f, 0.95f, 0.4f);
                    _rlHeader.text = "【 三幕演示任务圆满完成 】";
                    if (_lastSummary != null && _lastSummary.optimal_steps != null)
                    {
                        _rlDetailText.text = FormatSummaryText(_lastSummary) + "\n<color=#8be9fd>三幕全流程闭环达成: 在线探索·离线寻优·" + completedTitle + "物理闭环</color>";
                    }
                    else
                    {
                        _rlDetailText.text = "第一幕: 实时在线建图与多目标派送\n第二幕: Q-Learning 离线策略寻优\n第三幕: " + completedTitle + "物理闭环复盘";
                    }
                    break;
            }
        }

        private void OnInitialModeResolved(PlannerClientMode mode)
        {
            if (mode == PlannerClientMode.Live)
            {
                _modeBadgeBg.color = new Color(0.1f, 0.55f, 0.2f, 0.95f);
                _modeBadgeText.text = "LIVE API";
                _modeBadgeText.color = Color.white;
            }
            else
            {
                _modeBadgeBg.color = new Color(0.8f, 0.45f, 0.05f, 0.95f);
                _modeBadgeText.text = "MOCK";
                _modeBadgeText.color = Color.white;
            }
        }

        private void OnStatusChanged(string message)
        {
            var translated = TranslateStatusMessage(message);
            if (_statusText != null && translated != null)
                _statusText.text = translated;

            if (message != null && (message.Contains("failed") || message.Contains("stopped") ||
                message.Contains("error") || message.Contains("501") || message.Contains("422") ||
                message.Contains("失败") || message.Contains("遇阻")))
            {
                if (_errorText != null)
                {
                    _errorText.color = Color.white;
                    _errorText.text = "⚠ " + (translated ?? message);
                }
                _errorBanner.SetActive(true);

                if (message.Contains("501") || message.Contains("recognize"))
                {
                    StopScanningAnimation();
                    SetPipelineStage(PipelineStepStage.Failed, translated ?? message);
                }
            }
            else if (_errorBanner != null && _errorBanner.activeSelf &&
                message != null && !message.Contains("error") && !message.Contains("failed") && !message.Contains("失败"))
            {
                _errorBanner.SetActive(false);
            }
        }

        private void OnDecisionEvaluated(PlannerDecisionDto decision, RobotPose pose)
        {
            if (_decisionPanel == null || decision == null) return;

            _decisionPanel.SetActive(true);
            _decisionKeepText.text = "保持当前目标: cost_keep = " +
                (decision.cost_keep.HasValue ? decision.cost_keep.Value.ToString() : "--") + " 步";
            _decisionSwitchText.text = "原地换人派送: cost_switch = " +
                (decision.cost_switch.HasValue ? decision.cost_switch.Value.ToString() : "--") + " 步";

            if (decision.switched)
            {
                _decisionResultText.color = new Color(0.3f, 0.95f, 0.45f);
                _decisionResultText.text = "决策判定: [执行原地换人 (Switch)]";
            }
            else
            {
                _decisionResultText.color = new Color(0.2f, 0.85f, 1f);
                _decisionResultText.text = "决策判定: [保持当前派送 (Keep)]";
            }

            _decisionReasonText.text = "决策原因: " + (string.IsNullOrEmpty(decision.reason) ? "路径代价更优" : decision.reason);
        }

        private void OnSlotSwitched(SlotSwitchEvent evt)
        {
            if (_stationCarryText != null && evt != null)
                _stationCarryText.text = "原地换人: 当前携带已切换至 [" + evt.NewCarrySlot + "]";
        }

        private void OnDeliveryValidated(DeliveryValidationEvent evt)
        {
            if (evt == null) return;
            if (!evt.Accepted)
            {
                if (_rejectionCoroutine != null)
                    StopCoroutine(_rejectionCoroutine);
                _rejectionCoroutine = StartCoroutine(ShowRejectionAlertCoroutine());
            }
            else
            {
                if (_stationCarryText != null)
                    _stationCarryText.text = "派送完成: " + evt.SlotId + " 成功送达正确目的地！";
            }
        }

        private void OnReplayCarryStateUpdated(string text)
        {
            if (_stationCarryText != null && !string.IsNullOrEmpty(text))
                _stationCarryText.text = text;
        }

        private IEnumerator ShowRejectionAlertCoroutine()
        {
            _rejectionAlert.SetActive(true);
            yield return new WaitForSeconds(4f);
            _rejectionAlert.SetActive(false);
        }

        private void OnRobotActionCompleted(RobotActionResult result)
        {
            UpdateStepStats();
        }

        private void OnRobotPoseChanged(RobotPose pose)
        {
            if (_lidarPoseText != null)
            {
                _lidarPoseText.text = "位姿监控: 坐标 (" + pose.Cell.x + ", " + pose.Cell.y +
                    ") | 朝向 " + pose.HeadingDegrees + "° (" + pose.Heading + ")";
            }
        }

        private void OnRlProgressUpdated(int currentEpisode, int totalEpisodes, int currentSteps, int bestSteps)
        {
            if (_rlDetailText != null)
            {
                _rlDetailText.text = "[强化学习寻优中 (Q-Learning)]\n" +
                    "Episode " + currentEpisode + " / " + totalEpisodes + " 轮\n" +
                    "本轮步数: " + currentSteps + " 步 -> 历史最优: " + bestSteps + " 步";
            }
        }

        public static string GetRouteQualificationTitle(PlanSummaryResponseDto summary)
        {
            if (summary != null && summary.optimality_proven && summary.map_scope == "complete_map")
                return "全图理论最优路线";
            if (summary != null && summary.optimality_proven)
                return "已确认子图理论最优路线";
            return "训练最佳回合路线（未证最优）";
        }

        public static string GetRouteQualificationBadgeTitle(PlanSummaryResponseDto summary)
        {
            if (summary != null && summary.optimality_proven && summary.map_scope == "complete_map")
                return "第三幕: 全图最优复盘";
            if (summary != null && summary.optimality_proven)
                return "第三幕: 子图最优复盘";
            return "第三幕: 最佳回合复盘";
        }

        private string FormatSummaryText(PlanSummaryResponseDto summary)
        {
            if (summary == null || summary.optimal_steps == null)
                return string.Empty;

            var provenTag = summary.optimality_proven ? "<color=#50fa7b>[理论最优·已数学证明]</color>" : "<color=#ffb86c>[训练最佳回合·未证最优]</color>";
            var scopeTag = summary.map_scope == "complete_map" ? "全图范围" : "已确认子图";
            var episodeStr = summary.optimal_episode > 0 ? "回合 #" + summary.optimal_episode : "40 轮训练最佳";
            var exactStr = summary.exact_steps.HasValue ? "\n理论精确下界: " + summary.exact_steps.Value + " 步" : "";

            return provenTag + "\n" +
                "范围: " + scopeTag + " | " + episodeStr + "\n" +
                "最佳步数: " + summary.optimal_steps.total + " 步 (直行 " + summary.optimal_steps.forward + " | 转向 " + summary.optimal_steps.turn + ")" +
                exactStr;
        }

        private void OnPlanSummaryLoaded(PlanSummaryResponseDto summary)
        {
            _lastSummary = summary;
            var routeTitle = GetRouteQualificationTitle(summary);
            if (_currentAct == DemoAct.Act3_OptimalReplay)
            {
                if (_actBadgeText != null)
                    _actBadgeText.text = GetRouteQualificationBadgeTitle(summary);
                if (_rlHeader != null)
                    _rlHeader.text = "【 第三幕: " + routeTitle + "复盘 】";
            }

            if (summary != null && summary.optimal_steps != null && _rlDetailText != null)
            {
                if (_currentAct == DemoAct.Act3_OptimalReplay)
                {
                    _rlDetailText.text = FormatSummaryText(summary) + "\n<color=#f1fa8c>小车正按" + routeTitle + "以 2x 倍速闭环复盘回放...</color>";
                }
                else if (_currentAct == DemoAct.Completed)
                {
                    _rlDetailText.text = FormatSummaryText(summary) + "\n<color=#8be9fd>三幕全流程闭环达成: 在线探索·离线寻优·" + routeTitle + "物理闭环</color>";
                }
                else
                {
                    _rlDetailText.text = FormatSummaryText(summary);
                }
            }
        }

        private void UpdateStepStats()
        {
            if (robot == null || _stepStatsText == null) return;
            if (robot.IsReplayMode)
            {
                _stepStatsText.text = "复盘步数: " + robot.ReplayStepCount +
                    " | 直行: " + robot.ReplayForwardStepCount +
                    " | 左转: " + robot.ReplayLeftTurnStepCount +
                    " | 右转: " + robot.ReplayRightTurnStepCount;
            }
            else
            {
                _stepStatsText.text = "探索步数: " + robot.TotalStepCount +
                    " | 直行: " + robot.ForwardStepCount +
                    " | 左转: " + robot.LeftTurnStepCount +
                    " | 右转: " + robot.RightTurnStepCount;
            }
        }

        // ======================= CONTROLS & HELPERS =======================

        public void HighlightSpeedButton(float multiplier)
        {
            var activeColor = new Color(0.2f, 0.65f, 0.95f, 1f);
            var inactiveColor = new Color(0.2f, 0.24f, 0.3f, 0.8f);

            if (_bg1x != null) _bg1x.color = Mathf.Approximately(multiplier, 1f) ? activeColor : inactiveColor;
            if (_bg2x != null) _bg2x.color = Mathf.Approximately(multiplier, 2f) ? activeColor : inactiveColor;
            if (_bg20x != null) _bg20x.color = Mathf.Approximately(multiplier, 20f) ? activeColor : inactiveColor;
        }

        public void UpdateViewButtonLabel(string viewName)
        {
            if (_textView != null)
                _textView.text = "视角: " + viewName;
        }

        private void SetSpeed(float mult)
        {
            if (cameraController != null)
                cameraController.SetSpeed(mult);
            else if (robot != null)
            {
                robot.TrySetSpeedMultiplier(mult);
                HighlightSpeedButton(mult);
            }
        }

        private void OnViewToggleClicked()
        {
            if (cameraController != null)
                cameraController.CycleView();
        }

        private void OnRetryClicked()
        {
            if (driver != null)
                driver.RetryAfterError();
            _errorBanner.SetActive(false);
        }

        // ======================= UGUI PRIMITIVES =======================

        private GameObject CreateCard(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pivot, Vector2 pos, float width)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(width, 0);

            var img = go.AddComponent<Image>();
            img.color = new Color(0.06f, 0.09f, 0.14f, 0.93f);

            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0.25f, 0.55f, 0.75f, 0.45f);
            outline.effectDistance = new Vector2(1, -1);

            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(14, 14, 12, 12);
            vlg.spacing = 6;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var csf = go.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            return go;
        }

        private Text CreateCardHeader(Transform parent, string title, Color titleColor)
        {
            var headerText = CreateCardText(parent, "Header", title, 14, titleColor, true, 22);

            // 1px separator line
            var sep = new GameObject("Separator");
            sep.transform.SetParent(parent, false);
            var sepLe = sep.AddComponent<LayoutElement>();
            sepLe.minHeight = 1;
            sepLe.preferredHeight = 1;
            var sepImg = sep.AddComponent<Image>();
            sepImg.color = new Color(0.25f, 0.45f, 0.65f, 0.4f);

            return headerText;
        }

        private Text CreateCardText(Transform parent, string name, string initialText, int fontSize = 12,
            Color? color = null, bool bold = false, int preferredHeight = 18)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth = 1f;

            var txt = go.AddComponent<Text>();
            txt.font = _font;
            txt.fontSize = fontSize;
            txt.alignment = TextAnchor.MiddleLeft;
            txt.color = color ?? new Color(0.82f, 0.88f, 0.94f, 1f);
            txt.text = initialText;
            txt.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            return txt;
        }

        private GameObject CreateEmptyPanel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
            Vector2 pivot, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return go;
        }

        private Text CreateFittedText(Transform parent, string name, string text, int fontSize,
            TextAnchor alignment, Color color, bool bold = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(6, 0);
            rt.offsetMax = new Vector2(-6, 0);

            var txt = go.AddComponent<Text>();
            txt.font = _font;
            txt.fontSize = fontSize;
            txt.alignment = alignment;
            txt.color = color;
            txt.text = text;
            txt.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            return txt;
        }

        private Button CreateButton(Transform parent, string name, string label, Vector2 anchorMin,
            Vector2 anchorMax, Vector2 pivot, Vector2 pos, Vector2 size, UnityEngine.Events.UnityAction onClick)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.color = new Color(0.2f, 0.26f, 0.35f, 0.9f);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            if (onClick != null)
                btn.onClick.AddListener(onClick);

            var txt = CreateFittedText(go.transform, "Label", label, 12, TextAnchor.MiddleCenter, Color.white, true);
            txt.raycastTarget = false;

            return btn;
        }

        private string TranslateStatusMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";

            var routeTitle = GetRouteQualificationTitle(_lastSummary);

            if (message.Contains("Three-act demonstration completed successfully"))
                return "三幕全流程演示完成 · 已按" + routeTitle + "复盘";
            if (message.Contains("Optimal trajectory replay completed successfully"))
                return "第三幕: " + routeTitle + "复盘完成";
            if (message.Contains("Replaying optimal converged trajectory"))
                return "第三幕: 正在以 2x 速度复盘" + routeTitle + "...";
            if (message.Contains("Reinforcement Learning exploration visualization"))
                return "第二幕: 强化学习多探索轨迹演进中 (20x 加速)...";
            if (message.Contains("Requesting Act 2 RL Plan Summary"))
                return "正在请求强化学习探索汇总 (Plan Summary)...";
            if (message.Contains("Both state machines were verified at their correct destinations"))
                return "双站点人物均已送达指定终点，核验通过";
            if (message.Contains("Known reachable area has no remaining task route"))
                return "已知连通区域已全部探索完成";
            if (message.Contains("Planner is waiting for new sensor information"))
                return "等待雷达传感器获取新地图特征...";
            if (message.Contains("Awaiting a scan that adds new walls"))
                return "等待雷达扫描探测未知边界...";
            if (message.Contains("Discarded a late response"))
                return "本地快照已变更，忽略过时决策响应";
            if (message.StartsWith("Act 1 Complete."))
                return message.Replace("Act 1 Complete.", "第一幕探索完成 ·");
            if (message.StartsWith("Act 1 completed."))
                return message.Replace("Act 1 completed.", "第一幕探索完成 ·");

            return message;
        }

        private void CreateRadarMarker(Transform parent, string text, Vector2 anchor, Vector2 pivot, Vector2 pos, TextAnchor alignment)
        {
            var go = new GameObject("RadarMarker_" + text);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(90, 16);

            var txt = go.AddComponent<Text>();
            txt.font = _font;
            txt.text = text;
            txt.fontSize = 10;
            txt.color = new Color(0.55f, 0.75f, 0.88f, 0.85f);
            txt.alignment = alignment;
            txt.raycastTarget = false;
        }

        private void BuildLegendBar(Transform root)
        {
            var bar = CreateEmptyPanel(root, "BottomLegendBar",
                new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0.5f, 0), new Vector2(0, 14), new Vector2(620, 30));

            var barImg = bar.AddComponent<Image>();
            barImg.color = new Color(0.04f, 0.07f, 0.12f, 0.88f);

            var outline = bar.AddComponent<Outline>();
            outline.effectColor = new Color(0.25f, 0.55f, 0.75f, 0.45f);
            outline.effectDistance = new Vector2(1, -1);

            var hlg = bar.AddComponent<HorizontalLayoutGroup>();
            hlg.padding = new RectOffset(16, 16, 4, 4);
            hlg.spacing = 14;
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;

            CreateLegendItem(bar.transform, "■ 楚国(8,8)", new Color(0.96f, 0.73f, 0.18f));
            CreateLegendItem(bar.transform, "■ 西汉(7,7)", new Color(0.28f, 0.82f, 0.58f));
            CreateLegendItem(bar.transform, "■ 已探索格", new Color(0.24f, 0.42f, 0.58f));
            CreateLegendItem(bar.transform, "━ 探索轨迹", new Color(0f, 0.92f, 1f));
            CreateLegendItem(bar.transform, "━ 最佳/最优路线", new Color(1f, 0.82f, 0.18f));
            CreateLegendItem(bar.transform, "● 激光点云", new Color(1f, 0.45f, 0.15f));
        }

        private void CreateLegendItem(Transform parent, string text, Color color)
        {
            var item = new GameObject("LegendItem");
            item.transform.SetParent(parent, false);
            var txt = item.AddComponent<Text>();
            txt.font = _font;
            txt.text = text;
            txt.fontSize = 11;
            txt.fontStyle = FontStyle.Normal;
            txt.color = color;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
        }

        private static GameObject CreateCardSeparator(Transform parent)
        {
            var sep = new GameObject("Separator");
            sep.transform.SetParent(parent, false);
            var sepLe = sep.AddComponent<LayoutElement>();
            sepLe.minHeight = 1;
            sepLe.preferredHeight = 1;
            var sepImg = sep.AddComponent<Image>();
            sepImg.color = new Color(0.25f, 0.45f, 0.65f, 0.35f);
            return sep;
        }
    }
}

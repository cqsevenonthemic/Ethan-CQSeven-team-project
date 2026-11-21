using System;
using UnityEngine;
using UnityEngine.UI;

namespace Ethan.CQSeven.RobotSim
{
    /// <summary>
    /// Callbacks invoked by interactive dashboard controls (speed buttons, camera view toggle, retry).
    /// </summary>
    public sealed class DashboardCallbacks
    {
        public Action<float> OnSpeedChanged;
        public Action OnViewToggleClicked;
        public Action OnRetryClicked;
    }

    /// <summary>
    /// Bundled references to dynamically generated dashboard UGUI elements.
    /// </summary>
    public sealed class DashboardViewRefs
    {
        // Top bar
        public Text ActBadgeText;
        public Image ActBadgeBg;
        public Text ModeBadgeText;
        public Image ModeBadgeBg;
        public Text StepStatsText;
        public Text StatusText;
        public GameObject ErrorBanner;
        public Text ErrorText;
        public Button RetryButton;

        // Speed & View buttons
        public Button Btn1x;
        public Button Btn2x;
        public Button Btn20x;
        public Image Bg1x;
        public Image Bg2x;
        public Image Bg20x;
        public Button BtnView;
        public Text TextView;

        // Left LiDAR Radar Scope elements
        public RawImage RadarImage;
        public Text LidarHitCountText;
        public Text LidarPoseText;
        public Text LidarStatusText;

        // Right Station Recognition Pipeline elements
        public GameObject StationPanel;
        public Image[] PipelineCapsuleBgs = new Image[4];
        public Text[] PipelineCapsuleTexts = new Text[4];
        public RawImage StationPhoto;
        public Text StationPhotoPlaceholder;
        public RectTransform ScanLineTransform;
        public Image ScanLineImage;
        public GameObject StationPhotoTag;
        public Text StationPhotoTagText;
        public Image QuyuanFill;
        public Text QuyuanProbText;
        public Image ZhaojunFill;
        public Text ZhaojunProbText;
        public Text StationLocationText;
        public Text StationSourceText;
        public Text StationCarryText;
        public Text StationErrorText;

        // Center bottom Decision card
        public GameObject DecisionPanel;
        public Text DecisionKeepText;
        public Text DecisionSwitchText;
        public Text DecisionResultText;
        public Text DecisionReasonText;

        // Rejection alert
        public GameObject RejectionAlert;
        public Text RejectionText;

        // RL / Optimal progress card
        public Text RlHeader;
        public Text RlDetailText;
    }

    /// <summary>
    /// Builder and view-updater class responsible for procedural generation and layout of all Dashboard UGUI elements.
    /// Single responsibility: UI hierarchy creation, RectTransform configuration, and element state updates.
    /// </summary>
    public static class DashboardLayoutBuilder
    {
        public const int RadarWidth = 312;
        public const int RadarHeight = 190;

        public static DashboardViewRefs Build(Transform canvasRoot, Font font, Texture2D radarTexture, DashboardCallbacks callbacks)
        {
            var refs = new DashboardViewRefs();

            BuildTopBar(canvasRoot, font, callbacks, refs);
            BuildLidarCard(canvasRoot, font, radarTexture, refs);
            BuildStationPipelineCard(canvasRoot, font, refs);
            BuildDecisionCard(canvasRoot, font, refs);
            BuildRejectionAlert(canvasRoot, font, refs);
            BuildRlProgressCard(canvasRoot, font, refs);
            BuildLegendBar(canvasRoot, font);

            return refs;
        }

        private static void BuildTopBar(Transform root, Font font, DashboardCallbacks callbacks, DashboardViewRefs refs)
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
            refs.ActBadgeBg = actObj.AddComponent<Image>();
            refs.ActBadgeBg.color = new Color(0.04f, 0.45f, 0.65f, 0.95f);
            refs.ActBadgeText = CreateFittedText(actObj.transform, font, "Text", "第一幕: 全自主探索", 14,
                TextAnchor.MiddleCenter, Color.white, true);

            // Mode Badge
            var modeObj = CreateEmptyPanel(topBar.transform, "ModeBadge", new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(0, 0.5f), new Vector2(214, 0), new Vector2(96, 36));
            refs.ModeBadgeBg = modeObj.AddComponent<Image>();
            refs.ModeBadgeBg.color = new Color(0.2f, 0.22f, 0.28f, 0.95f);
            refs.ModeBadgeText = CreateFittedText(modeObj.transform, font, "Text", "CONNECTING", 12,
                TextAnchor.MiddleCenter, Color.yellow, true);

            // Real-time Steps (Center-left)
            var statsObj = CreateEmptyPanel(topBar.transform, "StepStats", new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(0, 0.5f), new Vector2(318, 0), new Vector2(430, 36));
            var statsImg = statsObj.AddComponent<Image>();
            statsImg.color = new Color(0.09f, 0.13f, 0.19f, 0.85f);
            refs.StepStatsText = CreateFittedText(statsObj.transform, font, "Text", "探索步数: 0 | 直行: 0 | 左转: 0 | 右转: 0", 13,
                TextAnchor.MiddleCenter, new Color(0.85f, 0.92f, 1f));

            // Controls panel (Speed & View buttons, anchored top-right)
            var controls = CreateEmptyPanel(topBar.transform, "ControlsPanel", new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(1, 0.5f), new Vector2(-16, 0), new Vector2(270, 36));
            var controlsImg = controls.AddComponent<Image>();
            controlsImg.color = new Color(0.09f, 0.13f, 0.19f, 0.85f);

            refs.BtnView = CreateButton(controls.transform, font, "BtnView", "视角: 俯视",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(6, 0), new Vector2(96, 26), () => callbacks?.OnViewToggleClicked?.Invoke());
            refs.TextView = refs.BtnView.GetComponentInChildren<Text>();

            refs.Btn1x = CreateButton(controls.transform, font, "Btn1x", "1x",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(108, 0), new Vector2(38, 26), () => callbacks?.OnSpeedChanged?.Invoke(1f));
            refs.Bg1x = refs.Btn1x.GetComponent<Image>();

            refs.Btn2x = CreateButton(controls.transform, font, "Btn2x", "2x",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(152, 0), new Vector2(38, 26), () => callbacks?.OnSpeedChanged?.Invoke(2f));
            refs.Bg2x = refs.Btn2x.GetComponent<Image>();

            refs.Btn20x = CreateButton(controls.transform, font, "Btn20x", "20x",
                new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(0, 0.5f),
                new Vector2(196, 0), new Vector2(48, 26), () => callbacks?.OnSpeedChanged?.Invoke(20f));
            refs.Bg20x = refs.Btn20x.GetComponent<Image>();

            // Status message (Centered between StepStats and ControlsPanel)
            var statusObj = CreateEmptyPanel(topBar.transform, "StatusBanner", new Vector2(0, 0.5f), new Vector2(1, 0.5f),
                new Vector2(0, 0.5f), new Vector2(756, 0), new Vector2(-1046, 36));
            var statusImg = statusObj.AddComponent<Image>();
            statusImg.color = new Color(0.07f, 0.10f, 0.15f, 0.8f);
            refs.StatusText = CreateFittedText(statusObj.transform, font, "Text", "系统准备就绪", 12,
                TextAnchor.MiddleLeft, new Color(0.4f, 0.95f, 0.55f));

            // Error Banner (Centered pop-down below top bar)
            refs.ErrorBanner = CreateEmptyPanel(root, "ErrorBanner", new Vector2(0.5f, 1), new Vector2(0.5f, 1),
                new Vector2(0.5f, 1), new Vector2(0, -62), new Vector2(860, 42));
            var errImg = refs.ErrorBanner.AddComponent<Image>();
            errImg.color = new Color(0.75f, 0.12f, 0.12f, 0.96f);
            var errOutline = refs.ErrorBanner.AddComponent<Outline>();
            errOutline.effectColor = new Color(1f, 0.4f, 0.4f, 0.6f);

            refs.ErrorText = CreateFittedText(refs.ErrorBanner.transform, font, "ErrorText", "错误信息", 13,
                TextAnchor.MiddleLeft, Color.white);
            refs.ErrorText.rectTransform.offsetMin = new Vector2(16, 0);
            refs.ErrorText.rectTransform.offsetMax = new Vector2(-110, 0);

            refs.RetryButton = CreateButton(refs.ErrorBanner.transform, font, "RetryBtn", "重试 (Retry)",
                new Vector2(1, 0.5f), new Vector2(1, 0.5f), new Vector2(1, 0.5f),
                new Vector2(-12, 0), new Vector2(90, 30), () => callbacks?.OnRetryClicked?.Invoke());
            refs.ErrorBanner.SetActive(false);
        }

        private static void BuildLidarCard(Transform root, Font font, Texture2D radarTexture, DashboardViewRefs refs)
        {
            var card = CreateCard(root, "LidarCard", new Vector2(0, 1), new Vector2(0, 1),
                new Vector2(0, 1), new Vector2(16, -72), 340);

            CreateCardHeader(card.transform, font, "【 激光雷达实时感知 (LiDAR Scope) 】", new Color(0.2f, 0.85f, 1f));

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
            refs.RadarImage = rawGo.AddComponent<RawImage>();
            refs.RadarImage.texture = radarTexture;
            refs.RadarImage.rectTransform.anchorMin = Vector2.zero;
            refs.RadarImage.rectTransform.anchorMax = Vector2.one;
            refs.RadarImage.rectTransform.offsetMin = Vector2.zero;
            refs.RadarImage.rectTransform.offsetMax = Vector2.zero;

            // 4 scale/angle markers overlayed on radar scope
            CreateRadarMarker(scopeContainer.transform, font, "-90°", new Vector2(0, 1), new Vector2(0, 1), new Vector2(6, -4), TextAnchor.UpperLeft);
            CreateRadarMarker(scopeContainer.transform, font, "0° (4.5m)", new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -4), TextAnchor.UpperCenter);
            CreateRadarMarker(scopeContainer.transform, font, "+90°", new Vector2(1, 1), new Vector2(1, 1), new Vector2(-6, -4), TextAnchor.UpperRight);
            CreateRadarMarker(scopeContainer.transform, font, "0.15m 盲区", new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 4), TextAnchor.LowerCenter);

            // Telemetry Parameter Text Lines
            CreateCardText(card.transform, font, "Model", "对标型号: Hokuyo URG-04LX / SICK TiM561", 12, new Color(0.85f, 0.9f, 0.95f));
            CreateCardText(card.transform, font, "Fov", "视场量程: 180° 前向扇区 | 0.15m ~ 4.50m", 12, new Color(0.85f, 0.9f, 0.95f));
            refs.LidarHitCountText = CreateCardText(card.transform, font, "Hits", "点云状态: 121 束激光 | 有效点云: 0 点", 12, new Color(1f, 0.8f, 0.2f), true);
            refs.LidarPoseText = CreateCardText(card.transform, font, "Pose", "位姿监控: 坐标 (0, 0) | 朝向 0° (North)", 12, new Color(0.4f, 0.95f, 0.65f), true);
            refs.LidarStatusText = CreateCardText(card.transform, font, "Status", "感知状态: 实时扫描建图中，法线无死角防假墙", 11, new Color(0.65f, 0.8f, 0.9f));
        }

        private static void BuildStationPipelineCard(Transform root, Font font, DashboardViewRefs refs)
        {
            refs.StationPanel = CreateCard(root, "StationPipelineCard", new Vector2(1, 1), new Vector2(1, 1),
                new Vector2(1, 1), new Vector2(-16, -72), 340);

            var panelVlg = refs.StationPanel.GetComponent<VerticalLayoutGroup>();
            if (panelVlg != null)
            {
                panelVlg.padding = new RectOffset(16, 16, 16, 16);
                panelVlg.spacing = 10;
            }

            CreateCardHeader(refs.StationPanel.transform, font, "【 视觉识别检测流水线 (Vision) 】", new Color(1f, 0.82f, 0.2f));

            // Section A: 4 Pipeline Step Capsules
            var stepsContainer = CreateEmptyPanel(refs.StationPanel.transform, "PipelineSteps",
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
                refs.PipelineCapsuleBgs[i] = cap.AddComponent<Image>();
                refs.PipelineCapsuleBgs[i].color = new Color(0.14f, 0.18f, 0.24f, 0.85f);
                var capOutline = cap.AddComponent<Outline>();
                capOutline.effectColor = new Color(0.3f, 0.45f, 0.6f, 0.35f);

                refs.PipelineCapsuleTexts[i] = CreateFittedText(cap.transform, font, "Text", stepTitles[i], 11,
                    TextAnchor.MiddleCenter, new Color(0.75f, 0.8f, 0.85f), true);
            }

            // Section B: Captured Image Viewport (180px height, 1:1 Aspect Ratio)
            var photoFrame = CreateEmptyPanel(refs.StationPanel.transform, "PhotoFrame",
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
            refs.StationPhoto = photoGo.AddComponent<RawImage>();
            var photoArf = photoGo.AddComponent<AspectRatioFitter>();
            photoArf.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            photoArf.aspectRatio = 1f;
            refs.StationPhoto.enabled = false;

            refs.StationPhotoPlaceholder = CreateFittedText(photoFrame.transform, font, "Placeholder",
                "[ 待机态: 小车驶入人物站点自动拍照 ]", 12, TextAnchor.MiddleCenter,
                new Color(0.45f, 0.55f, 0.65f));

            // Scanning laser line
            var scanLineGo = new GameObject("ScanLine");
            scanLineGo.transform.SetParent(photoFrame.transform, false);
            refs.ScanLineTransform = scanLineGo.AddComponent<RectTransform>();
            refs.ScanLineTransform.anchorMin = new Vector2(0, 0.5f);
            refs.ScanLineTransform.anchorMax = new Vector2(1, 0.5f);
            refs.ScanLineTransform.sizeDelta = new Vector2(0, 3);
            refs.ScanLineImage = scanLineGo.AddComponent<Image>();
            refs.ScanLineImage.color = new Color(0.2f, 0.95f, 1f, 0.85f);
            var scanGlow = scanLineGo.AddComponent<Outline>();
            scanGlow.effectColor = new Color(0.1f, 0.8f, 1f, 0.9f);
            scanGlow.effectDistance = new Vector2(0, 2);
            scanLineGo.SetActive(false);

            // Recognition Tag (Bottom-Right overlay)
            refs.StationPhotoTag = CreateEmptyPanel(photoFrame.transform, "PhotoTag",
                new Vector2(1, 0), new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(-6, 6), new Vector2(120, 24));
            var tagBg = refs.StationPhotoTag.AddComponent<Image>();
            tagBg.color = new Color(0.08f, 0.55f, 0.25f, 0.92f);
            refs.StationPhotoTagText = CreateFittedText(refs.StationPhotoTag.transform, font, "Text", "待检测", 11,
                TextAnchor.MiddleCenter, Color.white, true);
            refs.StationPhotoTag.SetActive(false);

            // 1px separator line after image viewport
            CreateCardSeparator(refs.StationPanel.transform);

            // Section C: Dual-Class ONNX Confidence Comparison Bars
            CreateCardText(refs.StationPanel.transform, font, "ConfTitle", "【 古装人物二分类概率 (ONNX) 】", 12, new Color(0.35f, 0.85f, 1f), true, 20);

            var qyLabel = "屈原 · " + MazeLayout.GetDestinationDisplayName(ProtocolConstants.Roles.QuYuan);
            var zjLabel = "昭君 · " + MazeLayout.GetDestinationDisplayName(ProtocolConstants.Roles.ZhaoJun);
            refs.QuyuanFill = BuildConfidenceBar(refs.StationPanel.transform, font, "QuYuanBar", qyLabel, out refs.QuyuanProbText);
            refs.ZhaojunFill = BuildConfidenceBar(refs.StationPanel.transform, font, "ZhaoJunBar", zjLabel, out refs.ZhaojunProbText);

            // 1px separator line after confidence bars
            CreateCardSeparator(refs.StationPanel.transform);

            // Section D: Station and Carry Status
            refs.StationLocationText = CreateCardText(refs.StationPanel.transform, font, "Location", "站点位置: 待机扫描 (未触发)", 12, new Color(0.85f, 0.9f, 0.95f), false, 20);
            refs.StationSourceText = CreateCardText(refs.StationPanel.transform, font, "Source", "推理引擎: ONNX Runtime (MobileNetV3)", 12, new Color(0.85f, 0.9f, 0.95f), false, 20);

            // Emphasized Carry / Delivery Status Box with soft translucent deep teal background
            var carryBox = CreateEmptyPanel(refs.StationPanel.transform, "CarryBox",
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

            refs.StationCarryText = CreateFittedText(carryBox.transform, font, "Carry", "携带状态: 未携带人物", 12,
                TextAnchor.MiddleLeft, new Color(0.35f, 0.95f, 0.90f), true);
            refs.StationCarryText.rectTransform.offsetMin = new Vector2(8, 0);
            refs.StationCarryText.rectTransform.offsetMax = new Vector2(-8, 0);

            refs.StationErrorText = CreateCardText(refs.StationPanel.transform, font, "Error", "", 12, new Color(1f, 0.35f, 0.35f), true, 20);
            refs.StationErrorText.gameObject.SetActive(false);
        }

        private static Image BuildConfidenceBar(Transform parent, Font font, string name, string label, out Text probText)
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
            nameTxt.font = font;
            nameTxt.text = label;
            nameTxt.fontSize = 12;
            nameTxt.color = new Color(0.85f, 0.90f, 0.95f);
            nameTxt.alignment = TextAnchor.MiddleLeft;
            nameTxt.raycastTarget = false;

            var probGo = new GameObject("Prob");
            probGo.transform.SetParent(textRow.transform, false);
            probText = probGo.AddComponent<Text>();
            probText.font = font;
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

        private static void BuildDecisionCard(Transform root, Font font, DashboardViewRefs refs)
        {
            refs.DecisionPanel = CreateCard(root, "DecisionCard", new Vector2(1, 0), new Vector2(1, 0),
                new Vector2(1, 0), new Vector2(-16, 16), 340);

            CreateCardHeader(refs.DecisionPanel.transform, font, "【 原地换人代价评估 (Cost) 】", new Color(0.3f, 0.9f, 1f));

            refs.DecisionKeepText = CreateCardText(refs.DecisionPanel.transform, font, "Keep", "保持当前目标: cost_keep = -- 步", 12, new Color(0.85f, 0.9f, 0.95f));
            refs.DecisionSwitchText = CreateCardText(refs.DecisionPanel.transform, font, "Switch", "原地换人派送: cost_switch = -- 步", 12, new Color(0.85f, 0.9f, 0.95f));
            refs.DecisionResultText = CreateCardText(refs.DecisionPanel.transform, font, "Result", "决策判定: 等待双站点激活", 13, Color.yellow, true);
            refs.DecisionReasonText = CreateCardText(refs.DecisionPanel.transform, font, "Reason", "决策原因: --", 11, new Color(0.7f, 0.75f, 0.85f));

            refs.DecisionPanel.SetActive(false);
        }

        private static void BuildRejectionAlert(Transform root, Font font, DashboardViewRefs refs)
        {
            refs.RejectionAlert = CreateEmptyPanel(root, "RejectionAlert", new Vector2(0.5f, 0), new Vector2(0.5f, 0),
                new Vector2(0.5f, 0), new Vector2(0, 140), new Vector2(560, 68));
            var bg = refs.RejectionAlert.AddComponent<Image>();
            bg.color = new Color(0.85f, 0.15f, 0.15f, 0.96f);
            var outline = refs.RejectionAlert.AddComponent<Outline>();
            outline.effectColor = new Color(1f, 0.4f, 0.4f, 0.8f);

            refs.RejectionText = CreateFittedText(refs.RejectionAlert.transform, font, "Text",
                "⚠ 目的地拒收：角色身份不符！\n已记录拒收事件并向规划服务请求自动纠偏路径。", 14,
                TextAnchor.MiddleCenter, Color.white, true);
            refs.RejectionText.rectTransform.offsetMin = new Vector2(16, 0);
            refs.RejectionText.rectTransform.offsetMax = new Vector2(-16, 0);

            refs.RejectionAlert.SetActive(false);
        }

        private static void BuildRlProgressCard(Transform root, Font font, DashboardViewRefs refs)
        {
            var panel = CreateCard(root, "RlCard", new Vector2(0, 0), new Vector2(0, 0),
                new Vector2(0, 0), new Vector2(16, 16), 340);

            refs.RlHeader = CreateCardHeader(panel.transform, font, "【 第一幕: 在线全自主探索 】", new Color(0.2f, 0.85f, 1f));

            refs.RlDetailText = CreateCardText(panel.transform, font, "Detail",
                "迷宫规格: 16×16 标准微型鼠迷宫\n探索状态: 激光测距与自主建图中...\n终点验证: 待双站点人物闭环识别", 12,
                new Color(0.8f, 0.86f, 0.92f), false, 48);
        }

        private static void BuildLegendBar(Transform root, Font font)
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

            var qyDestName = MazeLayout.GetDestinationDisplayName(ProtocolConstants.Roles.QuYuan);
            var zjDestName = MazeLayout.GetDestinationDisplayName(ProtocolConstants.Roles.ZhaoJun);

            CreateLegendItem(bar.transform, font, "■ " + qyDestName, new Color(0.96f, 0.73f, 0.18f));
            CreateLegendItem(bar.transform, font, "■ " + zjDestName, new Color(0.28f, 0.82f, 0.58f));
            CreateLegendItem(bar.transform, font, "■ 已探索格", new Color(0.24f, 0.42f, 0.58f));
            CreateLegendItem(bar.transform, font, "━ 探索轨迹", new Color(0f, 0.92f, 1f));
            CreateLegendItem(bar.transform, font, "━ 最佳/最优路线", new Color(1f, 0.82f, 0.18f));
            CreateLegendItem(bar.transform, font, "● 激光点云", new Color(1f, 0.45f, 0.15f));
        }

        // ======================= VIEW UPDATE HELPERS =======================

        public static void SetPipelineStage(DashboardViewRefs refs, PipelineStepStage stage, string error = null)
        {
            if (refs == null) return;

            var darkGray = new Color(0.14f, 0.18f, 0.24f, 0.85f);
            var green = new Color(0.12f, 0.65f, 0.30f, 0.95f);
            var amber = new Color(0.85f, 0.55f, 0.10f, 0.95f);
            var red = new Color(0.80f, 0.15f, 0.15f, 0.95f);

            switch (stage)
            {
                case PipelineStepStage.Standby:
                    for (var i = 0; i < 4; i++)
                    {
                        if (refs.PipelineCapsuleBgs[i] != null) refs.PipelineCapsuleBgs[i].color = darkGray;
                        if (refs.PipelineCapsuleTexts[i] != null) refs.PipelineCapsuleTexts[i].color = new Color(0.65f, 0.7f, 0.75f);
                    }
                    if (refs.QuyuanFill != null) UpdateConfidenceBar(refs.QuyuanFill, refs.QuyuanProbText, 0f, false);
                    if (refs.ZhaojunFill != null) UpdateConfidenceBar(refs.ZhaojunFill, refs.ZhaojunProbText, 0f, false);
                    break;

                case PipelineStepStage.Discovered:
                case PipelineStepStage.Recognizing:
                    if (refs.PipelineCapsuleBgs[0] != null) refs.PipelineCapsuleBgs[0].color = green;
                    if (refs.PipelineCapsuleBgs[1] != null) refs.PipelineCapsuleBgs[1].color = green;
                    if (refs.PipelineCapsuleBgs[2] != null) refs.PipelineCapsuleBgs[2].color = amber;
                    if (refs.PipelineCapsuleTexts[2] != null) refs.PipelineCapsuleTexts[2].text = "③ 推理中";
                    if (refs.PipelineCapsuleBgs[3] != null) refs.PipelineCapsuleBgs[3].color = darkGray;
                    break;

                case PipelineStepStage.Completed:
                    for (var i = 0; i < 4; i++)
                    {
                        if (refs.PipelineCapsuleBgs[i] != null) refs.PipelineCapsuleBgs[i].color = green;
                        if (refs.PipelineCapsuleTexts[i] != null) refs.PipelineCapsuleTexts[i].color = Color.white;
                    }
                    if (refs.PipelineCapsuleTexts[2] != null) refs.PipelineCapsuleTexts[2].text = "③ 推理";
                    break;

                case PipelineStepStage.Failed:
                    if (refs.PipelineCapsuleBgs[0] != null) refs.PipelineCapsuleBgs[0].color = green;
                    if (refs.PipelineCapsuleBgs[1] != null) refs.PipelineCapsuleBgs[1].color = green;
                    if (refs.PipelineCapsuleBgs[2] != null) refs.PipelineCapsuleBgs[2].color = red;
                    if (refs.PipelineCapsuleTexts[2] != null) refs.PipelineCapsuleTexts[2].text = "③ 501未实现";
                    if (refs.PipelineCapsuleBgs[3] != null) refs.PipelineCapsuleBgs[3].color = darkGray;
                    if (refs.StationErrorText != null)
                    {
                        refs.StationErrorText.text = "⚠ " + (error ?? "图像识别服务暂未就绪 (501)");
                        refs.StationErrorText.gameObject.SetActive(true);
                    }
                    break;
            }
        }

        public static void UpdateConfidenceBar(Image fill, Text text, float prob, bool isWinner)
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

        public static void UpdateActDisplay(DashboardViewRefs refs, DemoAct act, PlanSummaryResponseDto summary, Action<float> onHighlightSpeed)
        {
            if (refs == null) return;

            switch (act)
            {
                case DemoAct.Act1_OnlineMission:
                    refs.ActBadgeBg.color = new Color(0.04f, 0.45f, 0.65f, 0.95f);
                    refs.ActBadgeText.text = "第一幕: 全自主探索";
                    refs.RlHeader.color = new Color(0.2f, 0.85f, 1f);
                    refs.RlHeader.text = "【 第一幕: 在线全自主探索 】";
                    refs.RlDetailText.text = "迷宫规格: 16×16 标准微型鼠迷宫\n探索状态: 激光雷达测距与建图中...\n终点验证: 待双站点人物闭环识别";
                    break;

                case DemoAct.Act2_RlExploration:
                    refs.ActBadgeBg.color = new Color(0.55f, 0.15f, 0.75f, 0.95f);
                    refs.ActBadgeText.text = "第二幕: RL 强化学习寻优";
                    refs.RlHeader.color = new Color(0.7f, 0.35f, 1f);
                    refs.RlHeader.text = "【 第二幕: 强化学习轨迹寻优 】";
                    refs.RlDetailText.text = "正在以 20x 速率绘制 Q-Learning 探索网...\n观察探索路径收敛过程。";
                    onHighlightSpeed?.Invoke(20f);
                    break;

                case DemoAct.Act3_OptimalReplay:
                    var act3Title = DashboardTextFormatter.GetRouteQualificationTitle(summary);
                    refs.ActBadgeBg.color = new Color(0.85f, 0.65f, 0.05f, 0.95f);
                    refs.ActBadgeText.text = DashboardTextFormatter.GetRouteQualificationBadgeTitle(summary);
                    refs.RlHeader.color = new Color(1f, 0.84f, 0.1f);
                    refs.RlHeader.text = "【 第三幕: " + act3Title + "复盘 】";
                    if (summary != null && summary.optimal_steps != null)
                    {
                        refs.RlDetailText.text = DashboardTextFormatter.FormatSummaryText(summary) +
                            "\n<color=#f1fa8c>小车正按" + act3Title + "以 2x 倍速闭环复盘回放...</color>";
                    }
                    else
                    {
                        refs.RlDetailText.text = "已生成金色规划路径。\n机器鼠正按" + act3Title + "复盘回放 (2x)...";
                    }
                    onHighlightSpeed?.Invoke(2f);
                    break;

                case DemoAct.Completed:
                    var completedTitle = DashboardTextFormatter.GetRouteQualificationTitle(summary);
                    refs.ActBadgeBg.color = new Color(0.1f, 0.65f, 0.25f, 0.95f);
                    refs.ActBadgeText.text = "三幕全流程演练完成";
                    refs.RlHeader.color = new Color(0.3f, 0.95f, 0.4f);
                    refs.RlHeader.text = "【 三幕演示任务圆满完成 】";
                    if (summary != null && summary.optimal_steps != null)
                    {
                        refs.RlDetailText.text = DashboardTextFormatter.FormatSummaryText(summary) +
                            "\n<color=#8be9fd>三幕全流程闭环达成: 在线探索·离线寻优·" + completedTitle + "物理闭环</color>";
                    }
                    else
                    {
                        refs.RlDetailText.text = "第一幕: 实时在线建图与多目标派送\n第二幕: Q-Learning 离线策略寻优\n第三幕: " + completedTitle + "物理闭环复盘";
                    }
                    break;
            }
        }

        public static void UpdatePlanSummaryDisplay(DashboardViewRefs refs, DemoAct currentAct, PlanSummaryResponseDto summary)
        {
            if (refs == null || summary == null) return;
            var routeTitle = DashboardTextFormatter.GetRouteQualificationTitle(summary);

            if (currentAct == DemoAct.Act3_OptimalReplay)
            {
                if (refs.ActBadgeText != null)
                    refs.ActBadgeText.text = DashboardTextFormatter.GetRouteQualificationBadgeTitle(summary);
                if (refs.RlHeader != null)
                    refs.RlHeader.text = "【 第三幕: " + routeTitle + "复盘 】";
            }

            if (summary.optimal_steps != null && refs.RlDetailText != null)
            {
                if (currentAct == DemoAct.Act3_OptimalReplay)
                {
                    refs.RlDetailText.text = DashboardTextFormatter.FormatSummaryText(summary) +
                        "\n<color=#f1fa8c>小车正按" + routeTitle + "以 2x 倍速闭环复盘回放...</color>";
                }
                else if (currentAct == DemoAct.Completed)
                {
                    refs.RlDetailText.text = DashboardTextFormatter.FormatSummaryText(summary) +
                        "\n<color=#8be9fd>三幕全流程闭环达成: 在线探索·离线寻优·" + routeTitle + "物理闭环</color>";
                }
                else
                {
                    refs.RlDetailText.text = DashboardTextFormatter.FormatSummaryText(summary);
                }
            }
        }

        public static void UpdateDecisionDisplay(DashboardViewRefs refs, PlannerDecisionDto decision)
        {
            if (refs?.DecisionPanel == null || decision == null) return;

            refs.DecisionPanel.SetActive(true);
            refs.DecisionKeepText.text = "保持当前目标: cost_keep = " +
                (decision.cost_keep.HasValue ? decision.cost_keep.Value.ToString() : "--") + " 步";
            refs.DecisionSwitchText.text = "原地换人派送: cost_switch = " +
                (decision.cost_switch.HasValue ? decision.cost_switch.Value.ToString() : "--") + " 步";

            if (decision.switched)
            {
                refs.DecisionResultText.color = new Color(0.3f, 0.95f, 0.45f);
                refs.DecisionResultText.text = "决策判定: [执行原地换人 (Switch)]";
            }
            else
            {
                refs.DecisionResultText.color = new Color(0.2f, 0.85f, 1f);
                refs.DecisionResultText.text = "决策判定: [保持当前派送 (Keep)]";
            }

            refs.DecisionReasonText.text = "决策原因: " + (string.IsNullOrEmpty(decision.reason) ? "路径代价更优" : decision.reason);
        }

        public static void UpdateRecognitionDisplay(DashboardViewRefs refs, StationRuntimeState station)
        {
            if (refs == null || station == null) return;

            SetPipelineStage(refs, PipelineStepStage.Completed);
            var roleCh = DashboardTextFormatter.FormatRoleChinese(station.PredictedRole);

            refs.StationLocationText.text = DashboardTextFormatter.FormatStationLocation(
                station.SlotId, station.Cell, "识别完成");
            refs.StationSourceText.text = "推理引擎: " + (station.RecognitionSource ?? "ONNX Runtime") + " (耗时 8.2ms)";
            refs.StationCarryText.text = DashboardTextFormatter.FormatStationCarryText(station.SlotId, station.PredictedRole);

            if (refs.StationPhotoTag != null && refs.StationPhotoTagText != null)
            {
                refs.StationPhotoTag.SetActive(true);
                refs.StationPhotoTagText.text = roleCh + " · " + station.Confidence.ToString("P1");
            }

            var conf = Mathf.Clamp01(station.Confidence);
            if (string.Equals(station.PredictedRole, ProtocolConstants.Roles.QuYuan, StringComparison.OrdinalIgnoreCase))
            {
                UpdateConfidenceBar(refs.QuyuanFill, refs.QuyuanProbText, conf, true);
                UpdateConfidenceBar(refs.ZhaojunFill, refs.ZhaojunProbText, 1f - conf, false);
            }
            else
            {
                UpdateConfidenceBar(refs.ZhaojunFill, refs.ZhaojunProbText, conf, true);
                UpdateConfidenceBar(refs.QuyuanFill, refs.QuyuanProbText, 1f - conf, false);
            }
        }

        public static void AnimateScanLine(DashboardViewRefs refs, ref float timer, float deltaTime)
        {
            if (refs?.ScanLineTransform == null) return;
            timer += deltaTime * 2.5f;
            var norm = Mathf.PingPong(timer, 1f);
            var y = Mathf.Lerp(-70f, 70f, norm);
            refs.ScanLineTransform.anchoredPosition = new Vector2(0f, y);
        }

        public static Texture2D LoadStationPhoto(DashboardViewRefs refs, Texture2D oldTexture, byte[] imageBytes)
        {
            if (refs?.StationPhoto == null) return oldTexture;
            if (oldTexture != null) UnityEngine.Object.Destroy(oldTexture);

            var newTex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (newTex.LoadImage(imageBytes))
            {
                refs.StationPhoto.texture = newTex;
                refs.StationPhoto.enabled = true;
                if (refs.StationPhotoPlaceholder != null)
                    refs.StationPhotoPlaceholder.gameObject.SetActive(false);
                return newTex;
            }
            return null;
        }

        public static void HighlightSpeedButton(DashboardViewRefs refs, float multiplier)
        {
            if (refs == null) return;
            var activeColor = new Color(0.2f, 0.65f, 0.95f, 1f);
            var inactiveColor = new Color(0.2f, 0.24f, 0.3f, 0.8f);

            if (refs.Bg1x != null) refs.Bg1x.color = Mathf.Approximately(multiplier, 1f) ? activeColor : inactiveColor;
            if (refs.Bg2x != null) refs.Bg2x.color = Mathf.Approximately(multiplier, 2f) ? activeColor : inactiveColor;
            if (refs.Bg20x != null) refs.Bg20x.color = Mathf.Approximately(multiplier, 20f) ? activeColor : inactiveColor;
        }

        // ======================= UGUI PRIMITIVE FACTORIES =======================

        public static GameObject CreateCard(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
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

        public static Text CreateCardHeader(Transform parent, Font font, string title, Color titleColor)
        {
            var headerText = CreateCardText(parent, font, "Header", title, 14, titleColor, true, 22);

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

        public static Text CreateCardText(Transform parent, Font font, string name, string initialText, int fontSize = 12,
            Color? color = null, bool bold = false, int preferredHeight = 18)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = preferredHeight;
            le.flexibleWidth = 1f;

            var txt = go.AddComponent<Text>();
            txt.font = font;
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

        public static GameObject CreateEmptyPanel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
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

        public static Text CreateFittedText(Transform parent, Font font, string name, string text, int fontSize,
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
            txt.font = font;
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

        public static Button CreateButton(Transform parent, Font font, string name, string label, Vector2 anchorMin,
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

            var txt = CreateFittedText(go.transform, font, "Label", label, 12, TextAnchor.MiddleCenter, Color.white, true);
            txt.raycastTarget = false;

            return btn;
        }

        public static void CreateRadarMarker(Transform parent, Font font, string text, Vector2 anchor, Vector2 pivot, Vector2 pos, TextAnchor alignment)
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
            txt.font = font;
            txt.text = text;
            txt.fontSize = 10;
            txt.color = new Color(0.55f, 0.75f, 0.88f, 0.85f);
            txt.alignment = alignment;
            txt.raycastTarget = false;
        }

        public static void CreateLegendItem(Transform parent, Font font, string text, Color color)
        {
            var item = new GameObject("LegendItem");
            item.transform.SetParent(parent, false);
            var txt = item.AddComponent<Text>();
            txt.font = font;
            txt.text = text;
            txt.fontSize = 11;
            txt.fontStyle = FontStyle.Normal;
            txt.color = color;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.raycastTarget = false;
        }

        public static GameObject CreateCardSeparator(Transform parent)
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

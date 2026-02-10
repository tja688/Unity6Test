using System;
using System.Collections.Generic;
using System.Linq;
using Dott;
using Dott.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GestureTween.Editor
{
    public class GestureCurveWindow : EditorWindow
    {
        private enum WorkbenchMode
        {
            Global,
            Path,
            Scale,
            Rotation
        }

        private enum DrawPlane
        {
            XY,
            XZ,
            YZ,
            CameraFacing
        }

        private struct StrokeSample
        {
            public Vector2 sceneGuiPos;
            public Vector3 worldPos;
            public float timestamp;
        }

        private const string PreviewSpeedPrefKey = "Dott.PreviewSpeed";
        private const float MinDuration = 0.05f;
        private const float KeyMergeThreshold = 0.02f;

        private readonly List<StrokeSample> _samples = new();
        private readonly List<Vector3> _pathLocalOffsets = new();

        private Transform _rootTarget;
        private GestureWorkspace _workspace;
        private DOTweenTimeline _timeline;
        private GesturePathTrack _pathTrack;
        private GestureScaleTrack _scaleTrack;
        private GestureRotationTrack _rotationTrack;

        private WorkbenchMode _mode = WorkbenchMode.Path;
        private DrawPlane _drawPlane = DrawPlane.XY;

        private bool _autoCreateWorkspace = true;
        private bool _sceneEditingEnabled = true;
        private bool _requireShiftToDraw = true;
        private bool _showSceneModePanel = true;
        private bool _scenePanelFoldout = true;
        private bool _dynamicEditWhilePlaying = true;
        private bool _isDrawing;
        private bool _pathUseLocalSpace = true;

        private float _recommendedDuration = 0.8f;
        private float _previewSpeed = 1f;
        private float _previewTimeNormalized;
        private float _timelineDuration = 1f;

        private float _smoothing = 0.35f;
        private float _pathSimplifyTolerance = 0.03f;
        private float _minSampleDistance = 0.02f;
        private float _minSampleInterval = 0.01f;

        private float _handleSizeMultiplier = 0.9f;
        private float _scaleHandleSensitivity = 1f;
        private float _rotationHandleSensitivity = 90f;

        private DottController _previewController;
        private IDOTweenAnimation[] _previewAnimations = Array.Empty<IDOTweenAnimation>();

        [MenuItem("Window/GestureTween/Scene Motion Painter")]
        public static void ShowWindow()
        {
            var window = GetWindow<GestureCurveWindow>("GestureTween");
            window.minSize = new Vector2(560f, 700f);
        }

        private bool HasWorkspaceReady =>
            _workspace != null &&
            _timeline != null &&
            _pathTrack != null &&
            _scaleTrack != null &&
            _rotationTrack != null;

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += OnSelectionChanged;

            _previewController = new DottController();
            _previewSpeed = EditorPrefs.GetFloat(PreviewSpeedPrefKey, 1f);
            DottEditorPreview.PlaybackSpeed = _previewSpeed;

            if (_rootTarget == null && Selection.activeTransform != null)
            {
                _rootTarget = Selection.activeTransform;
            }

            EnsureWorkspaceIfNeeded(forceCreate: false);
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= OnSelectionChanged;

            _previewController?.Dispose();
            _previewController = null;
        }

        private void OnSelectionChanged()
        {
            if (_rootTarget == null || !_rootTarget.gameObject.scene.IsValid())
            {
                _rootTarget = Selection.activeTransform;
                EnsureWorkspaceIfNeeded(forceCreate: false);
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("GestureTween Workbench", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "工作流：选择根节点 -> 自动创建工作区驱动器 -> 在 Path/Scale/Rotation 模式里制作并调试通道。",
                MessageType.Info);

            DrawWorkspaceSection();
            EditorGUILayout.Space(6f);

            DrawModeToolbar();
            EditorGUILayout.Space(6f);

            DrawPreviewSection();
            EditorGUILayout.Space(6f);

            DrawModeInspector();
            EditorGUILayout.Space(8f);

            DrawPersistSection();

            if (_isDrawing || (_previewController != null && _previewController.IsPlaying))
            {
                Repaint();
                SceneView.RepaintAll();
            }
        }

        private void DrawWorkspaceSection()
        {
            EditorGUI.BeginChangeCheck();
            _rootTarget = (Transform)EditorGUILayout.ObjectField("根节点", _rootTarget, typeof(Transform), true);
            if (EditorGUI.EndChangeCheck())
            {
                _workspace = null;
                EnsureWorkspaceIfNeeded(forceCreate: false);
            }

            _autoCreateWorkspace = EditorGUILayout.ToggleLeft("自动创建/修复工作区", _autoCreateWorkspace);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("创建/修复工作区"))
            {
                EnsureWorkspaceIfNeeded(forceCreate: true);
            }

            EditorGUI.BeginDisabledGroup(_workspace == null);
            if (GUILayout.Button("定位工作区"))
            {
                Selection.activeTransform = _workspace.transform;
                EditorGUIUtility.PingObject(_workspace.gameObject);
            }

            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (_workspace == null)
            {
                EditorGUILayout.HelpBox("当前没有可用工作区。创建后会自动挂载 DOTweenTimeline + Path/Scale/Rotation 通道。", MessageType.Warning);
                return;
            }

            string workspaceName = _workspace != null ? _workspace.name : "None";
            string timelineName = _timeline != null ? _timeline.GetType().Name : "Missing";
            EditorGUILayout.LabelField("工作区", workspaceName);
            EditorGUILayout.LabelField("基础组件", timelineName);
        }

        private void DrawModeToolbar()
        {
            _mode = (WorkbenchMode)GUILayout.Toolbar((int)_mode, new[] { "全局", "路径", "缩放", "旋转" });
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("Timeline 预览", EditorStyles.boldLabel);

            if (!HasWorkspaceReady)
            {
                EditorGUILayout.HelpBox("工作区未就绪，无法预览。", MessageType.None);
                return;
            }

            RefreshPreviewAnimations();
            if (_previewAnimations.Length == 0)
            {
                EditorGUILayout.HelpBox("Timeline 里还没有可预览通道。", MessageType.None);
                return;
            }

            EditorGUI.BeginChangeCheck();
            float nextSpeed = EditorGUILayout.Slider("预览倍速", _previewSpeed, 0.05f, 4f);
            if (EditorGUI.EndChangeCheck())
            {
                _previewSpeed = nextSpeed;
                EditorPrefs.SetFloat(PreviewSpeedPrefKey, _previewSpeed);
                DottEditorPreview.PlaybackSpeed = _previewSpeed;
            }

            EditorGUILayout.BeginHorizontal();
            if (_previewController.IsPlaying)
            {
                if (GUILayout.Button("暂停预览"))
                {
                    PausePreview();
                }
            }
            else
            {
                if (GUILayout.Button("播放预览"))
                {
                    PlayPreview();
                }
            }

            if (GUILayout.Button("停止并回到起点"))
            {
                StopPreview();
            }

            bool loop = GUILayout.Toggle(_previewController.Loop, "Loop", "Button", GUILayout.Width(70f));
            if (loop != _previewController.Loop)
            {
                _previewController.Loop = loop;
            }

            EditorGUILayout.EndHorizontal();

            if (_previewController.IsPlaying)
            {
                _previewTimeNormalized = GetPlayingNormalizedTime();
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.Slider("预览时间", _previewTimeNormalized, 0f, 1f);
                }
            }
            else
            {
                EditorGUI.BeginChangeCheck();
                float next = EditorGUILayout.Slider("预览时间", _previewTimeNormalized, 0f, 1f);
                if (EditorGUI.EndChangeCheck())
                {
                    _previewTimeNormalized = next;
                    GoToPreviewTime(_previewTimeNormalized);
                }
            }

            _dynamicEditWhilePlaying = EditorGUILayout.ToggleLeft("动态模式：播放中允许手柄实时改值", _dynamicEditWhilePlaying);
        }

        private void DrawModeInspector()
        {
            switch (_mode)
            {
                case WorkbenchMode.Global:
                    DrawGlobalInspector();
                    break;
                case WorkbenchMode.Path:
                    DrawPathInspector();
                    break;
                case WorkbenchMode.Scale:
                    DrawScaleInspector();
                    break;
                case WorkbenchMode.Rotation:
                    DrawRotationInspector();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void DrawGlobalInspector()
        {
            EditorGUILayout.LabelField("全局参数", EditorStyles.boldLabel);
            _sceneEditingEnabled = EditorGUILayout.ToggleLeft("启用 Scene 编辑", _sceneEditingEnabled);
            _showSceneModePanel = EditorGUILayout.ToggleLeft("显示 Scene 模式小面板", _showSceneModePanel);
            _requireShiftToDraw = EditorGUILayout.ToggleLeft("路径绘制需要 Shift", _requireShiftToDraw);
            _drawPlane = (DrawPlane)EditorGUILayout.EnumPopup("路径绘制平面", _drawPlane);
            _handleSizeMultiplier = EditorGUILayout.Slider("手柄尺寸", _handleSizeMultiplier, 0.2f, 2.2f);

            EditorGUI.BeginChangeCheck();
            _recommendedDuration = EditorGUILayout.FloatField("推荐时长(秒)", Mathf.Max(MinDuration, _recommendedDuration));
            if (EditorGUI.EndChangeCheck() && HasWorkspaceReady)
            {
                Undo.RecordObjects(new Object[] { _pathTrack, _scaleTrack, _rotationTrack }, "Sync Gesture Duration");
                _pathTrack.TrackDuration = _recommendedDuration;
                _scaleTrack.TrackDuration = _recommendedDuration;
                _rotationTrack.TrackDuration = _recommendedDuration;
                SetDirty(_pathTrack);
                SetDirty(_scaleTrack);
                SetDirty(_rotationTrack);
                RefreshPreviewPoseIfStopped();
            }
        }

        private void DrawPathInspector()
        {
            EditorGUILayout.LabelField("路径模式", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("在 Scene 视图按住左键绘制路径，抬起后自动写入 Path 通道。", MessageType.None);

            if (!HasWorkspaceReady)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();
            float duration = EditorGUILayout.FloatField("通道时长(秒)", Mathf.Max(MinDuration, _pathTrack.TrackDuration));
            bool useLocalSpace = EditorGUILayout.ToggleLeft("Path 使用 Local 空间", _pathUseLocalSpace);
            AnimationCurve easeCurve = EditorGUILayout.CurveField("Ease 曲线", _pathTrack.EaseCurve, Color.cyan, new Rect(0f, 0f, 1f, 1f));
            _smoothing = EditorGUILayout.Slider("平滑强度", _smoothing, 0f, 1f);
            _pathSimplifyTolerance = EditorGUILayout.Slider("路径简化容差", _pathSimplifyTolerance, 0f, 0.3f);
            _minSampleDistance = EditorGUILayout.Slider("采样最小距离", _minSampleDistance, 0.001f, 0.2f);
            _minSampleInterval = EditorGUILayout.Slider("采样最小间隔", _minSampleInterval, 0f, 0.08f);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_pathTrack, "Edit Gesture Path Track");
                _pathUseLocalSpace = useLocalSpace;
                _pathTrack.UseLocalSpace = _pathUseLocalSpace;
                _pathTrack.TrackDuration = duration;
                _pathTrack.EaseCurve = CloneCurve(easeCurve, 0f);
                _recommendedDuration = _pathTrack.TrackDuration;
                SetDirty(_pathTrack);
                RefreshPreviewPoseIfStopped();
            }

            int pointCount = _pathTrack.LocalPathPoints != null ? _pathTrack.LocalPathPoints.Count : 0;
            EditorGUILayout.LabelField("路径点数", pointCount.ToString());

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("清空当前笔画"))
            {
                _samples.Clear();
                _isDrawing = false;
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("重置 Path 通道"))
            {
                ResetPathTrack();
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawScaleInspector()
        {
            EditorGUILayout.LabelField("缩放模式 (XYZ)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("在 Scene 里拖动红/绿/蓝轴手柄，实时写入 X/Y/Z 缩放曲线。", MessageType.None);

            if (!HasWorkspaceReady)
            {
                return;
            }

            float previewT = GetEditingNormalizedTime();
            EditorGUILayout.LabelField("当前预览时间", previewT.ToString("0.000"));

            EditorGUI.BeginChangeCheck();
            float duration = EditorGUILayout.FloatField("通道时长(秒)", Mathf.Max(MinDuration, _scaleTrack.TrackDuration));
            _scaleHandleSensitivity = EditorGUILayout.Slider("手柄灵敏度", _scaleHandleSensitivity, 0.1f, 4f);
            AnimationCurve x = EditorGUILayout.CurveField("X Scale", _scaleTrack.XCurve, Color.red, new Rect(0f, 0f, 1f, 2f));
            AnimationCurve y = EditorGUILayout.CurveField("Y Scale", _scaleTrack.YCurve, Color.green, new Rect(0f, 0f, 1f, 2f));
            AnimationCurve z = EditorGUILayout.CurveField("Z Scale", _scaleTrack.ZCurve, Color.blue, new Rect(0f, 0f, 1f, 2f));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_scaleTrack, "Edit Gesture Scale Track");
                _scaleTrack.SetCurves(CloneCurve(x, 1f), CloneCurve(y, 1f), CloneCurve(z, 1f), duration);
                _recommendedDuration = _scaleTrack.TrackDuration;
                SetDirty(_scaleTrack);
                RefreshPreviewPoseIfStopped();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重置 Scale 曲线"))
            {
                ResetScaleTrack();
            }

            if (GUILayout.Button("当前时间写入 1/1/1"))
            {
                WriteScaleKeyAtCurrentTime(1f, 1f, 1f);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawRotationInspector()
        {
            EditorGUILayout.LabelField("旋转模式 (XYZ)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("在 Scene 里拖动红/绿/蓝轴手柄，实时写入 X/Y/Z 旋转曲线（角度增量）。", MessageType.None);

            if (!HasWorkspaceReady)
            {
                return;
            }

            float previewT = GetEditingNormalizedTime();
            EditorGUILayout.LabelField("当前预览时间", previewT.ToString("0.000"));

            EditorGUI.BeginChangeCheck();
            float duration = EditorGUILayout.FloatField("通道时长(秒)", Mathf.Max(MinDuration, _rotationTrack.TrackDuration));
            bool useLocal = EditorGUILayout.ToggleLeft("旋转使用 Local 空间", _rotationTrack.UseLocalSpace);
            _rotationHandleSensitivity = EditorGUILayout.Slider("手柄灵敏度(角度/单位)", _rotationHandleSensitivity, 5f, 240f);
            AnimationCurve x = EditorGUILayout.CurveField("X Rotation", _rotationTrack.XCurve, Color.red, new Rect(0f, -180f, 1f, 360f));
            AnimationCurve y = EditorGUILayout.CurveField("Y Rotation", _rotationTrack.YCurve, Color.green, new Rect(0f, -180f, 1f, 360f));
            AnimationCurve z = EditorGUILayout.CurveField("Z Rotation", _rotationTrack.ZCurve, Color.blue, new Rect(0f, -180f, 1f, 360f));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_rotationTrack, "Edit Gesture Rotation Track");
                _rotationTrack.UseLocalSpace = useLocal;
                _rotationTrack.SetCurves(CloneCurve(x, 0f), CloneCurve(y, 0f), CloneCurve(z, 0f), duration);
                _recommendedDuration = _rotationTrack.TrackDuration;
                SetDirty(_rotationTrack);
                RefreshPreviewPoseIfStopped();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重置 Rotation 曲线"))
            {
                ResetRotationTrack();
            }

            if (GUILayout.Button("当前时间写入 0/0/0"))
            {
                WriteRotationKeyAtCurrentTime(0f, 0f, 0f);
            }

            EditorGUILayout.EndHorizontal();
        }

        private void DrawPersistSection()
        {
            EditorGUILayout.LabelField("保存", EditorStyles.boldLabel);
            if (GUILayout.Button("保存当前调试状态"))
            {
                SaveWorkspaceState();
            }
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!_sceneEditingEnabled || !HasWorkspaceReady)
            {
                return;
            }

            switch (_mode)
            {
                case WorkbenchMode.Global:
                    break;
                case WorkbenchMode.Path:
                    DrawStrokeInScene();
                    DrawGeneratedPathInScene();
                    HandlePathInput(sceneView);
                    DrawSceneMiniPanel(DrawPathScenePanelGUI);
                    break;
                case WorkbenchMode.Scale:
                    DrawScaleHandlesInScene();
                    DrawSceneMiniPanel(DrawScaleScenePanelGUI);
                    break;
                case WorkbenchMode.Rotation:
                    DrawRotationHandlesInScene();
                    DrawSceneMiniPanel(DrawRotationScenePanelGUI);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void DrawPathScenePanelGUI()
        {
            EditorGUILayout.LabelField("路径快捷参数", EditorStyles.miniBoldLabel);
            _smoothing = EditorGUILayout.Slider("平滑", _smoothing, 0f, 1f);
            _pathSimplifyTolerance = EditorGUILayout.Slider("简化", _pathSimplifyTolerance, 0f, 0.3f);
            _minSampleDistance = EditorGUILayout.Slider("采样距离", _minSampleDistance, 0.001f, 0.2f);
            _minSampleInterval = EditorGUILayout.Slider("采样间隔", _minSampleInterval, 0f, 0.08f);
            _drawPlane = (DrawPlane)EditorGUILayout.EnumPopup("绘制平面", _drawPlane);
        }

        private void DrawScaleScenePanelGUI()
        {
            EditorGUILayout.LabelField("缩放快捷参数", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("当前时间", GetEditingNormalizedTime().ToString("0.000"));
            _scaleHandleSensitivity = EditorGUILayout.Slider("灵敏度", _scaleHandleSensitivity, 0.1f, 4f);
            _handleSizeMultiplier = EditorGUILayout.Slider("手柄尺寸", _handleSizeMultiplier, 0.2f, 2.2f);
        }

        private void DrawRotationScenePanelGUI()
        {
            EditorGUILayout.LabelField("旋转快捷参数", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField("当前时间", GetEditingNormalizedTime().ToString("0.000"));
            _rotationHandleSensitivity = EditorGUILayout.Slider("灵敏度", _rotationHandleSensitivity, 5f, 240f);
            _handleSizeMultiplier = EditorGUILayout.Slider("手柄尺寸", _handleSizeMultiplier, 0.2f, 2.2f);
        }

        private void DrawSceneMiniPanel(Action drawBody)
        {
            if (!_showSceneModePanel || _mode == WorkbenchMode.Global)
            {
                return;
            }

            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(14f, 14f, 340f, 220f), GUI.skin.window);
            _scenePanelFoldout = EditorGUILayout.Foldout(_scenePanelFoldout, $"{_mode} 小面板", true);
            if (_scenePanelFoldout)
            {
                drawBody?.Invoke();
            }

            GUILayout.EndArea();
            Handles.EndGUI();
        }

        private void DrawStrokeInScene()
        {
            if (_samples.Count < 2)
            {
                return;
            }

            Handles.color = new Color(1f, 0.55f, 0.1f, 0.95f);
            for (int i = 0; i < _samples.Count - 1; i++)
            {
                Handles.DrawLine(_samples[i].worldPos, _samples[i + 1].worldPos);
            }
        }

        private void DrawGeneratedPathInScene()
        {
            Transform target = _pathTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            List<Vector3> worldPath = BuildWorldPath(target, _pathTrack.LocalPathPoints);
            if (worldPath.Count < 2)
            {
                return;
            }

            Handles.color = new Color(0.2f, 0.85f, 1f, 0.95f);
            for (int i = 0; i < worldPath.Count - 1; i++)
            {
                Handles.DrawLine(worldPath[i], worldPath[i + 1]);
            }

            Handles.color = Color.white;
            foreach (Vector3 p in worldPath)
            {
                Handles.DrawSolidDisc(p, Vector3.forward, HandleUtility.GetHandleSize(p) * 0.03f);
            }
        }

        private void HandlePathInput(SceneView sceneView)
        {
            if (_mode != WorkbenchMode.Path || _pathTrack == null)
            {
                return;
            }

            Transform target = _pathTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            Event e = Event.current;
            bool hotkeySatisfied = !_requireShiftToDraw || e.shift;

            if (_isDrawing)
            {
                HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

                if (e.type == EventType.MouseDrag && e.button == 0)
                {
                    if (TryGetWorldPoint(sceneView, e.mousePosition, out Vector3 world))
                    {
                        AddSample(e.mousePosition, world);
                    }

                    e.Use();
                    SceneView.RepaintAll();
                    return;
                }

                if ((e.type == EventType.MouseUp || e.rawType == EventType.MouseUp) && e.button == 0)
                {
                    FinishStroke();
                    e.Use();
                    SceneView.RepaintAll();
                    return;
                }

                if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
                {
                    _samples.Clear();
                    _isDrawing = false;
                    e.Use();
                    SceneView.RepaintAll();
                }

                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && hotkeySatisfied)
            {
                if (TryGetWorldPoint(sceneView, e.mousePosition, out Vector3 world))
                {
                    StartStroke(e.mousePosition, world);
                    e.Use();
                    SceneView.RepaintAll();
                }
            }
        }

        private void DrawScaleHandlesInScene()
        {
            if (!CanEditHandlesNow())
            {
                return;
            }

            Transform target = _scaleTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            float t = GetEditingNormalizedTime();
            float x = _scaleTrack.XCurve.Evaluate(t);
            float y = _scaleTrack.YCurve.Evaluate(t);
            float z = _scaleTrack.ZCurve.Evaluate(t);

            Vector3 pivot = target.position;
            float size = HandleUtility.GetHandleSize(pivot) * _handleSizeMultiplier;
            float worldUnit = Mathf.Max(0.0001f, size * 0.55f);
            float baseOffset = size * 0.35f;

            bool changed = false;
            changed |= DrawAxisSliderHandle(pivot, target.right, 1f, ref x, baseOffset, worldUnit, _scaleHandleSensitivity, Color.red, "SX");
            changed |= DrawAxisSliderHandle(pivot, target.up, 1f, ref y, baseOffset, worldUnit, _scaleHandleSensitivity, Color.green, "SY");
            changed |= DrawAxisSliderHandle(pivot, target.forward, 1f, ref z, baseOffset, worldUnit, _scaleHandleSensitivity, Color.blue, "SZ");

            if (!changed)
            {
                return;
            }

            x = Mathf.Max(0.01f, x);
            y = Mathf.Max(0.01f, y);
            z = Mathf.Max(0.01f, z);
            WriteScaleKeyAtCurrentTime(x, y, z);
        }

        private void DrawRotationHandlesInScene()
        {
            if (!CanEditHandlesNow())
            {
                return;
            }

            Transform target = _rotationTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            float t = GetEditingNormalizedTime();
            float x = _rotationTrack.XCurve.Evaluate(t);
            float y = _rotationTrack.YCurve.Evaluate(t);
            float z = _rotationTrack.ZCurve.Evaluate(t);

            Vector3 xAxis = _rotationTrack.UseLocalSpace ? target.right : Vector3.right;
            Vector3 yAxis = _rotationTrack.UseLocalSpace ? target.up : Vector3.up;
            Vector3 zAxis = _rotationTrack.UseLocalSpace ? target.forward : Vector3.forward;

            Vector3 pivot = target.position;
            float size = HandleUtility.GetHandleSize(pivot) * _handleSizeMultiplier;
            float worldUnit = Mathf.Max(0.0001f, size * 0.55f);
            float baseOffset = size * 0.6f;

            bool changed = false;
            changed |= DrawAxisSliderHandle(pivot, xAxis, 0f, ref x, baseOffset, worldUnit, _rotationHandleSensitivity, Color.red, "RX");
            changed |= DrawAxisSliderHandle(pivot, yAxis, 0f, ref y, baseOffset, worldUnit, _rotationHandleSensitivity, Color.green, "RY");
            changed |= DrawAxisSliderHandle(pivot, zAxis, 0f, ref z, baseOffset, worldUnit, _rotationHandleSensitivity, Color.blue, "RZ");

            if (!changed)
            {
                return;
            }

            WriteRotationKeyAtCurrentTime(x, y, z);
        }

        private bool CanEditHandlesNow()
        {
            if (!HasWorkspaceReady)
            {
                return false;
            }

            if (_previewController.IsPlaying && !_dynamicEditWhilePlaying)
            {
                Handles.BeginGUI();
                GUILayout.BeginArea(new Rect(14f, 238f, 340f, 46f), GUI.skin.box);
                GUILayout.Label("播放中已禁用手柄编辑。开启动态模式可实时改值。");
                GUILayout.EndArea();
                Handles.EndGUI();
                return false;
            }

            return true;
        }

        private static bool DrawAxisSliderHandle(
            Vector3 pivot,
            Vector3 axis,
            float baseValue,
            ref float value,
            float baseOffset,
            float worldUnit,
            float sensitivity,
            Color color,
            string label)
        {
            if (axis.sqrMagnitude < 0.0001f || worldUnit <= 0.0001f || sensitivity <= 0.0001f)
            {
                return false;
            }

            Vector3 axisDir = axis.normalized;
            Vector3 zeroPoint = pivot + axisDir * baseOffset;
            Vector3 currentPoint = zeroPoint + axisDir * ((value - baseValue) / sensitivity * worldUnit);

            Handles.color = new Color(color.r, color.g, color.b, 0.9f);
            Handles.DrawLine(zeroPoint, currentPoint);
            Handles.Label(currentPoint + axisDir * (HandleUtility.GetHandleSize(currentPoint) * 0.08f), $"{label}: {value:0.##}");

            EditorGUI.BeginChangeCheck();
            Vector3 movedPoint = Handles.Slider(
                currentPoint,
                axisDir,
                HandleUtility.GetHandleSize(currentPoint) * 0.09f,
                Handles.ConeHandleCap,
                0f);

            if (!EditorGUI.EndChangeCheck())
            {
                return false;
            }

            float worldDelta = Vector3.Dot(movedPoint - zeroPoint, axisDir);
            value = baseValue + worldDelta / worldUnit * sensitivity;
            return true;
        }

        private bool TryGetWorldPoint(SceneView sceneView, Vector2 guiPosition, out Vector3 worldPoint)
        {
            worldPoint = Vector3.zero;

            Transform planeTarget = _pathTrack != null ? _pathTrack.ResolveTarget() : _rootTarget;
            if (planeTarget == null)
            {
                return false;
            }

            Ray ray = HandleUtility.GUIPointToWorldRay(guiPosition);
            Vector3 planePoint = planeTarget.position;
            Vector3 normal = Vector3.forward;

            switch (_drawPlane)
            {
                case DrawPlane.XY:
                    normal = Vector3.forward;
                    break;
                case DrawPlane.XZ:
                    normal = Vector3.up;
                    break;
                case DrawPlane.YZ:
                    normal = Vector3.right;
                    break;
                case DrawPlane.CameraFacing:
                    normal = sceneView != null && sceneView.camera != null ? sceneView.camera.transform.forward : Vector3.forward;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            Plane plane = new Plane(normal, planePoint);
            if (!plane.Raycast(ray, out float enter))
            {
                return false;
            }

            worldPoint = ray.GetPoint(enter);
            return true;
        }

        private void StartStroke(Vector2 guiPos, Vector3 worldPos)
        {
            _samples.Clear();
            _isDrawing = true;
            AddSample(guiPos, worldPos, force: true);
        }

        private void AddSample(Vector2 guiPos, Vector3 worldPos, bool force = false)
        {
            float now = (float)EditorApplication.timeSinceStartup;
            if (!force && _samples.Count > 0)
            {
                StrokeSample last = _samples[^1];
                float dt = now - last.timestamp;
                float dist = Vector3.Distance(last.worldPos, worldPos);
                if (dt < _minSampleInterval && dist < _minSampleDistance)
                {
                    return;
                }
            }

            _samples.Add(new StrokeSample
            {
                sceneGuiPos = guiPos,
                worldPos = worldPos,
                timestamp = now
            });
        }

        private void FinishStroke()
        {
            _isDrawing = false;
            if (_samples.Count < 2 || !HasWorkspaceReady)
            {
                return;
            }

            _recommendedDuration = Mathf.Max(MinDuration, _samples[^1].timestamp - _samples[0].timestamp);
            GeneratePositionChannel();
            _samples.Clear();
            Repaint();
        }

        private void GeneratePositionChannel()
        {
            Transform target = _pathTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            var raw = new List<Vector3>(_samples.Count);
            foreach (StrokeSample sample in _samples)
            {
                raw.Add(sample.worldPos);
            }

            List<Vector3> smoothed = SmoothPath(raw, _smoothing);
            List<Vector3> simplified = SimplifyPath(smoothed, _pathSimplifyTolerance);
            if (simplified.Count < 2)
            {
                return;
            }

            _pathLocalOffsets.Clear();
            _pathLocalOffsets.AddRange(ConvertWorldPathToLocalOffsets(target, simplified));
            AnimationCurve ease = GenerateEaseCurve(_samples, _smoothing);

            Undo.RecordObject(_pathTrack, "Generate Gesture Path");
            _pathTrack.SetPathData(_pathLocalOffsets, ease, _recommendedDuration, _pathUseLocalSpace);
            _pathTrack.TrackDuration = _recommendedDuration;
            SetDirty(_pathTrack);

            RefreshPreviewPoseIfStopped();
            Debug.Log($"[GestureTween] Path updated. points={_pathLocalOffsets.Count}, duration={_recommendedDuration:0.###}s");
        }

        private void WriteScaleKeyAtCurrentTime(float x, float y, float z)
        {
            if (_scaleTrack == null)
            {
                return;
            }

            float t = GetEditingNormalizedTime();
            Undo.RecordObject(_scaleTrack, "Adjust Gesture Scale Curve");
            AnimationCurve xCurve = UpsertCurveKey(_scaleTrack.XCurve, t, x, 1f);
            AnimationCurve yCurve = UpsertCurveKey(_scaleTrack.YCurve, t, y, 1f);
            AnimationCurve zCurve = UpsertCurveKey(_scaleTrack.ZCurve, t, z, 1f);
            _scaleTrack.SetCurves(xCurve, yCurve, zCurve, _scaleTrack.TrackDuration);
            SetDirty(_scaleTrack);
            RefreshPreviewPoseIfStopped();
        }

        private void WriteRotationKeyAtCurrentTime(float x, float y, float z)
        {
            if (_rotationTrack == null)
            {
                return;
            }

            float t = GetEditingNormalizedTime();
            Undo.RecordObject(_rotationTrack, "Adjust Gesture Rotation Curve");
            AnimationCurve xCurve = UpsertCurveKey(_rotationTrack.XCurve, t, x, 0f);
            AnimationCurve yCurve = UpsertCurveKey(_rotationTrack.YCurve, t, y, 0f);
            AnimationCurve zCurve = UpsertCurveKey(_rotationTrack.ZCurve, t, z, 0f);
            _rotationTrack.SetCurves(xCurve, yCurve, zCurve, _rotationTrack.TrackDuration);
            SetDirty(_rotationTrack);
            RefreshPreviewPoseIfStopped();
        }

        private void PlayPreview()
        {
            if (!HasWorkspaceReady)
            {
                return;
            }

            RefreshPreviewAnimations();
            if (_previewAnimations.Length == 0)
            {
                return;
            }

            DottEditorPreview.PlaybackSpeed = _previewSpeed;
            float absoluteTime = Mathf.Clamp01(_previewTimeNormalized) * _timelineDuration;
            _previewController.GoTo(_previewAnimations, absoluteTime);
            _previewController.Play(_previewAnimations);
        }

        private void PausePreview()
        {
            if (_previewController == null)
            {
                return;
            }

            _previewController.Pause();
            _previewTimeNormalized = GetPlayingNormalizedTime();
        }

        private void StopPreview()
        {
            if (_previewController == null)
            {
                return;
            }

            _previewController.Stop();
            _previewTimeNormalized = 0f;
            GoToPreviewTime(0f);
        }

        private void GoToPreviewTime(float normalizedTime)
        {
            if (_previewController == null || !HasWorkspaceReady)
            {
                return;
            }

            RefreshPreviewAnimations();
            if (_previewAnimations.Length == 0)
            {
                return;
            }

            float absoluteTime = Mathf.Clamp01(normalizedTime) * _timelineDuration;
            _previewController.GoTo(_previewAnimations, absoluteTime);
        }

        private void RefreshPreviewPoseIfStopped()
        {
            if (_previewController == null)
            {
                return;
            }

            if (_previewController.IsPlaying)
            {
                DottEditorPreview.QueuePlayerLoopUpdate();
            }
            else
            {
                GoToPreviewTime(_previewTimeNormalized);
            }
        }

        private void RefreshPreviewAnimations()
        {
            if (_timeline == null)
            {
                _previewAnimations = Array.Empty<IDOTweenAnimation>();
                _timelineDuration = 1f;
                return;
            }

            _previewAnimations = _timeline
                .GetComponents<MonoBehaviour>()
                .Select(DottAnimation.FromComponent)
                .Where(animation => animation != null)
                .ToArray();

            _timelineDuration = ComputeTimelineDuration(_previewAnimations);
        }

        private float GetEditingNormalizedTime()
        {
            if (_previewController != null && _previewController.IsPlaying)
            {
                return GetPlayingNormalizedTime();
            }

            return Mathf.Clamp01(_previewTimeNormalized);
        }

        private float GetPlayingNormalizedTime()
        {
            float duration = Mathf.Max(MinDuration, _timelineDuration);
            float elapsed = Mathf.Max(0f, _previewController.ElapsedTime);
            float bounded = _previewController.Loop
                ? Mathf.Repeat(elapsed, duration)
                : Mathf.Clamp(elapsed, 0f, duration);
            return bounded / duration;
        }

        private static float ComputeTimelineDuration(IReadOnlyList<IDOTweenAnimation> animations)
        {
            float max = MinDuration;
            if (animations == null)
            {
                return max;
            }

            for (int i = 0; i < animations.Count; i++)
            {
                IDOTweenAnimation animation = animations[i];
                if (animation == null)
                {
                    continue;
                }

                int loops = animation.Loops == -1 ? 1 : Mathf.Max(1, animation.Loops);
                float end = Mathf.Max(0f, animation.Delay) + Mathf.Max(0f, animation.Duration) * loops;
                max = Mathf.Max(max, end);
            }

            return Mathf.Max(MinDuration, max);
        }

        private void EnsureWorkspaceIfNeeded(bool forceCreate)
        {
            if (_rootTarget == null)
            {
                return;
            }

            if (_workspace != null &&
                _workspace.gameObject.scene.IsValid() &&
                _workspace.transform.parent == _rootTarget)
            {
                CacheWorkspaceReferences();
                return;
            }

            _workspace = GestureWorkspaceFactory.FindWorkspace(_rootTarget);
            if (_workspace == null && (forceCreate || _autoCreateWorkspace))
            {
                _workspace = GestureWorkspaceFactory.EnsureWorkspace(_rootTarget);
            }

            CacheWorkspaceReferences();
        }

        private void CacheWorkspaceReferences()
        {
            if (_workspace == null)
            {
                _timeline = null;
                _pathTrack = null;
                _scaleTrack = null;
                _rotationTrack = null;
                return;
            }

            _timeline = _workspace.Timeline != null ? _workspace.Timeline : _workspace.GetComponent<DOTweenTimeline>();
            _pathTrack = _workspace.PathTrack != null ? _workspace.PathTrack : _workspace.GetComponent<GesturePathTrack>();
            _scaleTrack = _workspace.ScaleTrack != null ? _workspace.ScaleTrack : _workspace.GetComponent<GestureScaleTrack>();
            _rotationTrack = _workspace.RotationTrack != null ? _workspace.RotationTrack : _workspace.GetComponent<GestureRotationTrack>();

            _workspace.SetReferences(_timeline, _pathTrack, _scaleTrack, _rotationTrack);
            _workspace.BindRootToTracks();

            if (_pathTrack != null)
            {
                _pathUseLocalSpace = _pathTrack.UseLocalSpace;
                _recommendedDuration = Mathf.Max(MinDuration, _pathTrack.TrackDuration);
            }
        }

        private void ResetPathTrack()
        {
            if (_pathTrack == null)
            {
                return;
            }

            Undo.RecordObject(_pathTrack, "Reset Gesture Path Track");
            _pathLocalOffsets.Clear();
            _pathLocalOffsets.Add(Vector3.zero);
            _pathLocalOffsets.Add(Vector3.right);
            _pathTrack.SetPathData(_pathLocalOffsets, AnimationCurve.Linear(0f, 0f, 1f, 1f), _recommendedDuration, _pathUseLocalSpace);
            SetDirty(_pathTrack);
            RefreshPreviewPoseIfStopped();
        }

        private void ResetScaleTrack()
        {
            if (_scaleTrack == null)
            {
                return;
            }

            Undo.RecordObject(_scaleTrack, "Reset Gesture Scale Track");
            _scaleTrack.SetCurves(
                AnimationCurve.Linear(0f, 1f, 1f, 1f),
                AnimationCurve.Linear(0f, 1f, 1f, 1f),
                AnimationCurve.Linear(0f, 1f, 1f, 1f),
                _scaleTrack.TrackDuration);
            SetDirty(_scaleTrack);
            RefreshPreviewPoseIfStopped();
        }

        private void ResetRotationTrack()
        {
            if (_rotationTrack == null)
            {
                return;
            }

            Undo.RecordObject(_rotationTrack, "Reset Gesture Rotation Track");
            _rotationTrack.SetCurves(
                AnimationCurve.Linear(0f, 0f, 1f, 0f),
                AnimationCurve.Linear(0f, 0f, 1f, 0f),
                AnimationCurve.Linear(0f, 0f, 1f, 0f),
                _rotationTrack.TrackDuration);
            SetDirty(_rotationTrack);
            RefreshPreviewPoseIfStopped();
        }

        private void SaveWorkspaceState()
        {
            if (!HasWorkspaceReady)
            {
                return;
            }

            SetDirty(_workspace);
            SetDirty(_timeline);
            SetDirty(_pathTrack);
            SetDirty(_scaleTrack);
            SetDirty(_rotationTrack);

            if (_workspace.gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(_workspace.gameObject.scene);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[GestureTween] Workspace state saved.");
        }

        private static void SetDirty(Object obj)
        {
            if (obj == null)
            {
                return;
            }

            EditorUtility.SetDirty(obj);
            PrefabUtility.RecordPrefabInstancePropertyModifications(obj);
        }

        private static AnimationCurve CloneCurve(AnimationCurve curve, float fallbackValue)
        {
            if (curve == null || curve.length < 2)
            {
                return AnimationCurve.Linear(0f, fallbackValue, 1f, fallbackValue);
            }

            return new AnimationCurve(curve.keys);
        }

        private static AnimationCurve UpsertCurveKey(AnimationCurve source, float time, float value, float fallbackValue)
        {
            AnimationCurve curve = CloneCurve(source, fallbackValue);
            time = Mathf.Clamp01(time);

            int nearestKey = FindNearestKey(curve, time, KeyMergeThreshold);
            if (nearestKey >= 0)
            {
                Keyframe key = curve.keys[nearestKey];
                key.time = time;
                key.value = value;
                curve.MoveKey(nearestKey, key);
            }
            else
            {
                curve.AddKey(new Keyframe(time, value));
            }

            EnsureBoundaryKey(curve, 0f);
            EnsureBoundaryKey(curve, 1f);
            SmoothCurveTangents(curve);
            return curve;
        }

        private static int FindNearestKey(AnimationCurve curve, float time, float threshold)
        {
            if (curve == null)
            {
                return -1;
            }

            int found = -1;
            float min = threshold;
            for (int i = 0; i < curve.length; i++)
            {
                float delta = Mathf.Abs(curve.keys[i].time - time);
                if (delta > min)
                {
                    continue;
                }

                min = delta;
                found = i;
            }

            return found;
        }

        private static void EnsureBoundaryKey(AnimationCurve curve, float time)
        {
            if (curve == null)
            {
                return;
            }

            const float epsilon = 0.0001f;
            for (int i = 0; i < curve.length; i++)
            {
                if (Mathf.Abs(curve.keys[i].time - time) < epsilon)
                {
                    return;
                }
            }

            curve.AddKey(new Keyframe(time, curve.Evaluate(time)));
        }

        private static void SmoothCurveTangents(AnimationCurve curve)
        {
            if (curve == null)
            {
                return;
            }

            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            }
        }

        private static List<Vector3> BuildWorldPath(Transform target, IReadOnlyList<Vector3> localOffsets)
        {
            var points = new List<Vector3>();
            if (target == null || localOffsets == null || localOffsets.Count < 2)
            {
                return points;
            }

            Transform parent = target.parent;
            Vector3 startLocal = target.localPosition;
            for (int i = 0; i < localOffsets.Count; i++)
            {
                Vector3 localPoint = startLocal + localOffsets[i];
                Vector3 world = parent != null ? parent.TransformPoint(localPoint) : localPoint;
                points.Add(world);
            }

            return points;
        }

        private static List<Vector3> ConvertWorldPathToLocalOffsets(Transform target, List<Vector3> worldPath)
        {
            var offsets = new List<Vector3>(worldPath.Count);
            if (target == null || worldPath == null || worldPath.Count == 0)
            {
                return offsets;
            }

            Transform parent = target.parent;
            Vector3 startLocal = parent != null ? parent.InverseTransformPoint(worldPath[0]) : worldPath[0];
            for (int i = 0; i < worldPath.Count; i++)
            {
                Vector3 local = parent != null ? parent.InverseTransformPoint(worldPath[i]) : worldPath[i];
                offsets.Add(local - startLocal);
            }

            return offsets;
        }

        private static List<Vector3> SmoothPath(List<Vector3> raw, float smoothing)
        {
            if (raw == null || raw.Count <= 2)
            {
                return raw != null ? new List<Vector3>(raw) : new List<Vector3>();
            }

            int radius = Mathf.RoundToInt(Mathf.Lerp(0f, 4f, Mathf.Clamp01(smoothing)));
            if (radius <= 0)
            {
                return new List<Vector3>(raw);
            }

            var result = new List<Vector3>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                Vector3 sum = Vector3.zero;
                int count = 0;
                int from = Mathf.Max(0, i - radius);
                int to = Mathf.Min(raw.Count - 1, i + radius);
                for (int j = from; j <= to; j++)
                {
                    sum += raw[j];
                    count++;
                }

                result.Add(count > 0 ? sum / count : raw[i]);
            }

            result[0] = raw[0];
            result[^1] = raw[^1];
            return result;
        }

        private static List<Vector3> SimplifyPath(List<Vector3> points, float tolerance)
        {
            if (points == null || points.Count <= 2 || tolerance <= 0f)
            {
                return points != null ? new List<Vector3>(points) : new List<Vector3>();
            }

            float toleranceSq = tolerance * tolerance;
            var keep = new bool[points.Count];
            keep[0] = true;
            keep[^1] = true;
            SimplifySection(points, 0, points.Count - 1, toleranceSq, keep);

            var result = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                {
                    result.Add(points[i]);
                }
            }

            if (result.Count >= 2)
            {
                return result;
            }

            result.Clear();
            result.Add(points[0]);
            result.Add(points[^1]);
            return result;
        }

        private static void SimplifySection(List<Vector3> points, int start, int end, float toleranceSq, bool[] keep)
        {
            if (end <= start + 1)
            {
                return;
            }

            int index = -1;
            float maxDistSq = 0f;
            Vector3 a = points[start];
            Vector3 b = points[end];

            for (int i = start + 1; i < end; i++)
            {
                float distSq = DistancePointToSegmentSq(points[i], a, b);
                if (distSq <= maxDistSq)
                {
                    continue;
                }

                maxDistSq = distSq;
                index = i;
            }

            if (index < 0 || maxDistSq <= toleranceSq)
            {
                return;
            }

            keep[index] = true;
            SimplifySection(points, start, index, toleranceSq, keep);
            SimplifySection(points, index, end, toleranceSq, keep);
        }

        private static float DistancePointToSegmentSq(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abLenSq = ab.sqrMagnitude;
            if (abLenSq <= 0.000001f)
            {
                return (point - a).sqrMagnitude;
            }

            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / abLenSq);
            Vector3 projection = a + t * ab;
            return (point - projection).sqrMagnitude;
        }

        private static AnimationCurve GenerateEaseCurve(List<StrokeSample> samples, float smoothing)
        {
            int count = samples.Count;
            if (count < 2)
            {
                return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            }

            var speeds = new float[count - 1];
            float maxSpeed = 0f;
            for (int i = 1; i < count; i++)
            {
                float dt = Mathf.Max(0.0001f, samples[i].timestamp - samples[i - 1].timestamp);
                float dist = Vector3.Distance(samples[i].worldPos, samples[i - 1].worldPos);
                float speed = dist / dt;
                speeds[i - 1] = speed;
                maxSpeed = Mathf.Max(maxSpeed, speed);
            }

            if (maxSpeed < 0.0001f)
            {
                maxSpeed = 1f;
            }

            var cumulative = new float[count];
            float totalWeight = 0f;
            for (int i = 1; i < count; i++)
            {
                float normalizedSpeed = Mathf.Clamp01(speeds[i - 1] / maxSpeed);
                float weight = Mathf.Lerp(1f, Mathf.Max(0.05f, normalizedSpeed), 1f - smoothing);
                totalWeight += weight;
                cumulative[i] = totalWeight;
            }

            if (totalWeight < 0.0001f)
            {
                totalWeight = 1f;
            }

            float startTime = samples[0].timestamp;
            float totalTime = Mathf.Max(0.0001f, samples[^1].timestamp - startTime);
            var keys = new Keyframe[count];
            keys[0] = new Keyframe(0f, 0f);
            for (int i = 1; i < count; i++)
            {
                float t = Mathf.Clamp01((samples[i].timestamp - startTime) / totalTime);
                float p = Mathf.Clamp01(cumulative[i] / totalWeight);
                p = Mathf.Max(p, keys[i - 1].value);
                keys[i] = new Keyframe(t, p);
            }

            keys[^1] = new Keyframe(1f, 1f);
            var curve = new AnimationCurve(keys);
            SmoothCurveTangents(curve);
            return curve;
        }
    }
}

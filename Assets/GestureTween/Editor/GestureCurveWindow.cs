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
        private const int MaxBezierResamplePoints = 320;

        private readonly List<StrokeSample> _strokeSamples = new();
        private readonly List<StrokeSample> _lastStrokeSamples = new();
        private readonly List<Vector3> _liveStrokePath = new();
        private readonly List<Vector3> _pathLocalOffsets = new();
        private readonly Stack<List<Vector3>> _postProcessUndoStack = new();

        private List<Vector3> _lastStrokeBaselineOffsets = new();

        private Transform _rootTarget;
        private GestureWorkspace _workspace;
        private DOTweenTimeline _timeline;
        private GesturePathTrack _pathTrack;

        private bool _sceneEditingEnabled = true;
        private bool _requireShiftToDraw = true;
        private DrawPlane _drawPlane = DrawPlane.XY;
        private bool _isDrawing;
        private bool _pathUseLocalSpace = true;

        private float _recommendedDuration = 0.8f;
        private float _previewSpeed = 1f;
        private float _previewTimeNormalized;
        private float _timelineDuration = 1f;

        private float _minSampleDistance = 0.02f;
        private float _minSampleInterval = 0.01f;
        private float _captureSmoothingStrength = 0.25f;
        private int _captureSmoothingPasses = 1;
        private float _captureSimplifyAmount = 0.2f;
        private bool _autoBezierOnStroke = true;
        private float _autoBezierAmount = 0.35f;
        private float _autoBezierDensity = 1f;

        private float _postSmoothStrength = 0.45f;
        private int _postSmoothPasses = 2;
        private float _postBezierAmount = 0.5f;
        private float _postBezierDensity = 1f;
        private float _easeIntentStrength = 0.7f;
        private float _easeJitterSuppression = 0.65f;
        private float _easeKeyReduction = 0.45f;

        private bool _ghostFollowEnabled = true;
        private float _ghostAlpha = 0.24f;
        private float _ghostFollowSmoothTime = 0.06f;
        private bool _strokeStartedWithShift;
        private bool _ghostActive;
        private Vector3 _ghostWorldPosition;
        private Vector3 _ghostTargetWorldPosition;
        private Vector3 _ghostVelocity;
        private Material _ghostMaterial;

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
            _pathTrack != null;

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

            ReleaseGhostMaterial();
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
            EditorGUILayout.LabelField("GestureTween Path Workbench", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "专注快速路径手绘。旋转/缩放请直接使用 DOTween Animation + Timeline 原生能力。",
                MessageType.Info);

            DrawWorkspaceSection();
            EditorGUILayout.Space(6f);

            DrawPreviewSection();
            EditorGUILayout.Space(6f);

            DrawPathToolSection();
            EditorGUILayout.Space(6f);

            DrawPostProcessSection();
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

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("创建/修复工作区"))
            {
                EnsureWorkspaceIfNeeded(forceCreate: true);
            }

            using (new EditorGUI.DisabledScope(_workspace == null))
            {
                if (GUILayout.Button("定位工作区"))
                {
                    Selection.activeTransform = _workspace.transform;
                    EditorGUIUtility.PingObject(_workspace.gameObject);
                }
            }

            EditorGUILayout.EndHorizontal();

            if (_workspace == null)
            {
                EditorGUILayout.HelpBox("当前没有可用工作区。创建后会自动挂载 DOTweenTimeline + Path 通道。", MessageType.Warning);
                return;
            }

            string workspaceName = _workspace != null ? _workspace.name : "None";
            string timelineName = _timeline != null ? _timeline.GetType().Name : "Missing";
            EditorGUILayout.LabelField("工作区", workspaceName);
            EditorGUILayout.LabelField("基础组件", timelineName);
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
        }

        private void DrawPathToolSection()
        {
            EditorGUILayout.LabelField("快速路径绘制", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("在 Scene 视图按住 Shift + 左键拖拽即可手绘路径。Shift 绘制期间会显示半透明虚影跟随。", MessageType.None);

            _sceneEditingEnabled = EditorGUILayout.ToggleLeft("启用 Scene 编辑", _sceneEditingEnabled);
            _requireShiftToDraw = EditorGUILayout.ToggleLeft("路径绘制需要 Shift", _requireShiftToDraw);
            _drawPlane = (DrawPlane)EditorGUILayout.EnumPopup("路径绘制平面", _drawPlane);
            _ghostFollowEnabled = EditorGUILayout.ToggleLeft("启用虚影跟随", _ghostFollowEnabled);

            EditorGUI.BeginChangeCheck();
            _ghostAlpha = EditorGUILayout.Slider("虚影透明度", _ghostAlpha, 0.05f, 0.8f);
            _ghostFollowSmoothTime = EditorGUILayout.Slider("虚影跟随延迟", _ghostFollowSmoothTime, 0.01f, 0.25f);
            if (EditorGUI.EndChangeCheck())
            {
                _ghostAlpha = Mathf.Clamp01(_ghostAlpha);
                _ghostFollowSmoothTime = Mathf.Clamp(_ghostFollowSmoothTime, 0.01f, 0.25f);
            }

            if (!HasWorkspaceReady)
            {
                return;
            }

            EditorGUI.BeginChangeCheck();
            float duration = EditorGUILayout.FloatField("通道时长(秒)", Mathf.Max(MinDuration, _pathTrack.TrackDuration));
            bool useLocalSpace = EditorGUILayout.ToggleLeft("Path 使用 Local 空间", _pathUseLocalSpace);
            AnimationCurve easeCurve = EditorGUILayout.CurveField("Ease 曲线", _pathTrack.EaseCurve, Color.cyan, new Rect(0f, 0f, 1f, 1f));

            _minSampleDistance = EditorGUILayout.Slider("采样最小距离", _minSampleDistance, 0.001f, 0.2f);
            _minSampleInterval = EditorGUILayout.Slider("采样最小间隔", _minSampleInterval, 0f, 0.08f);

            _captureSmoothingStrength = EditorGUILayout.Slider("手绘去抖强度", _captureSmoothingStrength, 0f, 1f);
            _captureSmoothingPasses = EditorGUILayout.IntSlider("手绘去抖轮次", _captureSmoothingPasses, 1, 6);
            _captureSimplifyAmount = EditorGUILayout.Slider("手绘简化幅度", _captureSimplifyAmount, 0f, 1f);

            _autoBezierOnStroke = EditorGUILayout.ToggleLeft("落笔后自动贝塞尔抽象", _autoBezierOnStroke);
            if (_autoBezierOnStroke)
            {
                _autoBezierAmount = EditorGUILayout.Slider("自动贝塞尔强度", _autoBezierAmount, 0f, 1f);
                _autoBezierDensity = EditorGUILayout.Slider("自动贝塞尔密度", _autoBezierDensity, 0.3f, 2.5f);
            }

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(_pathTrack, "Edit Gesture Path Track");
                _pathUseLocalSpace = useLocalSpace;
                _pathTrack.UseLocalSpace = _pathUseLocalSpace;
                _pathTrack.TrackDuration = Mathf.Max(MinDuration, duration);
                _pathTrack.EaseCurve = CloneCurve(easeCurve, 0f);
                _recommendedDuration = _pathTrack.TrackDuration;
                SetDirty(_pathTrack);
                RefreshPreviewPoseIfStopped();
            }

            int pointCount = _pathTrack.LocalPathPoints != null ? _pathTrack.LocalPathPoints.Count : 0;
            EditorGUILayout.LabelField("当前路径点数", pointCount.ToString());
            EditorGUILayout.LabelField("当前笔画采样数", _strokeSamples.Count.ToString());

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("清空当前笔画"))
            {
                CancelStroke();
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("重置 Path 通道"))
            {
                ResetPathTrack();
            }

            EditorGUILayout.EndHorizontal();
        }
        private void DrawPostProcessSection()
        {
            EditorGUILayout.LabelField("路径后处理", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("所有后处理都锁定起终点，并支持“撤销一步后处理”和“还原到本次手绘原始路径”。", MessageType.None);

            if (!HasWorkspaceReady)
            {
                return;
            }

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("路径平滑", EditorStyles.boldLabel);
            _postSmoothStrength = EditorGUILayout.Slider("平滑强度", _postSmoothStrength, 0f, 1f);
            _postSmoothPasses = EditorGUILayout.IntSlider("平滑轮次", _postSmoothPasses, 1, 8);
            if (GUILayout.Button("一键平滑路径"))
            {
                ApplySmoothingPostProcess();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("贝塞尔简化", EditorStyles.boldLabel);
            _postBezierAmount = EditorGUILayout.Slider("简化幅度", _postBezierAmount, 0f, 1f);
            _postBezierDensity = EditorGUILayout.Slider("曲线密度", _postBezierDensity, 0.3f, 2.5f);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("贝塞尔简化路径"))
            {
                ApplyBezierPostProcess();
            }

            if (GUILayout.Button("平滑 + 贝塞尔简化"))
            {
                ApplySmoothAndBezierPostProcess();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginVertical(GUI.skin.box);
            EditorGUILayout.LabelField("缓动优化", EditorStyles.boldLabel);
            _easeIntentStrength = EditorGUILayout.Slider("意图适配强度", _easeIntentStrength, 0f, 1f);
            _easeJitterSuppression = EditorGUILayout.Slider("缓动抖动抑制", _easeJitterSuppression, 0f, 1f);
            _easeKeyReduction = EditorGUILayout.Slider("关键点压缩幅度", _easeKeyReduction, 0f, 1f);

            if (GUILayout.Button("重建节奏缓动（基于最近一次手绘）"))
            {
                OptimizeEaseFromLastStroke();
            }

            EditorGUILayout.EndVertical();

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_postProcessUndoStack.Count == 0))
            {
                if (GUILayout.Button("撤销一步后处理"))
                {
                    UndoOnePostProcessStep();
                }
            }

            using (new EditorGUI.DisabledScope(_lastStrokeBaselineOffsets == null || _lastStrokeBaselineOffsets.Count < 2))
            {
                if (GUILayout.Button("还原到本次手绘原始路径"))
                {
                    RestoreToLastStrokeBaseline();
                }
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

            DrawGeneratedPathInScene();
            DrawStrokeInScene();
            DrawGhostFollower(sceneView);
            HandlePathInput(sceneView);
        }

        private void DrawStrokeInScene()
        {
            if (_strokeSamples.Count >= 2)
            {
                Handles.color = new Color(1f, 0.55f, 0.1f, 0.8f);
                for (int i = 0; i < _strokeSamples.Count - 1; i++)
                {
                    Handles.DrawLine(_strokeSamples[i].worldPos, _strokeSamples[i + 1].worldPos);
                }
            }

            if (_liveStrokePath.Count >= 2)
            {
                Handles.color = new Color(1f, 0.85f, 0.2f, 0.95f);
                for (int i = 0; i < _liveStrokePath.Count - 1; i++)
                {
                    Handles.DrawLine(_liveStrokePath[i], _liveStrokePath[i + 1]);
                }
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
            foreach (Vector3 point in worldPath)
            {
                Handles.DrawSolidDisc(point, Vector3.forward, HandleUtility.GetHandleSize(point) * 0.03f);
            }
        }

        private void DrawGhostFollower(SceneView sceneView)
        {
            if (!_ghostActive || !_strokeStartedWithShift || !_ghostFollowEnabled)
            {
                return;
            }

            if (_pathTrack == null)
            {
                return;
            }

            Transform target = _pathTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            if (Event.current.type != EventType.Repaint)
            {
                return;
            }

            Vector3 worldOffset = _ghostWorldPosition - target.position;
            bool meshDrawn = DrawGhostMeshes(target, worldOffset);

            Handles.color = new Color(0.2f, 0.95f, 1f, Mathf.Clamp01(_ghostAlpha * 2f));
            Handles.DrawDottedLine(target.position, _ghostWorldPosition, 4f);

            if (!meshDrawn)
            {
                Bounds bounds = CalculateRendererBounds(target);
                Vector3 center = bounds.center + worldOffset;
                Vector3 size = bounds.size;
                if (size.sqrMagnitude < 0.0001f)
                {
                    float fallback = HandleUtility.GetHandleSize(center) * 0.2f;
                    size = Vector3.one * fallback;
                }

                Handles.DrawWireCube(center, size);
            }

            if (sceneView != null)
            {
                sceneView.Repaint();
            }
        }

        private bool DrawGhostMeshes(Transform target, Vector3 worldOffset)
        {
            EnsureGhostMaterial();
            if (_ghostMaterial == null)
            {
                return false;
            }

            Color color = new Color(0.2f, 0.95f, 1f, _ghostAlpha);
            if (_ghostMaterial.HasProperty("_Color"))
            {
                _ghostMaterial.SetColor("_Color", color);
            }

            bool drawn = false;
            Matrix4x4 translation = Matrix4x4.Translate(worldOffset);
            MeshFilter[] meshFilters = target.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < meshFilters.Length; i++)
            {
                MeshFilter meshFilter = meshFilters[i];
                MeshRenderer meshRenderer = meshFilter != null ? meshFilter.GetComponent<MeshRenderer>() : null;
                Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
                if (meshFilter == null || meshRenderer == null || mesh == null || !meshRenderer.enabled)
                {
                    continue;
                }

                _ghostMaterial.SetPass(0);
                Graphics.DrawMeshNow(mesh, translation * meshFilter.transform.localToWorldMatrix);
                drawn = true;
            }

            return drawn;
        }

        private void EnsureGhostMaterial()
        {
            if (_ghostMaterial != null)
            {
                return;
            }

            Shader shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                return;
            }

            _ghostMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            _ghostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            _ghostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            _ghostMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            _ghostMaterial.SetInt("_ZWrite", 0);
        }

        private void ReleaseGhostMaterial()
        {
            if (_ghostMaterial == null)
            {
                return;
            }

            DestroyImmediate(_ghostMaterial);
            _ghostMaterial = null;
        }

        private static Bounds CalculateRendererBounds(Transform target)
        {
            Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(target.position, Vector3.zero);
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds;
        }

        private void HandlePathInput(SceneView sceneView)
        {
            if (_pathTrack == null)
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
                    CancelStroke();
                    e.Use();
                    SceneView.RepaintAll();
                }

                return;
            }

            if (e.type == EventType.MouseDown && e.button == 0 && !e.alt && hotkeySatisfied)
            {
                if (TryGetWorldPoint(sceneView, e.mousePosition, out Vector3 world))
                {
                    StartStroke(e.mousePosition, world, e.shift);
                    e.Use();
                    SceneView.RepaintAll();
                }
            }
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

        private void StartStroke(Vector2 guiPos, Vector3 worldPos, bool startedWithShift)
        {
            _strokeSamples.Clear();
            _liveStrokePath.Clear();
            _isDrawing = true;
            _strokeStartedWithShift = startedWithShift;
            _ghostActive = false;

            AddSample(guiPos, worldPos, force: true);

            if (_ghostFollowEnabled && _strokeStartedWithShift)
            {
                _ghostActive = true;
                _ghostWorldPosition = worldPos;
                _ghostTargetWorldPosition = worldPos;
                _ghostVelocity = Vector3.zero;
            }
        }

        private void CancelStroke()
        {
            _strokeSamples.Clear();
            _liveStrokePath.Clear();
            _isDrawing = false;
            _strokeStartedWithShift = false;
            _ghostActive = false;
            _ghostVelocity = Vector3.zero;
        }

        private void AddSample(Vector2 guiPos, Vector3 worldPos, bool force = false)
        {
            float now = (float)EditorApplication.timeSinceStartup;
            if (!force && _strokeSamples.Count > 0)
            {
                StrokeSample last = _strokeSamples[^1];
                float dt = now - last.timestamp;
                float dist = Vector3.Distance(last.worldPos, worldPos);
                if (dt < _minSampleInterval && dist < _minSampleDistance)
                {
                    return;
                }
            }

            _strokeSamples.Add(new StrokeSample
            {
                sceneGuiPos = guiPos,
                worldPos = worldPos,
                timestamp = now
            });

            UpdateLiveStrokePath();
            UpdateGhostFollow(now);
        }

        private void UpdateLiveStrokePath()
        {
            _liveStrokePath.Clear();
            if (_strokeSamples.Count == 0)
            {
                return;
            }

            var raw = new List<Vector3>(_strokeSamples.Count);
            for (int i = 0; i < _strokeSamples.Count; i++)
            {
                raw.Add(_strokeSamples[i].worldPos);
            }

            List<Vector3> current = ApplyIterativeSmoothing(raw, _captureSmoothingStrength, _captureSmoothingPasses);
            float simplifyTolerance = ComputeWorldTolerance(current, _captureSimplifyAmount);
            current = SimplifyPath(current, simplifyTolerance);

            if (_autoBezierOnStroke && current.Count >= 3)
            {
                current = SimplifyToBezierPath(current, _autoBezierAmount, _autoBezierDensity);
            }

            _liveStrokePath.AddRange(current);
        }

        private void UpdateGhostFollow(float now)
        {
            if (!_ghostFollowEnabled || !_strokeStartedWithShift || _strokeSamples.Count == 0)
            {
                _ghostActive = false;
                return;
            }

            _ghostTargetWorldPosition = _liveStrokePath.Count > 0
                ? _liveStrokePath[^1]
                : _strokeSamples[^1].worldPos;

            if (!_ghostActive)
            {
                _ghostActive = true;
                _ghostWorldPosition = _ghostTargetWorldPosition;
                _ghostVelocity = Vector3.zero;
                return;
            }

            float dt = 0.016f;
            if (_strokeSamples.Count >= 2)
            {
                dt = Mathf.Max(0.0001f, _strokeSamples[^1].timestamp - _strokeSamples[^2].timestamp);
            }

            _ghostWorldPosition = Vector3.SmoothDamp(
                _ghostWorldPosition,
                _ghostTargetWorldPosition,
                ref _ghostVelocity,
                _ghostFollowSmoothTime,
                Mathf.Infinity,
                dt);
        }

        private void FinishStroke()
        {
            _isDrawing = false;
            _strokeStartedWithShift = false;
            _ghostActive = false;
            _ghostVelocity = Vector3.zero;

            if (_strokeSamples.Count < 2 || !HasWorkspaceReady)
            {
                _strokeSamples.Clear();
                _liveStrokePath.Clear();
                return;
            }

            _recommendedDuration = Mathf.Max(MinDuration, _strokeSamples[^1].timestamp - _strokeSamples[0].timestamp);

            _lastStrokeSamples.Clear();
            _lastStrokeSamples.AddRange(_strokeSamples);

            List<Vector3> finalPath = _liveStrokePath.Count >= 2
                ? new List<Vector3>(_liveStrokePath)
                : _strokeSamples.Select(sample => sample.worldPos).ToList();

            finalPath = ResamplePathIfNeeded(finalPath, MaxBezierResamplePoints);
            if (finalPath.Count < 2)
            {
                _strokeSamples.Clear();
                _liveStrokePath.Clear();
                return;
            }

            AnimationCurve optimizedEase = BuildOptimizedEaseCurve(
                _lastStrokeSamples,
                _easeIntentStrength,
                _easeJitterSuppression,
                _easeKeyReduction);

            ApplyWorldPathToTrack(
                finalPath,
                optimizedEase,
                _recommendedDuration,
                "Generate Gesture Path",
                captureBaseline: true,
                clearPostHistory: true);

            _strokeSamples.Clear();
            _liveStrokePath.Clear();
            Repaint();
        }

        private void ApplySmoothingPostProcess()
        {
            if (!TryGetCurrentWorldPath(out List<Vector3> worldPath) || worldPath.Count < 3)
            {
                return;
            }

            PushPostProcessSnapshot();
            List<Vector3> smoothed = ApplyIterativeSmoothing(worldPath, _postSmoothStrength, _postSmoothPasses);
            ApplyWorldPathToTrack(
                smoothed,
                CloneCurve(_pathTrack.EaseCurve, 0f),
                Mathf.Max(MinDuration, _pathTrack.TrackDuration),
                "Smooth Gesture Path",
                captureBaseline: false,
                clearPostHistory: false);
        }

        private void ApplyBezierPostProcess()
        {
            if (!TryGetCurrentWorldPath(out List<Vector3> worldPath) || worldPath.Count < 3)
            {
                return;
            }

            PushPostProcessSnapshot();
            List<Vector3> simplified = SimplifyToBezierPath(worldPath, _postBezierAmount, _postBezierDensity);
            ApplyWorldPathToTrack(
                simplified,
                CloneCurve(_pathTrack.EaseCurve, 0f),
                Mathf.Max(MinDuration, _pathTrack.TrackDuration),
                "Bezier Simplify Gesture Path",
                captureBaseline: false,
                clearPostHistory: false);
        }

        private void ApplySmoothAndBezierPostProcess()
        {
            if (!TryGetCurrentWorldPath(out List<Vector3> worldPath) || worldPath.Count < 3)
            {
                return;
            }

            PushPostProcessSnapshot();
            List<Vector3> smoothed = ApplyIterativeSmoothing(worldPath, _postSmoothStrength, _postSmoothPasses);
            List<Vector3> simplified = SimplifyToBezierPath(smoothed, _postBezierAmount, _postBezierDensity);

            ApplyWorldPathToTrack(
                simplified,
                CloneCurve(_pathTrack.EaseCurve, 0f),
                Mathf.Max(MinDuration, _pathTrack.TrackDuration),
                "Smooth + Bezier Gesture Path",
                captureBaseline: false,
                clearPostHistory: false);
        }

        private void OptimizeEaseFromLastStroke()
        {
            if (_pathTrack == null)
            {
                return;
            }

            if (_lastStrokeSamples.Count < 2)
            {
                Debug.LogWarning("[GestureTween] 没有最近一次手绘数据，无法重建节奏缓动。");
                return;
            }

            AnimationCurve optimizedEase = BuildOptimizedEaseCurve(
                _lastStrokeSamples,
                _easeIntentStrength,
                _easeJitterSuppression,
                _easeKeyReduction);

            Undo.RecordObject(_pathTrack, "Optimize Gesture Ease");
            _pathTrack.EaseCurve = optimizedEase;
            SetDirty(_pathTrack);

            RefreshPreviewPoseIfStopped();
        }

        private void PushPostProcessSnapshot()
        {
            if (_pathTrack == null || _pathTrack.LocalPathPoints == null || _pathTrack.LocalPathPoints.Count < 2)
            {
                return;
            }

            _postProcessUndoStack.Push(new List<Vector3>(_pathTrack.LocalPathPoints));
        }

        private void UndoOnePostProcessStep()
        {
            if (_postProcessUndoStack.Count == 0)
            {
                return;
            }

            List<Vector3> offsets = _postProcessUndoStack.Pop();
            ApplyLocalOffsetsToTrack(offsets, "Undo Gesture Path Post-Process");
        }

        private void RestoreToLastStrokeBaseline()
        {
            if (_lastStrokeBaselineOffsets == null || _lastStrokeBaselineOffsets.Count < 2)
            {
                return;
            }

            PushPostProcessSnapshot();
            ApplyLocalOffsetsToTrack(_lastStrokeBaselineOffsets, "Restore Gesture Stroke Baseline");
        }

        private bool TryGetCurrentWorldPath(out List<Vector3> worldPath)
        {
            worldPath = new List<Vector3>();
            if (_pathTrack == null)
            {
                return false;
            }

            Transform target = _pathTrack.ResolveTarget();
            if (target == null)
            {
                return false;
            }

            worldPath = BuildWorldPath(target, _pathTrack.LocalPathPoints);
            return worldPath.Count >= 2;
        }
        private void ApplyLocalOffsetsToTrack(IReadOnlyList<Vector3> offsets, string undoLabel)
        {
            if (_pathTrack == null || offsets == null || offsets.Count < 2)
            {
                return;
            }

            Undo.RecordObject(_pathTrack, undoLabel);

            _pathLocalOffsets.Clear();
            for (int i = 0; i < offsets.Count; i++)
            {
                _pathLocalOffsets.Add(offsets[i]);
            }

            float duration = Mathf.Max(MinDuration, _pathTrack.TrackDuration);
            AnimationCurve ease = CloneCurve(_pathTrack.EaseCurve, 0f);

            _pathTrack.UseLocalSpace = _pathUseLocalSpace;
            _pathTrack.SetPathData(_pathLocalOffsets, ease, duration, _pathUseLocalSpace);
            _pathTrack.TrackDuration = duration;

            SetDirty(_pathTrack);
            RefreshPreviewPoseIfStopped();
        }

        private void ApplyWorldPathToTrack(
            IReadOnlyList<Vector3> worldPath,
            AnimationCurve easeCurve,
            float duration,
            string undoLabel,
            bool captureBaseline,
            bool clearPostHistory)
        {
            if (_pathTrack == null || worldPath == null || worldPath.Count < 2)
            {
                return;
            }

            Transform target = _pathTrack.ResolveTarget();
            if (target == null)
            {
                return;
            }

            List<Vector3> offsets = ConvertWorldPathToLocalOffsets(target, worldPath);
            if (offsets.Count < 2)
            {
                return;
            }

            _pathLocalOffsets.Clear();
            _pathLocalOffsets.AddRange(offsets);

            AnimationCurve ease = CloneCurve(easeCurve ?? _pathTrack.EaseCurve, 0f);
            float finalDuration = Mathf.Max(MinDuration, duration);

            Undo.RecordObject(_pathTrack, undoLabel);
            _pathTrack.UseLocalSpace = _pathUseLocalSpace;
            _pathTrack.SetPathData(_pathLocalOffsets, ease, finalDuration, _pathUseLocalSpace);
            _pathTrack.TrackDuration = finalDuration;

            _recommendedDuration = finalDuration;
            SetDirty(_pathTrack);

            if (captureBaseline)
            {
                _lastStrokeBaselineOffsets = new List<Vector3>(_pathTrack.LocalPathPoints);
            }

            if (clearPostHistory)
            {
                _postProcessUndoStack.Clear();
            }

            RefreshPreviewPoseIfStopped();
            Debug.Log($"[GestureTween] Path updated. points={_pathTrack.LocalPathPoints.Count}, duration={finalDuration:0.###}s");
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
            if (_workspace == null && forceCreate)
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
                return;
            }

            _timeline = _workspace.Timeline != null ? _workspace.Timeline : _workspace.GetComponent<DOTweenTimeline>();
            _pathTrack = _workspace.PathTrack != null ? _workspace.PathTrack : _workspace.GetComponent<GesturePathTrack>();

            _workspace.SetReferences(_timeline, _pathTrack);
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

            _pathTrack.SetPathData(
                _pathLocalOffsets,
                AnimationCurve.Linear(0f, 0f, 1f, 1f),
                Mathf.Max(MinDuration, _recommendedDuration),
                _pathUseLocalSpace);

            SetDirty(_pathTrack);

            _postProcessUndoStack.Clear();
            _lastStrokeBaselineOffsets = new List<Vector3>(_pathTrack.LocalPathPoints);

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

        private static List<Vector3> ConvertWorldPathToLocalOffsets(Transform target, IReadOnlyList<Vector3> worldPath)
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

        private static List<Vector3> ApplyIterativeSmoothing(IReadOnlyList<Vector3> raw, float strength, int passes)
        {
            if (raw == null)
            {
                return new List<Vector3>();
            }

            if (raw.Count <= 2 || strength <= 0f || passes <= 0)
            {
                return new List<Vector3>(raw);
            }

            var current = new List<Vector3>(raw);
            var next = new List<Vector3>(raw.Count);
            strength = Mathf.Clamp01(strength);
            int iterationCount = Mathf.Max(1, passes);

            for (int pass = 0; pass < iterationCount; pass++)
            {
                next.Clear();
                next.Add(current[0]);

                for (int i = 1; i < current.Count - 1; i++)
                {
                    Vector3 prev = current[i - 1];
                    Vector3 point = current[i];
                    Vector3 nextPoint = current[i + 1];

                    Vector3 laplacian = ((prev + nextPoint) * 0.5f) - point;
                    float cornerWeight = Vector3.Angle(point - prev, nextPoint - point) / 180f;
                    float adaptive = Mathf.Lerp(0.45f, 1f, cornerWeight);

                    Vector3 delta = laplacian * (strength * adaptive);
                    float maxStep = Mathf.Max(0.0001f, (nextPoint - prev).magnitude * 0.5f);
                    if (delta.magnitude > maxStep)
                    {
                        delta = delta.normalized * maxStep;
                    }

                    next.Add(point + delta);
                }

                next.Add(current[^1]);

                var temp = current;
                current = next;
                next = temp;
            }

            current[0] = raw[0];
            current[^1] = raw[^1];
            return current;
        }

        private static float ComputeWorldTolerance(IReadOnlyList<Vector3> points, float amount)
        {
            amount = Mathf.Clamp01(amount);
            float extent = Mathf.Max(0.05f, ComputeBoundsExtent(points));
            return Mathf.Lerp(0.0004f, 0.08f, amount) * extent;
        }

        private static float ComputeBoundsExtent(IReadOnlyList<Vector3> points)
        {
            if (points == null || points.Count == 0)
            {
                return 0f;
            }

            Vector3 min = points[0];
            Vector3 max = points[0];
            for (int i = 1; i < points.Count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }

            Vector3 size = max - min;
            return Mathf.Max(size.x, size.y, size.z);
        }

        private static List<Vector3> SimplifyPath(IReadOnlyList<Vector3> points, float tolerance)
        {
            if (points == null)
            {
                return new List<Vector3>();
            }

            if (points.Count <= 2 || tolerance <= 0f)
            {
                return new List<Vector3>(points);
            }

            float toleranceSq = tolerance * tolerance;
            var keep = new bool[points.Count];
            keep[0] = true;
            keep[^1] = true;

            SimplifyPathSection(points, 0, points.Count - 1, toleranceSq, keep);

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
                result[0] = points[0];
                result[^1] = points[^1];
                return result;
            }

            return new List<Vector3> { points[0], points[^1] };
        }

        private static void SimplifyPathSection(IReadOnlyList<Vector3> points, int start, int end, float toleranceSq, bool[] keep)
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
            SimplifyPathSection(points, start, index, toleranceSq, keep);
            SimplifyPathSection(points, index, end, toleranceSq, keep);
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

        private static List<Vector3> SimplifyToBezierPath(IReadOnlyList<Vector3> source, float amount, float density)
        {
            if (source == null)
            {
                return new List<Vector3>();
            }

            if (source.Count <= 2)
            {
                return new List<Vector3>(source);
            }

            amount = Mathf.Clamp01(amount);
            density = Mathf.Clamp(density, 0.3f, 2.5f);

            float rdpTolerance = ComputeWorldTolerance(source, Mathf.Lerp(0.08f, 1f, amount));
            List<Vector3> anchors = SimplifyPath(source, rdpTolerance);
            if (anchors.Count < 2)
            {
                return new List<Vector3>(source);
            }

            float tension = Mathf.Lerp(0.15f, 0.68f, amount);
            int minSamples = Mathf.RoundToInt(Mathf.Lerp(16f, 4f, amount));

            var curvePoints = new List<Vector3>(anchors.Count * 6) { anchors[0] };
            for (int i = 0; i < anchors.Count - 1; i++)
            {
                Vector3 p0 = anchors[i];
                Vector3 p1 = anchors[i + 1];
                Vector3 prev = i > 0 ? anchors[i - 1] : p0;
                Vector3 next = i + 2 < anchors.Count ? anchors[i + 2] : p1;

                Vector3 tangent0 = (p1 - prev) * (0.5f * tension);
                Vector3 tangent1 = (next - p0) * (0.5f * tension);
                Vector3 c0 = p0 + tangent0 / 3f;
                Vector3 c1 = p1 - tangent1 / 3f;

                float estimatedLength = EstimateBezierLength(p0, c0, c1, p1, 6);
                int samples = Mathf.Clamp(Mathf.RoundToInt(estimatedLength * density * 4f), minSamples, 36);

                for (int s = 1; s <= samples; s++)
                {
                    float t = s / (float)samples;
                    curvePoints.Add(EvaluateCubicBezier(p0, c0, c1, p1, t));
                }
            }

            if (curvePoints.Count > MaxBezierResamplePoints)
            {
                curvePoints = ResamplePathIfNeeded(curvePoints, MaxBezierResamplePoints);
            }

            curvePoints[0] = source[0];
            curvePoints[^1] = source[^1];
            return curvePoints;
        }

        private static Vector3 EvaluateCubicBezier(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t)
        {
            float u = 1f - t;
            float tt = t * t;
            float uu = u * u;
            float uuu = uu * u;
            float ttt = tt * t;
            return (uuu * p0) + (3f * uu * t * c0) + (3f * u * tt * c1) + (ttt * p1);
        }

        private static float EstimateBezierLength(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, int subdivisions)
        {
            float length = 0f;
            Vector3 prev = p0;
            int steps = Mathf.Max(2, subdivisions);
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                Vector3 point = EvaluateCubicBezier(p0, c0, c1, p1, t);
                length += Vector3.Distance(prev, point);
                prev = point;
            }

            return length;
        }

        private static List<Vector3> ResamplePathIfNeeded(IReadOnlyList<Vector3> source, int maxCount)
        {
            if (source == null)
            {
                return new List<Vector3>();
            }

            if (source.Count <= maxCount)
            {
                return new List<Vector3>(source);
            }

            var cumulative = new float[source.Count];
            float total = 0f;
            cumulative[0] = 0f;
            for (int i = 1; i < source.Count; i++)
            {
                total += Vector3.Distance(source[i - 1], source[i]);
                cumulative[i] = total;
            }

            if (total <= 0.0001f)
            {
                return new List<Vector3> { source[0], source[^1] };
            }

            var result = new List<Vector3>(maxCount) { source[0] };
            int segmentIndex = 1;
            for (int i = 1; i < maxCount - 1; i++)
            {
                float targetLength = total * (i / (float)(maxCount - 1));
                while (segmentIndex < cumulative.Length - 1 && cumulative[segmentIndex] < targetLength)
                {
                    segmentIndex++;
                }

                int prevIndex = Mathf.Max(0, segmentIndex - 1);
                float a = cumulative[prevIndex];
                float b = cumulative[segmentIndex];
                float segmentT = b > a ? Mathf.Clamp01((targetLength - a) / (b - a)) : 0f;
                result.Add(Vector3.Lerp(source[prevIndex], source[segmentIndex], segmentT));
            }

            result.Add(source[^1]);
            return result;
        }

        private static AnimationCurve BuildOptimizedEaseCurve(
            IReadOnlyList<StrokeSample> samples,
            float intentStrength,
            float jitterSuppression,
            float keyReduction)
        {
            if (samples == null || samples.Count < 2)
            {
                return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            }

            int segmentCount = samples.Count - 1;
            var segmentDurations = new float[segmentCount];
            var segmentSpeeds = new float[segmentCount];
            float maxSpeed = 0f;

            for (int i = 0; i < segmentCount; i++)
            {
                float dt = Mathf.Max(0.0005f, samples[i + 1].timestamp - samples[i].timestamp);
                float distance = Vector3.Distance(samples[i + 1].worldPos, samples[i].worldPos);
                float speed = distance / dt;

                segmentDurations[i] = dt;
                segmentSpeeds[i] = speed;
                maxSpeed = Mathf.Max(maxSpeed, speed);
            }

            if (maxSpeed <= 0.0001f)
            {
                return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            }

            int halfWindow = Mathf.RoundToInt(Mathf.Lerp(1f, 4f, Mathf.Clamp01(jitterSuppression)));
            int medianWindow = Mathf.Clamp(2 * halfWindow + 1, 3, 11);

            float[] medianFiltered = MedianFilter(segmentSpeeds, medianWindow);
            float emaAlpha = Mathf.Lerp(0.68f, 0.22f, Mathf.Clamp01(jitterSuppression));
            float[] speedEnvelope = ExponentialSmooth(medianFiltered, emaAlpha);

            float clampedIntent = Mathf.Clamp01(intentStrength);
            float gamma = Mathf.Lerp(0.8f, 1.6f, clampedIntent);

            var cumulative = new float[samples.Count];
            float totalWeight = 0f;
            for (int i = 0; i < segmentCount; i++)
            {
                float normalizedSpeed = Mathf.Clamp01(speedEnvelope[i] / maxSpeed);
                float shaped = Mathf.Pow(Mathf.Max(0.02f, normalizedSpeed), gamma);

                float weight = Mathf.Lerp(1f, shaped, clampedIntent);
                weight = Mathf.Lerp(weight, 1f, jitterSuppression * 0.35f);

                totalWeight += weight * segmentDurations[i];
                cumulative[i + 1] = totalWeight;
            }

            if (totalWeight <= 0.0001f)
            {
                return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            }

            float startTime = samples[0].timestamp;
            float totalTime = Mathf.Max(0.0005f, samples[^1].timestamp - startTime);

            var points = new List<Vector2>(samples.Count);
            points.Add(new Vector2(0f, 0f));

            for (int i = 1; i < samples.Count - 1; i++)
            {
                float t = Mathf.Clamp01((samples[i].timestamp - startTime) / totalTime);
                float p = Mathf.Clamp01(cumulative[i] / totalWeight);
                p = Mathf.Max(p, points[^1].y + 0.0002f);
                points.Add(new Vector2(t, p));
            }

            points.Add(new Vector2(1f, 1f));

            if (points.Count > 3 && jitterSuppression > 0f)
            {
                for (int i = 1; i < points.Count - 1; i++)
                {
                    float left = points[i - 1].y;
                    float current = points[i].y;
                    float right = points[i + 1].y;
                    float softened = Mathf.Lerp(current, (left + right) * 0.5f, jitterSuppression * 0.5f);
                    float clamped = Mathf.Clamp(softened, left + 0.0001f, right - 0.0001f);
                    points[i] = new Vector2(points[i].x, clamped);
                }
            }

            float simplifyTolerance = Mathf.Lerp(0.0005f, 0.035f, Mathf.Clamp01(keyReduction));
            List<Vector2> simplified = Simplify2D(points, simplifyTolerance);

            var keys = new Keyframe[simplified.Count];
            for (int i = 0; i < simplified.Count; i++)
            {
                float time = Mathf.Clamp01(simplified[i].x);
                float value = Mathf.Clamp01(simplified[i].y);

                if (i > 0)
                {
                    value = Mathf.Max(value, keys[i - 1].value + 0.0001f);
                }

                keys[i] = new Keyframe(time, value);
            }

            keys[0] = new Keyframe(0f, 0f);
            keys[^1] = new Keyframe(1f, 1f);

            var curve = new AnimationCurve(keys);
            SmoothCurveTangents(curve);
            return curve;
        }

        private static float[] MedianFilter(float[] source, int windowSize)
        {
            if (source == null || source.Length == 0)
            {
                return Array.Empty<float>();
            }

            int half = Mathf.Max(1, windowSize / 2);
            var output = new float[source.Length];
            var buffer = new List<float>(windowSize);

            for (int i = 0; i < source.Length; i++)
            {
                buffer.Clear();
                int from = Mathf.Max(0, i - half);
                int to = Mathf.Min(source.Length - 1, i + half);
                for (int j = from; j <= to; j++)
                {
                    buffer.Add(source[j]);
                }

                buffer.Sort();
                output[i] = buffer[buffer.Count / 2];
            }

            return output;
        }

        private static float[] ExponentialSmooth(float[] source, float alpha)
        {
            if (source == null || source.Length == 0)
            {
                return Array.Empty<float>();
            }

            alpha = Mathf.Clamp01(alpha);
            var forward = new float[source.Length];
            var backward = new float[source.Length];

            forward[0] = source[0];
            for (int i = 1; i < source.Length; i++)
            {
                forward[i] = Mathf.Lerp(forward[i - 1], source[i], alpha);
            }

            backward[^1] = forward[^1];
            float backwardAlpha = Mathf.Clamp01(alpha * 0.5f);
            for (int i = source.Length - 2; i >= 0; i--)
            {
                backward[i] = Mathf.Lerp(backward[i + 1], forward[i], backwardAlpha);
            }

            return backward;
        }

        private static List<Vector2> Simplify2D(IReadOnlyList<Vector2> points, float tolerance)
        {
            if (points == null)
            {
                return new List<Vector2>();
            }

            if (points.Count <= 2 || tolerance <= 0f)
            {
                return new List<Vector2>(points);
            }

            float toleranceSq = tolerance * tolerance;
            var keep = new bool[points.Count];
            keep[0] = true;
            keep[^1] = true;

            Simplify2DSection(points, 0, points.Count - 1, toleranceSq, keep);

            var result = new List<Vector2>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                {
                    result.Add(points[i]);
                }
            }

            if (result.Count >= 2)
            {
                result[0] = points[0];
                result[^1] = points[^1];
                return result;
            }

            return new List<Vector2> { points[0], points[^1] };
        }

        private static void Simplify2DSection(IReadOnlyList<Vector2> points, int start, int end, float toleranceSq, bool[] keep)
        {
            if (end <= start + 1)
            {
                return;
            }

            int index = -1;
            float maxDistSq = 0f;
            Vector2 a = points[start];
            Vector2 b = points[end];

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
            Simplify2DSection(points, start, index, toleranceSq, keep);
            Simplify2DSection(points, index, end, toleranceSq, keep);
        }

        private static float DistancePointToSegmentSq(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float abLenSq = ab.sqrMagnitude;
            if (abLenSq <= 0.000001f)
            {
                return (point - a).sqrMagnitude;
            }

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / abLenSq);
            Vector2 projection = a + t * ab;
            return (point - projection).sqrMagnitude;
        }
    }
}

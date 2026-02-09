using System;
using System.Collections.Generic;
using DG.Tweening;
using UnityEditor;
using UnityEngine;

namespace GestureTween.Editor
{
    /// <summary>
    /// 场景内手绘动效工具：
    /// 1) SceneView 直接绘制
    /// 2) 生成路径/速度/缩放/旋转通道
    /// 3) 输出 Preset 并一键应用
    /// </summary>
    public class GestureCurveWindow : EditorWindow
    {
        private enum GestureChannel
        {
            PositionPath,
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

        private const string PresetsRoot = "Assets/GestureTween/Presets";
        private const int PathTypeCatmullRom = 1;
        private const int PathModeTopDown2D = 1;

        private static readonly Type DotweenPathType = Type.GetType("DG.Tweening.DOTweenPath, DOTweenPro");

        private readonly List<StrokeSample> _samples = new();
        private readonly List<Vector3> _positionPathLocalOffsets = new();

        private bool _isDrawing;
        private bool _sceneDrawingEnabled = true;
        private bool _requireShiftToDraw = true;
        private bool _autoReturnToRest = true;
        private bool _useLocalSpace = true;
        private GestureChannel _activeChannel = GestureChannel.PositionPath;
        private DrawPlane _drawPlane = DrawPlane.XY;
        private Transform _target;

        private float _smoothing = 0.35f;
        private float _pathSimplifyTolerance = 0.03f;
        private float _minSampleDistance = 0.02f;
        private float _minSampleInterval = 0.01f;
        private float _scaleSensitivity = 0.01f;
        private float _rotationSensitivity = 0.35f;
        private float _recommendedDuration = 0.8f;

        private bool _hasPositionPath;
        private bool _hasScaleChannel;
        private bool _hasRotationChannel;

        private AnimationCurve _generatedEaseCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        private AnimationCurve _generatedScaleCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        private AnimationCurve _generatedRotationCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        private string _description = string.Empty;

        private Tween _previewTween;
        private float _previewProgress;

        [MenuItem("Window/GestureTween/Scene Motion Painter")]
        public static void ShowWindow()
        {
            var window = GetWindow<GestureCurveWindow>("GestureTween");
            window.minSize = new Vector2(500f, 620f);
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Selection.selectionChanged += OnSelectionChanged;
            if (_target == null && Selection.activeTransform != null)
            {
                _target = Selection.activeTransform;
            }
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Selection.selectionChanged -= OnSelectionChanged;
            _previewTween?.Kill();
            _previewTween = null;
        }

        private void OnSelectionChanged()
        {
            if (_target == null || !_target.gameObject.scene.IsValid())
            {
                _target = Selection.activeTransform;
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("GestureTween Scene Motion Painter", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("在 Scene 视图直接绘制。默认按住 Shift + 鼠标左键开始录制，可分通道叠加。", MessageType.Info);

            DrawTargetAndRecordOptions();
            EditorGUILayout.Space(8);
            DrawChannelConfig();
            EditorGUILayout.Space(8);
            DrawGeneratedDataSection();
            EditorGUILayout.Space(8);
            DrawActionButtons();

            if (_isDrawing || (_previewTween != null && _previewTween.IsActive()))
            {
                Repaint();
            }
        }

        private void DrawTargetAndRecordOptions()
        {
            _target = (Transform)EditorGUILayout.ObjectField("目标对象", _target, typeof(Transform), true);
            _sceneDrawingEnabled = EditorGUILayout.ToggleLeft("启用 Scene 绘制", _sceneDrawingEnabled);
            _requireShiftToDraw = EditorGUILayout.ToggleLeft("仅在按住 Shift 时录制", _requireShiftToDraw);
            _useLocalSpace = EditorGUILayout.ToggleLeft("应用路径时使用 Local 空间", _useLocalSpace);
            _autoReturnToRest = EditorGUILayout.ToggleLeft("Scale/Rotation 自动回归初始值", _autoReturnToRest);
            _drawPlane = (DrawPlane)EditorGUILayout.EnumPopup("绘制平面", _drawPlane);

            EditorGUILayout.BeginHorizontal();
            _smoothing = EditorGUILayout.Slider("平滑强度", _smoothing, 0f, 1f);
            _recommendedDuration = EditorGUILayout.FloatField("时长(秒)", Mathf.Max(0.05f, _recommendedDuration));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            _minSampleDistance = EditorGUILayout.Slider("采样最小距离", _minSampleDistance, 0.001f, 0.2f);
            _minSampleInterval = EditorGUILayout.Slider("采样最小间隔", _minSampleInterval, 0f, 0.08f);
            EditorGUILayout.EndHorizontal();

            if (GetTarget() == null)
            {
                EditorGUILayout.HelpBox("请先选择一个目标对象（Transform）。", MessageType.Warning);
            }
        }

        private void DrawChannelConfig()
        {
            _activeChannel = (GestureChannel)GUILayout.Toolbar((int)_activeChannel, new[] { "Position Path", "Scale", "Rotation" });

            switch (_activeChannel)
            {
                case GestureChannel.PositionPath:
                    _pathSimplifyTolerance = EditorGUILayout.Slider("路径简化容差", _pathSimplifyTolerance, 0f, 0.3f);
                    break;

                case GestureChannel.Scale:
                    _scaleSensitivity = EditorGUILayout.Slider("Scale 灵敏度", _scaleSensitivity, 0.001f, 0.05f);
                    EditorGUILayout.HelpBox("手势向上拖动会放大，向下拖动会缩小。", MessageType.None);
                    break;

                case GestureChannel.Rotation:
                    _rotationSensitivity = EditorGUILayout.Slider("Rotation 灵敏度", _rotationSensitivity, 0.05f, 2f);
                    EditorGUILayout.HelpBox("手势向右拖动为顺时针增量，向左为反向。", MessageType.None);
                    break;
            }
        }

        private void DrawGeneratedDataSection()
        {
            EditorGUILayout.LabelField("生成结果", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"Position Path: {(_hasPositionPath ? $"Yes ({_positionPathLocalOffsets.Count} points)" : "No")}");
            EditorGUILayout.LabelField($"Scale Channel: {(_hasScaleChannel ? "Yes" : "No")}");
            EditorGUILayout.LabelField($"Rotation Channel: {(_hasRotationChannel ? "Yes" : "No")}");

            _generatedEaseCurve = EditorGUILayout.CurveField("速度曲线 (Ease)", _generatedEaseCurve, Color.cyan, new Rect(0f, 0f, 1f, 1f));
            _generatedScaleCurve = EditorGUILayout.CurveField("Scale 曲线", _generatedScaleCurve, Color.green, new Rect(0f, 0f, 1.5f, 2f));
            _generatedRotationCurve = EditorGUILayout.CurveField("Rotation 曲线", _generatedRotationCurve, Color.yellow, new Rect(0f, -90f, 1f, 180f));
            _description = EditorGUILayout.TextField("描述", _description);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("预览 Ease 进度", GUILayout.Width(120)))
            {
                PlayEasePreview();
            }

            EditorGUILayout.LabelField($"进度: {_previewProgress:P0}", GUILayout.Width(80));
            var progressRect = GUILayoutUtility.GetRect(100, 18f);
            EditorGUI.ProgressBar(progressRect, _previewProgress, string.Empty);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawActionButtons()
        {
            EditorGUILayout.LabelField("应用与导出", EditorStyles.miniBoldLabel);

            EditorGUI.BeginDisabledGroup(!_hasPositionPath && !_hasScaleChannel && !_hasRotationChannel);
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("保存为 Preset 资产"))
            {
                SaveAsPreset();
            }

            if (GUILayout.Button("应用到 DOTweenPath"))
            {
                ApplyToDotweenPath();
            }

            if (GUILayout.Button("应用到 DOTweenAnimation"))
            {
                ApplyToDotweenAnimation();
            }

            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("应用到 GestureTweenPlayer（自动生成预设）"))
            {
                ApplyToGesturePlayer();
            }

            EditorGUI.EndDisabledGroup();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("清空当前笔画"))
            {
                _samples.Clear();
                _isDrawing = false;
                SceneView.RepaintAll();
            }

            if (GUILayout.Button("清空所有通道"))
            {
                ClearGeneratedChannels();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void OnSceneGUI(SceneView sceneView)
        {
            if (!_sceneDrawingEnabled) return;

            DrawStrokeInScene();
            DrawGeneratedPathInScene();
            HandleSceneInput(sceneView);
        }

        private void DrawStrokeInScene()
        {
            if (_samples.Count < 2) return;

            Handles.color = new Color(1f, 0.55f, 0.1f, 0.95f);
            for (int i = 0; i < _samples.Count - 1; i++)
            {
                Handles.DrawLine(_samples[i].worldPos, _samples[i + 1].worldPos);
            }
        }

        private void DrawGeneratedPathInScene()
        {
            Transform target = GetTarget();
            if (!_hasPositionPath || target == null || _positionPathLocalOffsets.Count < 2) return;

            List<Vector3> worldPath = BuildWorldPathForTarget(target);
            if (worldPath.Count < 2) return;

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

        private void HandleSceneInput(SceneView sceneView)
        {
            Transform target = GetTarget();
            if (target == null) return;

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

        private bool TryGetWorldPoint(SceneView sceneView, Vector2 guiPosition, out Vector3 worldPoint)
        {
            Transform target = GetTarget();
            worldPoint = Vector3.zero;
            if (target == null) return false;

            Ray ray = HandleUtility.GUIPointToWorldRay(guiPosition);
            Vector3 planePoint = target.position;
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
            }

            Plane plane = new Plane(normal, planePoint);
            if (!plane.Raycast(ray, out float enter)) return false;
            worldPoint = ray.GetPoint(enter);
            return true;
        }

        private void StartStroke(Vector2 guiPos, Vector3 worldPos)
        {
            _samples.Clear();
            _isDrawing = true;
            AddSample(guiPos, worldPos, true);
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
            if (_samples.Count < 2) return;

            _recommendedDuration = Mathf.Max(0.05f, _samples[^1].timestamp - _samples[0].timestamp);

            switch (_activeChannel)
            {
                case GestureChannel.PositionPath:
                    GeneratePositionChannel();
                    break;
                case GestureChannel.Scale:
                    GenerateScaleChannel();
                    break;
                case GestureChannel.Rotation:
                    GenerateRotationChannel();
                    break;
            }

            _samples.Clear();
            Repaint();
        }

        private void GeneratePositionChannel()
        {
            Transform target = GetTarget();
            if (target == null) return;

            var raw = new List<Vector3>(_samples.Count);
            foreach (StrokeSample sample in _samples)
            {
                raw.Add(sample.worldPos);
            }

            List<Vector3> smoothed = SmoothPath(raw, _smoothing);
            List<Vector3> simplified = SimplifyPath(smoothed, _pathSimplifyTolerance);
            if (simplified.Count < 2) return;

            _positionPathLocalOffsets.Clear();
            _positionPathLocalOffsets.AddRange(ConvertWorldPathToLocalOffsets(target, simplified));

            _generatedEaseCurve = GenerateEaseCurve(_samples, _smoothing);
            _hasPositionPath = _positionPathLocalOffsets.Count >= 2;

            Debug.Log($"[GestureTween] Position channel generated. Points: {_positionPathLocalOffsets.Count}, duration: {_recommendedDuration:0.###}s");
        }

        private void GenerateScaleChannel()
        {
            if (_samples.Count < 2) return;

            Vector2 start = _samples[0].sceneGuiPos;
            _generatedScaleCurve = GenerateValueCurve(_samples, s =>
            {
                float delta = start.y - s.sceneGuiPos.y;
                return Mathf.Max(0.05f, 1f + delta * _scaleSensitivity);
            }, 1f);

            _hasScaleChannel = _generatedScaleCurve != null && _generatedScaleCurve.length >= 2;
            Debug.Log("[GestureTween] Scale channel generated.");
        }

        private void GenerateRotationChannel()
        {
            if (_samples.Count < 2) return;

            Vector2 start = _samples[0].sceneGuiPos;
            _generatedRotationCurve = GenerateValueCurve(_samples, s =>
            {
                float delta = s.sceneGuiPos.x - start.x;
                return delta * _rotationSensitivity;
            }, 0f);

            _hasRotationChannel = _generatedRotationCurve != null && _generatedRotationCurve.length >= 2;
            Debug.Log("[GestureTween] Rotation channel generated.");
        }

        private AnimationCurve GenerateEaseCurve(List<StrokeSample> samples, float smoothing)
        {
            int count = samples.Count;
            if (count < 2) return AnimationCurve.Linear(0f, 0f, 1f, 1f);

            var speeds = new float[count - 1];
            float maxSpeed = 0f;
            for (int i = 1; i < count; i++)
            {
                float dt = Mathf.Max(0.0001f, samples[i].timestamp - samples[i - 1].timestamp);
                float dist = Vector3.Distance(samples[i].worldPos, samples[i - 1].worldPos);
                float speed = dist / dt;
                speeds[i - 1] = speed;
                if (speed > maxSpeed) maxSpeed = speed;
            }

            if (maxSpeed < 0.0001f) maxSpeed = 1f;

            var cumulative = new float[count];
            float totalWeight = 0f;
            for (int i = 1; i < count; i++)
            {
                float normalizedSpeed = Mathf.Clamp01(speeds[i - 1] / maxSpeed);
                float weight = Mathf.Lerp(1f, Mathf.Max(0.05f, normalizedSpeed), 1f - smoothing);
                totalWeight += weight;
                cumulative[i] = totalWeight;
            }

            if (totalWeight < 0.0001f) totalWeight = 1f;

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

        private AnimationCurve GenerateValueCurve(List<StrokeSample> samples, Func<StrokeSample, float> evaluator, float restValue)
        {
            int count = samples.Count;
            if (count < 2) return new AnimationCurve(new Keyframe(0f, restValue), new Keyframe(1f, restValue));

            float startTime = samples[0].timestamp;
            float totalTime = Mathf.Max(0.0001f, samples[^1].timestamp - startTime);

            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = evaluator(samples[i]);
            }

            int radius = Mathf.RoundToInt(Mathf.Lerp(0f, 4f, _smoothing));
            if (radius > 0)
            {
                var smoothed = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float sum = 0f;
                    int c = 0;
                    int from = Mathf.Max(0, i - radius);
                    int to = Mathf.Min(count - 1, i + radius);
                    for (int j = from; j <= to; j++)
                    {
                        sum += values[j];
                        c++;
                    }
                    smoothed[i] = c > 0 ? sum / c : values[i];
                }
                values = smoothed;
            }

            var keys = new Keyframe[count];
            for (int i = 0; i < count; i++)
            {
                float t = Mathf.Clamp01((samples[i].timestamp - startTime) / totalTime);
                keys[i] = new Keyframe(t, values[i]);
            }

            keys[0].value = restValue;
            if (_autoReturnToRest)
            {
                keys[^1].value = restValue;
            }

            var curve = new AnimationCurve(keys);
            SmoothCurveTangents(curve);
            return curve;
        }

        private static void SmoothCurveTangents(AnimationCurve curve)
        {
            if (curve == null) return;
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.ClampedAuto);
            }
        }

        private void PlayEasePreview()
        {
            _previewTween?.Kill();
            _previewProgress = 0f;

            AnimationCurve curve = _generatedEaseCurve != null && _generatedEaseCurve.length >= 2
                ? _generatedEaseCurve
                : AnimationCurve.Linear(0f, 0f, 1f, 1f);

            _previewTween = DOVirtual.Float(0f, 1f, Mathf.Max(0.05f, _recommendedDuration), v =>
            {
                _previewProgress = v;
                Repaint();
            }).SetEase(curve).OnComplete(() =>
            {
                _previewProgress = 1f;
                Repaint();
            });
        }

        private void SaveAsPreset()
        {
            EnsureFolder(PresetsRoot);

            string path = EditorUtility.SaveFilePanelInProject(
                "保存 GestureTween Preset",
                "GestureMotion",
                "asset",
                "选择保存位置",
                PresetsRoot
            );

            if (string.IsNullOrEmpty(path))
            {
                Debug.Log("[GestureTween] Save cancelled.");
                return;
            }

            GestureCurvePreset preset = AssetDatabase.LoadAssetAtPath<GestureCurvePreset>(path);
            if (preset == null)
            {
                preset = CreateInstance<GestureCurvePreset>();
                AssetDatabase.CreateAsset(preset, path);
            }

            PopulatePreset(preset);
            EditorUtility.SetDirty(preset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = preset;
            Debug.Log($"[GestureTween] Preset saved: {path}");
        }

        private void ApplyToDotweenAnimation()
        {
            Transform target = GetTarget();
            if (target == null)
            {
                EditorUtility.DisplayDialog("GestureTween", "请先指定目标对象。", "确定");
                return;
            }

            DOTweenAnimation anim = target.GetComponent<DOTweenAnimation>();
            if (anim == null)
            {
                anim = Undo.AddComponent<DOTweenAnimation>(target.gameObject);
            }

            Undo.RecordObject(anim, "Apply Gesture Ease Curve");
            anim.easeType = Ease.INTERNAL_Custom;
            anim.easeCurve = CloneCurve(_generatedEaseCurve);
            anim.duration = Mathf.Max(0.05f, _recommendedDuration);
            EditorUtility.SetDirty(anim);
            PrefabUtility.RecordPrefabInstancePropertyModifications(anim);

            Debug.Log($"[GestureTween] Ease curve applied to DOTweenAnimation: {target.name}");
        }

        private void ApplyToDotweenPath()
        {
            Transform target = GetTarget();
            if (target == null)
            {
                EditorUtility.DisplayDialog("GestureTween", "请先指定目标对象。", "确定");
                return;
            }

            if (!_hasPositionPath || _positionPathLocalOffsets.Count < 2)
            {
                EditorUtility.DisplayDialog("GestureTween", "当前没有可用的 Position Path 通道。", "确定");
                return;
            }

            if (DotweenPathType == null)
            {
                EditorUtility.DisplayDialog("GestureTween", "未找到 DOTweenPath 类型（确认 DOTween Pro 已正确导入）。", "确定");
                return;
            }

            Component pathComp = target.GetComponent(DotweenPathType);
            if (pathComp == null)
            {
                pathComp = Undo.AddComponent(target.gameObject, DotweenPathType);
            }

            List<Vector3> pathPoints = BuildAbsolutePathForTarget(target, _useLocalSpace);
            if (pathPoints.Count < 2)
            {
                EditorUtility.DisplayDialog("GestureTween", "路径点数量不足，无法应用。", "确定");
                return;
            }

            Undo.RecordObject(pathComp, "Apply Gesture Path");
            var so = new SerializedObject(pathComp);

            SetFloat(so, "duration", Mathf.Max(0.05f, _recommendedDuration));
            SetInt(so, "easeType", (int)Ease.INTERNAL_Custom);
            SetCurve(so, "easeCurve", CloneCurve(_generatedEaseCurve));
            SetBool(so, "isLocal", _useLocalSpace);
            SetBool(so, "isClosedPath", false);
            SetInt(so, "pathType", PathTypeCatmullRom);
            SetInt(so, "pathMode", PathModeTopDown2D);
            SetBool(so, "autoPlay", false);
            SetBool(so, "autoKill", true);
            SetInt(so, "loops", 1);

            WriteVector3Array(so, "wps", pathPoints, true);
            ClearVector3Array(so, "fullWps");

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(pathComp);
            PrefabUtility.RecordPrefabInstancePropertyModifications(pathComp);

            Debug.Log($"[GestureTween] Path applied to DOTweenPath: {target.name}");
        }

        private void ApplyToGesturePlayer()
        {
            Transform target = GetTarget();
            if (target == null)
            {
                EditorUtility.DisplayDialog("GestureTween", "请先指定目标对象。", "确定");
                return;
            }

            EnsureFolder(PresetsRoot);
            EnsureFolder($"{PresetsRoot}/Auto");

            string assetPath = $"{PresetsRoot}/Auto/{SanitizeFileName(target.name)}_GestureMotion.asset";
            GestureCurvePreset preset = AssetDatabase.LoadAssetAtPath<GestureCurvePreset>(assetPath);
            if (preset == null)
            {
                preset = CreateInstance<GestureCurvePreset>();
                AssetDatabase.CreateAsset(preset, assetPath);
            }

            PopulatePreset(preset);
            EditorUtility.SetDirty(preset);
            AssetDatabase.SaveAssets();

            Type playerType = Type.GetType("GestureTween.GestureTweenPlayer, Assembly-CSharp");
            if (playerType == null)
            {
                EditorUtility.DisplayDialog("GestureTween", "未找到 GestureTweenPlayer 类型（建议让 Unity 重新生成项目后再试）。", "确定");
                return;
            }

            Component player = target.GetComponent(playerType);
            if (player == null)
            {
                player = Undo.AddComponent(target.gameObject, playerType);
            }

            Undo.RecordObject(player, "Apply GestureTween Player");
            var presetProperty = playerType.GetProperty("Preset");
            presetProperty?.SetValue(player, preset);
            EditorUtility.SetDirty(player);
            PrefabUtility.RecordPrefabInstancePropertyModifications(player);

            Debug.Log($"[GestureTween] GestureTweenPlayer updated: {target.name} -> {assetPath}");
        }

        private void PopulatePreset(GestureCurvePreset preset)
        {
            preset.recommendedDuration = Mathf.Max(0.05f, _recommendedDuration);
            preset.easeCurve = CloneCurve(_generatedEaseCurve);
            preset.description = string.IsNullOrWhiteSpace(_description)
                ? $"Gesture generated {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                : _description;

            preset.hasPositionPath = _hasPositionPath && _positionPathLocalOffsets.Count >= 2;
            if (preset.localPathPoints == null) preset.localPathPoints = new List<Vector3>();
            preset.localPathPoints.Clear();
            if (preset.hasPositionPath)
            {
                preset.localPathPoints.AddRange(_positionPathLocalOffsets);
            }
            else
            {
                preset.localPathPoints.Add(Vector3.zero);
                preset.localPathPoints.Add(Vector3.right);
            }

            preset.hasScaleChannel = _hasScaleChannel;
            preset.scaleCurve = CloneCurve(_generatedScaleCurve);

            preset.hasRotationChannel = _hasRotationChannel;
            preset.rotationCurve = CloneCurve(_generatedRotationCurve);
        }

        private void ClearGeneratedChannels()
        {
            _samples.Clear();
            _positionPathLocalOffsets.Clear();

            _hasPositionPath = false;
            _hasScaleChannel = false;
            _hasRotationChannel = false;

            _generatedEaseCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            _generatedScaleCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
            _generatedRotationCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);
            _description = string.Empty;

            _previewTween?.Kill();
            _previewTween = null;
            _previewProgress = 0f;

            SceneView.RepaintAll();
            Repaint();
        }

        private Transform GetTarget()
        {
            if (_target != null) return _target;
            if (Selection.activeTransform != null)
            {
                _target = Selection.activeTransform;
            }

            return _target;
        }

        private List<Vector3> BuildAbsolutePathForTarget(Transform target, bool useLocalSpace)
        {
            var points = new List<Vector3>(_positionPathLocalOffsets.Count);
            if (_positionPathLocalOffsets.Count < 2) return points;

            Transform parent = target.parent;
            Vector3 startLocal = target.localPosition;

            foreach (Vector3 offset in _positionPathLocalOffsets)
            {
                Vector3 localPoint = startLocal + offset;
                Vector3 absolute = useLocalSpace
                    ? localPoint
                    : (parent != null ? parent.TransformPoint(localPoint) : localPoint);
                points.Add(absolute);
            }

            return points;
        }

        private List<Vector3> BuildWorldPathForTarget(Transform target)
        {
            var points = new List<Vector3>(_positionPathLocalOffsets.Count);
            if (_positionPathLocalOffsets.Count < 2) return points;

            Transform parent = target.parent;
            Vector3 startLocal = target.localPosition;
            foreach (Vector3 offset in _positionPathLocalOffsets)
            {
                Vector3 localPoint = startLocal + offset;
                Vector3 world = parent != null ? parent.TransformPoint(localPoint) : localPoint;
                points.Add(world);
            }

            return points;
        }

        private static List<Vector3> ConvertWorldPathToLocalOffsets(Transform target, List<Vector3> worldPath)
        {
            var offsets = new List<Vector3>(worldPath.Count);
            if (worldPath.Count == 0) return offsets;

            Transform parent = target.parent;
            Vector3 startLocal = parent != null ? parent.InverseTransformPoint(worldPath[0]) : worldPath[0];

            foreach (Vector3 world in worldPath)
            {
                Vector3 local = parent != null ? parent.InverseTransformPoint(world) : world;
                offsets.Add(local - startLocal);
            }

            return offsets;
        }

        private static List<Vector3> SmoothPath(List<Vector3> raw, float smoothing)
        {
            if (raw.Count <= 2) return new List<Vector3>(raw);

            int radius = Mathf.RoundToInt(Mathf.Lerp(0f, 4f, Mathf.Clamp01(smoothing)));
            if (radius == 0) return new List<Vector3>(raw);

            var result = new List<Vector3>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                Vector3 sum = Vector3.zero;
                int c = 0;
                int from = Mathf.Max(0, i - radius);
                int to = Mathf.Min(raw.Count - 1, i + radius);
                for (int j = from; j <= to; j++)
                {
                    sum += raw[j];
                    c++;
                }

                result.Add(c > 0 ? sum / c : raw[i]);
            }

            result[0] = raw[0];
            result[^1] = raw[^1];
            return result;
        }

        private static List<Vector3> SimplifyPath(List<Vector3> points, float tolerance)
        {
            if (points.Count <= 2 || tolerance <= 0f) return new List<Vector3>(points);

            float toleranceSq = tolerance * tolerance;
            var keep = new bool[points.Count];
            keep[0] = true;
            keep[^1] = true;
            SimplifySection(points, 0, points.Count - 1, toleranceSq, keep);

            var result = new List<Vector3>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i]) result.Add(points[i]);
            }

            if (result.Count < 2)
            {
                result.Clear();
                result.Add(points[0]);
                result.Add(points[^1]);
            }

            return result;
        }

        private static void SimplifySection(List<Vector3> points, int start, int end, float toleranceSq, bool[] keep)
        {
            if (end <= start + 1) return;

            int index = -1;
            float maxDistSq = 0f;
            Vector3 a = points[start];
            Vector3 b = points[end];

            for (int i = start + 1; i < end; i++)
            {
                float distSq = DistancePointToSegmentSq(points[i], a, b);
                if (distSq > maxDistSq)
                {
                    maxDistSq = distSq;
                    index = i;
                }
            }

            if (index == -1 || maxDistSq <= toleranceSq) return;

            keep[index] = true;
            SimplifySection(points, start, index, toleranceSq, keep);
            SimplifySection(points, index, end, toleranceSq, keep);
        }

        private static float DistancePointToSegmentSq(Vector3 point, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abLenSq = ab.sqrMagnitude;
            if (abLenSq <= 0.000001f) return (point - a).sqrMagnitude;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / abLenSq);
            Vector3 projection = a + t * ab;
            return (point - projection).sqrMagnitude;
        }

        private static AnimationCurve CloneCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length < 2)
            {
                return AnimationCurve.Linear(0f, 0f, 1f, 1f);
            }

            return new AnimationCurve(curve.keys);
        }

        private static void EnsureFolder(string folderPath)
        {
            if (AssetDatabase.IsValidFolder(folderPath)) return;

            string[] parts = folderPath.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in invalid)
            {
                name = name.Replace(c, '_');
            }

            return string.IsNullOrWhiteSpace(name) ? "GestureTarget" : name;
        }

        private static void SetBool(SerializedObject so, string propName, bool value)
        {
            SerializedProperty p = so.FindProperty(propName);
            if (p != null) p.boolValue = value;
        }

        private static void SetInt(SerializedObject so, string propName, int value)
        {
            SerializedProperty p = so.FindProperty(propName);
            if (p != null) p.intValue = value;
        }

        private static void SetFloat(SerializedObject so, string propName, float value)
        {
            SerializedProperty p = so.FindProperty(propName);
            if (p != null) p.floatValue = value;
        }

        private static void SetCurve(SerializedObject so, string propName, AnimationCurve value)
        {
            SerializedProperty p = so.FindProperty(propName);
            if (p != null) p.animationCurveValue = value;
        }

        private static void WriteVector3Array(SerializedObject so, string propName, List<Vector3> points, bool skipFirst)
        {
            SerializedProperty p = so.FindProperty(propName);
            if (p == null || !p.isArray) return;

            int start = skipFirst ? 1 : 0;
            int count = Mathf.Max(0, points.Count - start);
            p.arraySize = count;
            for (int i = 0; i < count; i++)
            {
                p.GetArrayElementAtIndex(i).vector3Value = points[i + start];
            }
        }

        private static void ClearVector3Array(SerializedObject so, string propName)
        {
            SerializedProperty p = so.FindProperty(propName);
            if (p != null && p.isArray)
            {
                p.arraySize = 0;
            }
        }
    }
}

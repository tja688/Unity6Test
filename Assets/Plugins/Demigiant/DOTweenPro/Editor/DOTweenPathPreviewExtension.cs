using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Core.PathCore;
using DG.Tweening.Plugins.Options;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace DG.DOTweenEditor
{
    [InitializeOnLoad]
    internal static class DOTweenPathPreviewExtension
    {
        private sealed class PathPreviewInfo
        {
            public DOTweenPath path;
            public Tween tween;
            public Vector3 originalLocalPosition;
            public Quaternion originalLocalRotation;
            public float progress;
        }

        private static readonly Dictionary<int, PathPreviewInfo> PathToPreview = new Dictionary<int, PathPreviewInfo>();
        private static readonly List<int> TmpKeys = new List<int>();

        private static bool _reflectionInitialized;
        private static FieldInfo _wpsField;
        private static FieldInfo _durationField;
        private static FieldInfo _pathTypeField;
        private static FieldInfo _pathModeField;
        private static FieldInfo _isLocalField;
        private static FieldInfo _isClosedPathField;
        private static FieldInfo _easeTypeField;
        private static FieldInfo _loopsField;
        private static FieldInfo _loopTypeField;
        private static FieldInfo _idField;
        private static FieldInfo _lookAtTypeField;
        private static FieldInfo _lookAtTransformField;
        private static FieldInfo _lookAtPositionField;
        private static FieldInfo _lookAheadField;

        private static bool _stylesInitialized;
        private static GUIStyle _previewBox;
        private static GUIStyle _previewLabel;
        private static GUIStyle _previewButton;
        private static GUIStyle _statusLabel;

        static DOTweenPathPreviewExtension()
        {
            InitializeReflection();
            Editor.finishedDefaultHeaderGUI += OnFinishedDefaultHeaderGUI;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopAllPreviews;
        }

        private static void OnFinishedDefaultHeaderGUI(Editor editor)
        {
            if (Application.isPlaying) return;
            if (editor == null) return;

            DOTweenPath src = editor.target as DOTweenPath;
            if (src == null) return;

            InitStyles();
            DrawPreviewPanel(src, editor.targets.Length > 1);
        }

        private static void DrawPreviewPanel(DOTweenPath src, bool hasMultiSelection)
        {
            int id = src.GetInstanceID();
            bool isPreviewingAny = PathToPreview.Count > 0;
            bool isPreviewingThis = PathToPreview.ContainsKey(id);

            Color prevBackground = GUI.backgroundColor;
            GUI.backgroundColor = isPreviewingAny
                ? new Color(0.30f, 0.70f, 0.50f)
                : new Color(0.20f, 0.20f, 0.20f);

            GUILayout.Space(6);
            GUILayout.BeginVertical(_previewBox);
            GUI.backgroundColor = prevBackground;

            GUILayout.Label("Path Preview", _previewLabel);
            GUILayout.Space(2);

            if (hasMultiSelection)
            {
                EditorGUILayout.HelpBox("Preview supports one DOTweenPath at a time. Use single selection for controls.", MessageType.Info);
            }

            GUILayout.BeginHorizontal();
            EditorGUI.BeginDisabledGroup(isPreviewingThis || hasMultiSelection);
            if (GUILayout.Button("Play", _previewButton))
            {
                StartPreview(src);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!isPreviewingThis || hasMultiSelection);
            if (GUILayout.Button("Pause", _previewButton))
            {
                PausePreview(src);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!isPreviewingThis || hasMultiSelection);
            if (GUILayout.Button("Stop", _previewButton))
            {
                StopPreview(src);
            }
            EditorGUI.EndDisabledGroup();
            GUILayout.EndHorizontal();

            if (isPreviewingThis && PathToPreview.TryGetValue(id, out PathPreviewInfo info))
            {
                EditorGUI.BeginChangeCheck();
                float newProgress = EditorGUILayout.Slider("Progress", info.progress, 0f, 1f);
                if (EditorGUI.EndChangeCheck() && info.tween != null && info.tween.IsActive())
                {
                    info.tween.Goto(newProgress * info.tween.Duration());
                    info.progress = newProgress;
                }

                string status = (info.tween != null && info.tween.IsPlaying()) ? "Playing" : "Paused";
                GUILayout.Label("Status: " + status, _statusLabel);
            }

            if (isPreviewingAny)
            {
                GUILayout.Space(2);
                if (GUILayout.Button("Stop All Previews", _previewButton))
                {
                    StopAllPreviews();
                }
            }

            GUILayout.EndVertical();
        }

        private static void StartPreview(DOTweenPath src)
        {
            if (src == null) return;

            StopPreview(src);

            Tween tween = CreatePathTween(src);
            if (tween == null)
            {
                Debug.LogWarning("DOTweenPath Preview: Failed to create preview tween. Make sure path has at least 2 waypoints.");
                return;
            }

            if (PathToPreview.Count == 0)
            {
                DOTweenEditorPreview.Start(OnPreviewUpdated);
            }

            var info = new PathPreviewInfo
            {
                path = src,
                tween = tween,
                originalLocalPosition = src.transform.localPosition,
                originalLocalRotation = src.transform.localRotation,
                progress = 0f
            };
            PathToPreview[src.GetInstanceID()] = info;

            DOTweenEditorPreview.PrepareTweenForPreview(tween, true, true, true);
            InternalEditorUtility.RepaintAllViews();
        }

        private static Tween CreatePathTween(DOTweenPath src)
        {
            try
            {
                List<Vector3> wps = _wpsField != null ? _wpsField.GetValue(src) as List<Vector3> : null;
                if (wps == null || wps.Count < 2) return null;

                float duration = GetReflectedValue(_durationField, src, 1f);
                PathType pathType = GetReflectedValue(_pathTypeField, src, PathType.CatmullRom);
                PathMode pathMode = GetReflectedValue(_pathModeField, src, PathMode.Full3D);
                bool isLocal = GetReflectedValue(_isLocalField, src, false);
                bool isClosedPath = GetReflectedValue(_isClosedPathField, src, false);
                Ease easeType = GetReflectedValue(_easeTypeField, src, Ease.OutQuad);
                int loops = GetReflectedValue(_loopsField, src, 1);
                LoopType loopType = GetReflectedValue(_loopTypeField, src, LoopType.Restart);
                string tweenId = GetReflectedValue(_idField, src, string.Empty);

                Vector3[] waypoints = wps.ToArray();
                TweenerCore<Vector3, Path, PathOptions> tween = isLocal
                    ? src.transform.DOLocalPath(waypoints, duration, pathType, pathMode)
                    : src.transform.DOPath(waypoints, duration, pathType, pathMode);
                if (tween == null) return null;

                tween.SetOptions(isClosedPath)
                    .SetEase(easeType)
                    .SetLoops(loops, loopType);

                ApplyLookAtSettings(src, tween);

                if (!string.IsNullOrEmpty(tweenId))
                {
                    tween.SetId(tweenId);
                }

                return tween;
            }
            catch (Exception e)
            {
                Debug.LogError("DOTweenPath Preview: Error creating tween\n" + e);
                return null;
            }
        }

        private static void ApplyLookAtSettings(DOTweenPath src, TweenerCore<Vector3, Path, PathOptions> tween)
        {
            if (_lookAtTypeField == null) return;

            try
            {
                object lookAtType = _lookAtTypeField.GetValue(src);
                if (lookAtType == null) return;

                string name = lookAtType.ToString();
                if (name.IndexOf("Ahead", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    float lookAhead = GetReflectedValue(_lookAheadField, src, 0.01f);
                    tween.SetLookAt(lookAhead);
                    return;
                }

                if (name.IndexOf("Transform", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Transform lookAtTransform = GetReflectedValue(_lookAtTransformField, src, (Transform)null);
                    if (lookAtTransform != null) tween.SetLookAt(lookAtTransform);
                    return;
                }

                if (name.IndexOf("Position", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Vector3 lookAtPosition = GetReflectedValue(_lookAtPositionField, src, Vector3.zero);
                    tween.SetLookAt(lookAtPosition);
                }
            }
            catch
            {
            }
        }

        private static T GetReflectedValue<T>(FieldInfo field, object instance, T defaultValue)
        {
            if (field == null || instance == null) return defaultValue;

            try
            {
                object value = field.GetValue(instance);
                if (value is T typed) return typed;
            }
            catch
            {
            }

            return defaultValue;
        }

        private static void PausePreview(DOTweenPath src)
        {
            if (src == null) return;
            if (!PathToPreview.TryGetValue(src.GetInstanceID(), out PathPreviewInfo info)) return;
            if (info.tween == null || !info.tween.IsActive()) return;

            info.tween.TogglePause();
            InternalEditorUtility.RepaintAllViews();
        }

        private static void StopPreview(DOTweenPath src)
        {
            if (src == null) return;
            int id = src.GetInstanceID();
            if (!PathToPreview.TryGetValue(id, out PathPreviewInfo info)) return;

            StopPreviewInternal(info);
            PathToPreview.Remove(id);

            if (PathToPreview.Count == 0)
            {
                CleanupPreviewSystem();
            }

            InternalEditorUtility.RepaintAllViews();
        }

        private static void StopPreviewInternal(PathPreviewInfo info)
        {
            if (info == null) return;

            if (info.tween != null && info.tween.IsActive())
            {
                info.tween.Kill();
            }

            if (info.path != null)
            {
                info.path.transform.localPosition = info.originalLocalPosition;
                info.path.transform.localRotation = info.originalLocalRotation;
                EditorUtility.SetDirty(info.path);
            }
        }

        private static void StopAllPreviews()
        {
            if (PathToPreview.Count == 0)
            {
                CleanupPreviewSystem();
                return;
            }

            TmpKeys.Clear();
            foreach (int id in PathToPreview.Keys)
            {
                TmpKeys.Add(id);
            }

            foreach (int id in TmpKeys)
            {
                if (PathToPreview.TryGetValue(id, out PathPreviewInfo info))
                {
                    StopPreviewInternal(info);
                }
            }

            TmpKeys.Clear();
            PathToPreview.Clear();
            CleanupPreviewSystem();
            InternalEditorUtility.RepaintAllViews();
        }

        private static void OnPreviewUpdated()
        {
            if (PathToPreview.Count == 0) return;

            foreach (KeyValuePair<int, PathPreviewInfo> kvp in PathToPreview)
            {
                PathPreviewInfo info = kvp.Value;
                if (info.tween != null && info.tween.IsActive())
                {
                    info.progress = info.tween.ElapsedPercentage();
                }
            }

            InternalEditorUtility.RepaintAllViews();
        }

        private static void CleanupPreviewSystem()
        {
            DOTweenEditorPreview.Stop(true, true);
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            StopAllPreviews();
        }

        private static void InitializeReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            Type pathType = typeof(DOTweenPath);
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _wpsField = pathType.GetField("wps", flags);
            _durationField = pathType.GetField("duration", flags);
            _pathTypeField = pathType.GetField("pathType", flags);
            _pathModeField = pathType.GetField("pathMode", flags);
            _isLocalField = pathType.GetField("isLocal", flags);
            _isClosedPathField = pathType.GetField("isClosedPath", flags);
            _easeTypeField = pathType.GetField("easeType", flags);
            _loopsField = pathType.GetField("loops", flags);
            _loopTypeField = pathType.GetField("loopType", flags);
            _idField = pathType.GetField("id", flags);
            _lookAtTypeField = pathType.GetField("lookAtType", flags);
            _lookAtTransformField = pathType.GetField("lookAtTransform", flags);
            _lookAtPositionField = pathType.GetField("lookAtPosition", flags);
            _lookAheadField = pathType.GetField("lookAhead", flags);

            Type baseType = pathType.BaseType;
            if (baseType != null)
            {
                if (_durationField == null) _durationField = baseType.GetField("duration", flags);
                if (_easeTypeField == null) _easeTypeField = baseType.GetField("easeType", flags);
                if (_loopsField == null) _loopsField = baseType.GetField("loops", flags);
                if (_loopTypeField == null) _loopTypeField = baseType.GetField("loopType", flags);
                if (_idField == null) _idField = baseType.GetField("id", flags);
            }
        }

        private static void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _previewBox = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                margin = new RectOffset(0, 0, 3, 4)
            };
            _previewLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
            _previewButton = new GUIStyle(EditorStyles.miniButton)
            {
                fixedHeight = 21
            };
            _statusLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Italic
            };
        }
    }
}

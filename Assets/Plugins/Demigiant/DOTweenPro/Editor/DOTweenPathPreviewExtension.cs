// Author: Custom Extension for DOTweenPath Editor Preview
// Created: 2026/02/09
// Description: Adds editor preview functionality to DOTweenPath component,
//              similar to DOTweenAnimation's preview feature.
//
// USAGE:
// 1. Add DOTweenPath component to any GameObject
// 2. Configure your path waypoints and settings in the Inspector
// 3. You will see a "🎬 Path Preview - Extension" panel at the bottom of the Inspector
// 4. Click "► Play" to preview the path animation in the editor (without entering Play Mode)
// 5. Use the Progress slider to scrub through the animation
// 6. Click "■ Stop" to stop and reset to original position

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
    /// <summary>
    /// Extension class that adds editor preview functionality to DOTweenPath
    /// </summary>
    [CustomEditor(typeof(DOTweenPath))]
    [CanEditMultipleObjects]
    public class DOTweenPathPreviewExtension : Editor
    {
        // Preview state management
        private static readonly Dictionary<DOTweenPath, PathPreviewInfo> _PathToPreview = new Dictionary<DOTweenPath, PathPreviewInfo>();
        private static readonly List<DOTweenPath> _TmpKeys = new List<DOTweenPath>();

        private DOTweenPath _src;
        private Editor _defaultEditor;
        private Type _defaultEditorType;

        // Cached reflection info
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
        private static FieldInfo _lookAheadField;

        // Styles
        private static bool _stylesInitialized;
        private static GUIStyle _previewBox;
        private static GUIStyle _previewLabel;
        private static GUIStyle _btPreview;
        private static GUIStyle _statusLabel;

        #region Unity Methods

        void OnEnable()
        {
            _src = target as DOTweenPath;

            // Initialize reflection
            if (!_reflectionInitialized)
            {
                InitializeReflection();
            }

            // Try to get the default DOTweenPathInspector
            TryCreateDefaultEditor();
        }

        void OnDisable()
        {
            StopAllPreviews();

            if (_defaultEditor != null)
            {
                DestroyImmediate(_defaultEditor);
                _defaultEditor = null;
            }
        }

        public override void OnInspectorGUI()
        {
            // Draw the default DOTweenPath inspector first
            if (_defaultEditor != null)
            {
                _defaultEditor.OnInspectorGUI();
            }
            else
            {
                // Fallback: draw default inspector
                DrawDefaultInspector();
            }

            // Add our preview controls
            if (!Application.isPlaying)
            {
                GUILayout.Space(10);
                DrawPreviewPanel();
            }
        }

        #endregion

        #region Preview Panel GUI

        private void DrawPreviewPanel()
        {
            InitStyles();

            bool isPreviewing = _PathToPreview.Count > 0;
            bool isPreviewingThis = isPreviewing && _PathToPreview.ContainsKey(_src);

            // Preview panel background
            GUI.backgroundColor = isPreviewing
                ? new Color(0.3f, 0.7f, 0.5f)
                : new Color(0.2f, 0.2f, 0.2f);

            GUILayout.BeginVertical(_previewBox);
            GUI.backgroundColor = Color.white;

            // Title
            GUILayout.BeginHorizontal();
            GUILayout.Label("🎬 Path Preview - Extension", _previewLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // Preview controls - Play buttons
            GUILayout.BeginHorizontal();

            EditorGUI.BeginDisabledGroup(isPreviewingThis);
            if (GUILayout.Button("► Play", _btPreview))
            {
                StartPreview(_src);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!isPreviewingThis);
            if (GUILayout.Button("❚❚ Pause", _btPreview))
            {
                PausePreview(_src);
            }
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!isPreviewingThis);
            if (GUILayout.Button("■ Stop", _btPreview))
            {
                StopPreview(_src);
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();

            // Progress slider
            if (isPreviewingThis && _PathToPreview.TryGetValue(_src, out PathPreviewInfo info))
            {
                GUILayout.Space(4);

                EditorGUI.BeginChangeCheck();
                float newProgress = EditorGUILayout.Slider("Progress", info.progress, 0f, 1f);
                if (EditorGUI.EndChangeCheck() && info.tween != null && info.tween.IsActive())
                {
                    info.tween.Goto(newProgress * info.tween.Duration());
                    info.progress = newProgress;
                }

                // Status
                string status = info.tween != null && info.tween.IsPlaying() ? "▶ Playing..." : "❚❚ Paused";
                GUILayout.Label($"Status: {status}", _statusLabel);
            }

            // Stop all button
            if (isPreviewing)
            {
                GUILayout.Space(4);
                if (GUILayout.Button("■ Stop All Previews", _btPreview))
                {
                    StopAllPreviews();
                }
            }

            GUILayout.EndVertical();
        }

        #endregion

        #region Preview Methods

        private void StartPreview(DOTweenPath src)
        {
            if (src == null) return;

            // Stop existing preview for this path
            if (_PathToPreview.ContainsKey(src))
            {
                StopPreview(src);
            }

            // Start editor preview system
            DOTweenEditorPreview.Start(OnPreviewUpdated);

            // Subscribe to play mode changes
#if UNITY_2017_2_OR_NEWER
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif

            // Create the path tween
            Tween tween = CreatePathTween(src);

            if (tween == null)
            {
                Debug.LogWarning("DOTweenPath Preview: Failed to create tween. Make sure the path has at least 2 waypoints.");
                return;
            }

            // Store original position/rotation for reset
            var pathInfo = new PathPreviewInfo
            {
                path = src,
                tween = tween,
                originalPosition = src.transform.position,
                originalRotation = src.transform.rotation,
                originalLocalPosition = src.transform.localPosition,
                originalLocalRotation = src.transform.localRotation,
                progress = 0f
            };

            _PathToPreview[src] = pathInfo;

            // Prepare tween for preview
            DOTweenEditorPreview.PrepareTweenForPreview(tween, true, true, true);
        }

        private Tween CreatePathTween(DOTweenPath src)
        {
            try
            {
                // Get waypoints via reflection
                List<Vector3> wps = null;
                if (_wpsField != null)
                {
                    wps = _wpsField.GetValue(src) as List<Vector3>;
                }

                if (wps == null || wps.Count < 2)
                {
                    Debug.LogWarning("DOTweenPath Preview: Path needs at least 2 waypoints. Please add waypoints to the path first.");
                    return null;
                }

                // Get settings via reflection with fallbacks
                float duration = GetReflectedValue<float>(_durationField, src, 1f);
                PathType pathTypeValue = GetReflectedValue<PathType>(_pathTypeField, src, PathType.CatmullRom);
                PathMode pathModeValue = GetReflectedValue<PathMode>(_pathModeField, src, PathMode.Full3D);
                bool isLocal = GetReflectedValue<bool>(_isLocalField, src, false);
                bool isClosedPath = GetReflectedValue<bool>(_isClosedPathField, src, false);
                Ease easeType = GetReflectedValue<Ease>(_easeTypeField, src, Ease.OutQuad);
                int loops = GetReflectedValue<int>(_loopsField, src, 1);
                LoopType loopType = GetReflectedValue<LoopType>(_loopTypeField, src, LoopType.Restart);
                string id = GetReflectedValue<string>(_idField, src, "");

                // Convert waypoints to array
                Vector3[] waypoints = wps.ToArray();

                // Create the path tween
                TweenerCore<Vector3, Path, PathOptions> tween;

                if (isLocal)
                {
                    tween = src.transform.DOLocalPath(waypoints, duration, pathTypeValue, pathModeValue);
                }
                else
                {
                    tween = src.transform.DOPath(waypoints, duration, pathTypeValue, pathModeValue);
                }

                if (tween == null) return null;

                // Apply path settings
                tween.SetOptions(isClosedPath)
                     .SetEase(easeType)
                     .SetLoops(loops, loopType);

                // Handle LookAt
                if (_lookAtTypeField != null)
                {
                    try
                    {
                        var lookAtValue = _lookAtTypeField.GetValue(src);
                        if (lookAtValue != null && lookAtValue.ToString().Contains("Ahead"))
                        {
                            float lookAhead = GetReflectedValue<float>(_lookAheadField, src, 0.01f);
                            tween.SetLookAt(lookAhead);
                        }
                    }
                    catch { /* Ignore LookAt errors */ }
                }

                if (!string.IsNullOrEmpty(id))
                {
                    tween.SetId(id);
                }

                return tween;
            }
            catch (Exception e)
            {
                Debug.LogError($"DOTweenPath Preview: Error creating tween: {e.Message}\n{e.StackTrace}");
                return null;
            }
        }

        private T GetReflectedValue<T>(FieldInfo field, object obj, T defaultValue)
        {
            if (field == null) return defaultValue;

            try
            {
                object value = field.GetValue(obj);
                if (value is T typedValue)
                {
                    return typedValue;
                }
            }
            catch { }

            return defaultValue;
        }

        private void PausePreview(DOTweenPath src)
        {
            if (!_PathToPreview.TryGetValue(src, out PathPreviewInfo info)) return;

            if (info.tween != null && info.tween.IsActive())
            {
                info.tween.TogglePause();
            }
        }

        private void StopPreview(DOTweenPath src)
        {
            if (!_PathToPreview.TryGetValue(src, out PathPreviewInfo info)) return;

            // Kill the tween
            if (info.tween != null && info.tween.IsActive())
            {
                info.tween.Kill();
            }

            // Restore original transform
            if (info.path != null)
            {
                info.path.transform.localPosition = info.originalLocalPosition;
                info.path.transform.localRotation = info.originalLocalRotation;
                EditorUtility.SetDirty(info.path);
            }

            _PathToPreview.Remove(src);

            if (_PathToPreview.Count == 0)
            {
                CleanupPreviewSystem();
            }

            InternalEditorUtility.RepaintAllViews();
        }

        private static void StopAllPreviews()
        {
            _TmpKeys.Clear();
            foreach (var kvp in _PathToPreview)
            {
                _TmpKeys.Add(kvp.Key);
            }

            foreach (var key in _TmpKeys)
            {
                if (_PathToPreview.TryGetValue(key, out PathPreviewInfo info))
                {
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
            }

            _TmpKeys.Clear();
            _PathToPreview.Clear();

            CleanupPreviewSystem();
            InternalEditorUtility.RepaintAllViews();
        }

        private static void CleanupPreviewSystem()
        {
            DOTweenEditorPreview.Stop(true, true);

#if UNITY_2017_2_OR_NEWER
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
#endif
        }

#if UNITY_2017_2_OR_NEWER
        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            StopAllPreviews();
        }
#endif

        private static void OnPreviewUpdated()
        {
            // Update progress for all previewing paths
            foreach (var kvp in _PathToPreview)
            {
                var info = kvp.Value;
                if (info.tween != null && info.tween.IsActive())
                {
                    info.progress = info.tween.ElapsedPercentage();
                }
            }
        }

        #endregion

        #region Initialization

        private static void InitializeReflection()
        {
            _reflectionInitialized = true;

            try
            {
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
                _lookAheadField = pathType.GetField("lookAhead", flags);

                // Also try base class for some fields
                Type baseType = pathType.BaseType;
                if (baseType != null)
                {
                    if (_durationField == null) _durationField = baseType.GetField("duration", flags);
                    if (_easeTypeField == null) _easeTypeField = baseType.GetField("easeType", flags);
                    if (_loopsField == null) _loopsField = baseType.GetField("loops", flags);
                    if (_loopTypeField == null) _loopTypeField = baseType.GetField("loopType", flags);
                    if (_idField == null) _idField = baseType.GetField("id", flags);
                }

                // Debug log available fields
                if (_wpsField == null)
                {
                    Debug.LogWarning("DOTweenPath Preview: Could not find 'wps' field. Preview may not work correctly.");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"DOTweenPath Preview: Reflection initialization failed: {e.Message}");
            }
        }

        private void TryCreateDefaultEditor()
        {
            try
            {
                // Find DOTweenPathInspector type in all assemblies
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var assembly in assemblies)
                {
                    try
                    {
                        _defaultEditorType = assembly.GetType("DG.DOTweenEditor.DOTweenPathInspector");
                        if (_defaultEditorType != null) break;
                    }
                    catch { /* Continue searching */ }
                }

                if (_defaultEditorType != null)
                {
                    _defaultEditor = CreateEditor(targets, _defaultEditorType);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"DOTweenPath Preview: Could not create default editor: {e.Message}");
            }
        }

        private static void InitStyles()
        {
            if (_stylesInitialized) return;
            _stylesInitialized = true;

            _previewBox = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(8, 8, 8, 8),
                margin = new RectOffset(0, 0, 4, 4)
            };

            _previewLabel = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 12
            };

            _btPreview = new GUIStyle(EditorStyles.miniButton)
            {
                fixedHeight = 22,
                richText = true
            };

            _statusLabel = new GUIStyle(EditorStyles.miniLabel)
            {
                fontStyle = FontStyle.Italic
            };
        }

        #endregion

        #region Helper Classes

        private class PathPreviewInfo
        {
            public DOTweenPath path;
            public Tween tween;
            public Vector3 originalPosition;
            public Quaternion originalRotation;
            public Vector3 originalLocalPosition;
            public Quaternion originalLocalRotation;
            public float progress;
        }

        #endregion
    }
}

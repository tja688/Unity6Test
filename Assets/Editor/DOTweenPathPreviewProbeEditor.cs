using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dott;
using DG.Tweening;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

[CustomEditor(typeof(DOTweenPathPreviewProbe))]
public class DOTweenPathPreviewProbeEditor : Editor
{
    private sealed class PreviewState
    {
        public DOTweenPathPreviewProbe probe;
        public DOTweenPath path;
        public Tween tween;
        public Vector3 originalLocalPosition;
        public Quaternion originalLocalRotation;
        public float progress;
    }

    private static readonly Dictionary<int, PreviewState> ActivePreviews = new Dictionary<int, PreviewState>();
    private static readonly List<int> TmpKeys = new List<int>();

    private static bool _previewApiInitialized;
    private static bool _previewApiMissingLogged;
    private static MethodInfo _previewStartWithCallback;
    private static MethodInfo _previewStartNoArgs;
    private static MethodInfo _previewStopWithBools;
    private static MethodInfo _previewStopNoArgs;
    private static MethodInfo _previewPrepareWithBools;
    private static MethodInfo _previewPrepareNoArgs;

    private DOTweenPathPreviewProbe _probe;

    static DOTweenPathPreviewProbeEditor()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        AssemblyReloadEvents.beforeAssemblyReload += StopAllPreviews;
    }

    private void OnEnable()
    {
        _probe = (DOTweenPathPreviewProbe)target;
        InitializePreviewApiReflection();
    }

    private void OnDisable()
    {
        if (_probe != null)
        {
            StopPreview(_probe);
        }
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();
        DrawDefaultInspector();
        serializedObject.ApplyModifiedProperties();

        GUILayout.Space(8);
        EditorGUILayout.LabelField("Editor Preview (Probe)", EditorStyles.boldLabel);

        DOTweenPath resolvedPath = _probe != null ? _probe.ResolvePath() : null;
        bool apiAvailable = IsPreviewApiAvailable();

        EditorGUILayout.LabelField("Resolved Path", resolvedPath != null ? resolvedPath.name : "None");
        EditorGUILayout.LabelField("Preview API", apiAvailable ? "Available" : "Missing");

        if (resolvedPath == null)
        {
            EditorGUILayout.HelpBox("No DOTweenPath found. Add DOTweenPath on the same object or assign targetPath manually.", MessageType.Warning);
            return;
        }

        if (!apiAvailable)
        {
            EditorGUILayout.HelpBox("DOTweenEditorPreview API not found. Check DOTween Editor DLL import settings.", MessageType.Error);
            return;
        }

        int probeId = _probe.GetInstanceID();
        bool isPreviewing = ActivePreviews.TryGetValue(probeId, out PreviewState state);

        GUILayout.BeginHorizontal();
        EditorGUI.BeginDisabledGroup(isPreviewing);
        if (GUILayout.Button("Play"))
        {
            StartPreview(_probe);
        }
        EditorGUI.EndDisabledGroup();

        EditorGUI.BeginDisabledGroup(!isPreviewing);
        if (GUILayout.Button("Pause"))
        {
            PausePreview(_probe);
        }
        EditorGUI.EndDisabledGroup();

        EditorGUI.BeginDisabledGroup(!isPreviewing);
        if (GUILayout.Button("Stop"))
        {
            StopPreview(_probe);
        }
        EditorGUI.EndDisabledGroup();
        GUILayout.EndHorizontal();

        if (isPreviewing && state != null)
        {
            EditorGUI.BeginChangeCheck();
            float newProgress = EditorGUILayout.Slider("Progress", state.progress, 0f, 1f);
            if (EditorGUI.EndChangeCheck() && state.tween != null && state.tween.IsActive())
            {
                state.tween.Goto(newProgress * state.tween.Duration(false));
                state.progress = newProgress;
                InternalEditorUtility.RepaintAllViews();
            }

            string status = state.tween != null && state.tween.IsPlaying() ? "Playing" : "Paused";
            EditorGUILayout.LabelField("Status", status);
        }

        if (ActivePreviews.Count > 0)
        {
            if (GUILayout.Button("Stop All Probes"))
            {
                StopAllPreviews();
            }
        }
    }

    private static void StartPreview(DOTweenPathPreviewProbe probe)
    {
        if (probe == null) return;

        DOTweenPath path = probe.ResolvePath();
        if (path == null)
        {
            Debug.LogWarning("DOTweenPathPreviewProbe: no DOTweenPath resolved.");
            return;
        }

        StopPreview(probe);

        Tween tween = CreatePathTween(path);
        if (tween == null)
        {
            Debug.LogWarning("DOTweenPathPreviewProbe: failed to create tween from path. Ensure at least 2 waypoints.");
            return;
        }

        if (ActivePreviews.Count == 0)
        {
            if (!StartPreviewLoop())
            {
                tween.Kill();
                return;
            }
        }

        var state = new PreviewState
        {
            probe = probe,
            path = path,
            tween = tween,
            originalLocalPosition = path.transform.localPosition,
            originalLocalRotation = path.transform.localRotation,
            progress = 0f
        };
        ActivePreviews[probe.GetInstanceID()] = state;

        PrepareTweenForPreview(tween);
        InternalEditorUtility.RepaintAllViews();
    }

    private static void PausePreview(DOTweenPathPreviewProbe probe)
    {
        if (probe == null) return;
        if (!ActivePreviews.TryGetValue(probe.GetInstanceID(), out PreviewState state)) return;
        if (state.tween == null || !state.tween.IsActive()) return;

        state.tween.TogglePause();
        InternalEditorUtility.RepaintAllViews();
    }

    private static void StopPreview(DOTweenPathPreviewProbe probe)
    {
        if (probe == null) return;

        int id = probe.GetInstanceID();
        if (!ActivePreviews.TryGetValue(id, out PreviewState state)) return;

        StopPreviewState(state);
        ActivePreviews.Remove(id);

        if (ActivePreviews.Count == 0)
        {
            StopPreviewLoop();
        }

        InternalEditorUtility.RepaintAllViews();
    }

    private static void StopAllPreviews()
    {
        if (ActivePreviews.Count == 0)
        {
            StopPreviewLoop();
            return;
        }

        TmpKeys.Clear();
        TmpKeys.AddRange(ActivePreviews.Keys);
        foreach (int key in TmpKeys)
        {
            if (ActivePreviews.TryGetValue(key, out PreviewState state))
            {
                StopPreviewState(state);
            }
        }

        TmpKeys.Clear();
        ActivePreviews.Clear();
        StopPreviewLoop();
        InternalEditorUtility.RepaintAllViews();
    }

    private static void StopPreviewState(PreviewState state)
    {
        if (state == null) return;

        if (state.tween != null && state.tween.IsActive())
        {
            state.tween.Kill();
        }

        if (state.path != null)
        {
            state.path.transform.localPosition = state.originalLocalPosition;
            state.path.transform.localRotation = state.originalLocalRotation;
            EditorUtility.SetDirty(state.path);
        }
    }

    private static void OnPreviewUpdated()
    {
        if (ActivePreviews.Count == 0) return;

        foreach (KeyValuePair<int, PreviewState> kvp in ActivePreviews)
        {
            PreviewState state = kvp.Value;
            if (state?.tween != null && state.tween.IsActive())
            {
                state.progress = state.tween.ElapsedPercentage(false);
            }
        }

        InternalEditorUtility.RepaintAllViews();
    }

    private static void OnPlayModeChanged(PlayModeStateChange _)
    {
        StopAllPreviews();
    }

    private static Tween CreatePathTween(DOTweenPath path)
    {
        if (DOTweenPathTweenFactory.TryCreateTween(path, out Tween tween))
        {
            return tween;
        }

        return null;
    }

    private static void InitializePreviewApiReflection()
    {
        if (_previewApiInitialized) return;
        _previewApiInitialized = true;

        Type previewType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a =>
            {
                try { return a.GetType("DG.DOTweenEditor.DOTweenEditorPreview"); }
                catch { return null; }
            })
            .FirstOrDefault(t => t != null);

        if (previewType == null) return;

        BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
        MethodInfo[] methods = previewType.GetMethods(flags);
        foreach (MethodInfo method in methods)
        {
            if (method.Name == "Start")
            {
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 1 && p[0].ParameterType == typeof(Action)) _previewStartWithCallback = method;
                if (p.Length == 0) _previewStartNoArgs = method;
            }
            else if (method.Name == "Stop")
            {
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 2 && p[0].ParameterType == typeof(bool) && p[1].ParameterType == typeof(bool)) _previewStopWithBools = method;
                if (p.Length == 0) _previewStopNoArgs = method;
            }
            else if (method.Name == "PrepareTweenForPreview")
            {
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 4) _previewPrepareWithBools = method;
                if (p.Length == 1) _previewPrepareNoArgs = method;
            }
        }
    }

    private static bool IsPreviewApiAvailable()
    {
        InitializePreviewApiReflection();
        return _previewStartWithCallback != null || _previewStartNoArgs != null;
    }

    private static bool StartPreviewLoop()
    {
        InitializePreviewApiReflection();

        try
        {
            if (_previewStartWithCallback != null)
            {
                _previewStartWithCallback.Invoke(null, new object[] { (Action)OnPreviewUpdated });
                return true;
            }

            if (_previewStartNoArgs != null)
            {
                _previewStartNoArgs.Invoke(null, null);
                return true;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("DOTweenPathPreviewProbe: failed to start preview loop\n" + e);
            return false;
        }

        if (!_previewApiMissingLogged)
        {
            Debug.LogError("DOTweenPathPreviewProbe: DOTweenEditorPreview.Start not found.");
            _previewApiMissingLogged = true;
        }
        return false;
    }

    private static void PrepareTweenForPreview(Tween tween)
    {
        if (tween == null) return;
        InitializePreviewApiReflection();

        try
        {
            if (_previewPrepareWithBools != null)
            {
                _previewPrepareWithBools.Invoke(null, new object[] { tween, true, true, true });
                return;
            }

            if (_previewPrepareNoArgs != null)
            {
                _previewPrepareNoArgs.Invoke(null, new object[] { tween });
            }
        }
        catch (Exception e)
        {
            Debug.LogError("DOTweenPathPreviewProbe: failed to prepare tween for preview\n" + e);
        }
    }

    private static void StopPreviewLoop()
    {
        InitializePreviewApiReflection();

        try
        {
            if (_previewStopWithBools != null)
            {
                _previewStopWithBools.Invoke(null, new object[] { true, true });
                return;
            }

            if (_previewStopNoArgs != null)
            {
                _previewStopNoArgs.Invoke(null, null);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("DOTweenPathPreviewProbe: failed to stop preview loop\n" + e);
        }
    }
}

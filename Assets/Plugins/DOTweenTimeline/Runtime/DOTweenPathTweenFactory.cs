using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Core.PathCore;
using DG.Tweening.Plugins.Options;
using UnityEngine;

namespace Dott
{
    public static class DOTweenPathTweenFactory
    {
        private const BindingFlags FLAGS = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool initialized;
        private static FieldInfo wpsField;
        private static FieldInfo durationField;
        private static FieldInfo pathTypeField;
        private static FieldInfo pathModeField;
        private static FieldInfo isLocalField;
        private static FieldInfo isClosedPathField;
        private static FieldInfo easeTypeField;
        private static FieldInfo easeCurveField;
        private static FieldInfo loopsField;
        private static FieldInfo loopTypeField;
        private static FieldInfo idField;
        private static FieldInfo isSpeedBasedField;
        private static FieldInfo relativeField;
        private static FieldInfo orientTypeField;
        private static FieldInfo lookAtTransformField;
        private static FieldInfo lookAtPositionField;
        private static FieldInfo lookAheadField;
        private static FieldInfo autoPlayField;

        public static bool IsReflectionReady
        {
            get
            {
                EnsureInitialized();
                return wpsField != null;
            }
        }

        public static bool TryCreateTween(DOTweenPath path, out Tween tween)
        {
            tween = null;
            if (!TryGetWaypoints(path, out Vector3[] waypoints) || waypoints.Length < 2)
            {
                return false;
            }

            try
            {
                float duration = Read(durationField, path, 1f);
                PathType pathType = Read(pathTypeField, path, PathType.CatmullRom);
                PathMode pathMode = Read(pathModeField, path, PathMode.Full3D);
                bool isLocal = Read(isLocalField, path, false);
                bool isClosed = Read(isClosedPathField, path, false);
                Ease easeType = Read(easeTypeField, path, Ease.OutQuad);
                AnimationCurve easeCurve = Read<AnimationCurve>(easeCurveField, path, null);
                int loops = Read(loopsField, path, 1);
                LoopType loopType = Read(loopTypeField, path, LoopType.Restart);
                bool isSpeedBased = Read(isSpeedBasedField, path, false);
                bool isRelative = Read(relativeField, path, false);
                string tweenId = Read(idField, path, string.Empty);

                TweenerCore<Vector3, Path, PathOptions> pathTween = isLocal
                    ? path.transform.DOLocalPath(waypoints, duration, pathType, pathMode)
                    : path.transform.DOPath(waypoints, duration, pathType, pathMode);
                if (pathTween == null)
                {
                    return false;
                }

                pathTween.SetOptions(isClosed)
                    .SetLoops(loops, loopType);

                if (easeType == Ease.INTERNAL_Custom && easeCurve != null)
                {
                    pathTween.SetEase(easeCurve);
                }
                else
                {
                    pathTween.SetEase(easeType);
                }

                if (isSpeedBased)
                {
                    pathTween.SetSpeedBased();
                }

                if (isRelative)
                {
                    pathTween.SetRelative();
                }

                if (!string.IsNullOrEmpty(tweenId))
                {
                    pathTween.SetId(tweenId);
                }

                ApplyLookAt(path, pathTween);
                tween = pathTween;
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError("DOTweenPathTweenFactory: failed to create tween\n" + e);
                return false;
            }
        }

        public static bool TryGetDuration(DOTweenPath path, out float duration)
        {
            duration = 1f;
            if (path == null)
            {
                return false;
            }

            EnsureInitialized();
            if (durationField == null)
            {
                return false;
            }

            duration = Read(durationField, path, 1f);
            return true;
        }

        public static bool TryGetLoops(DOTweenPath path, out int loops)
        {
            loops = 1;
            if (path == null)
            {
                return false;
            }

            EnsureInitialized();
            if (loopsField == null)
            {
                return false;
            }

            loops = Read(loopsField, path, 1);
            return true;
        }

        public static bool IsValid(DOTweenPath path)
        {
            return TryGetWaypointCount(path, out int waypointCount) && waypointCount >= 2;
        }

        public static bool TryGetWaypointCount(DOTweenPath path, out int waypointCount)
        {
            waypointCount = 0;
            if (!TryGetWaypoints(path, out Vector3[] waypoints))
            {
                return false;
            }

            waypointCount = waypoints.Length;
            return true;
        }

        public static bool TryDisableAutoPlay(DOTweenPath path)
        {
            if (path == null)
            {
                return false;
            }

            EnsureInitialized();
            if (autoPlayField == null)
            {
                return false;
            }

            try
            {
                if (autoPlayField.GetValue(path) is bool autoPlay && autoPlay)
                {
                    autoPlayField.SetValue(path, false);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static void EnsureInitialized()
        {
            if (initialized)
            {
                return;
            }

            initialized = true;

            Type pathType = typeof(DOTweenPath);
            wpsField = pathType.GetField("wps", FLAGS);
            durationField = pathType.GetField("duration", FLAGS);
            pathTypeField = pathType.GetField("pathType", FLAGS);
            pathModeField = pathType.GetField("pathMode", FLAGS);
            isLocalField = pathType.GetField("isLocal", FLAGS);
            isClosedPathField = pathType.GetField("isClosedPath", FLAGS);
            easeTypeField = pathType.GetField("easeType", FLAGS);
            easeCurveField = pathType.GetField("easeCurve", FLAGS);
            loopsField = pathType.GetField("loops", FLAGS);
            loopTypeField = pathType.GetField("loopType", FLAGS);
            idField = pathType.GetField("id", FLAGS);
            isSpeedBasedField = pathType.GetField("isSpeedBased", FLAGS);
            relativeField = pathType.GetField("relative", FLAGS);
            orientTypeField = pathType.GetField("orientType", FLAGS) ?? pathType.GetField("lookAtType", FLAGS);
            lookAtTransformField = pathType.GetField("lookAtTransform", FLAGS);
            lookAtPositionField = pathType.GetField("lookAtPosition", FLAGS);
            lookAheadField = pathType.GetField("lookAhead", FLAGS);
            autoPlayField = pathType.GetField("autoPlay", FLAGS);

            Type baseType = pathType.BaseType;
            if (baseType == null)
            {
                return;
            }

            durationField ??= baseType.GetField("duration", FLAGS);
            easeTypeField ??= baseType.GetField("easeType", FLAGS);
            easeCurveField ??= baseType.GetField("easeCurve", FLAGS);
            loopsField ??= baseType.GetField("loops", FLAGS);
            loopTypeField ??= baseType.GetField("loopType", FLAGS);
            idField ??= baseType.GetField("id", FLAGS);
            isSpeedBasedField ??= baseType.GetField("isSpeedBased", FLAGS);
            relativeField ??= baseType.GetField("relative", FLAGS);
            autoPlayField ??= baseType.GetField("autoPlay", FLAGS);
        }

        private static bool TryGetWaypoints(DOTweenPath path, out Vector3[] waypoints)
        {
            waypoints = Array.Empty<Vector3>();
            if (path == null)
            {
                return false;
            }

            EnsureInitialized();
            if (wpsField == null)
            {
                return false;
            }

            try
            {
                object value = wpsField.GetValue(path);
                switch (value)
                {
                    case Vector3[] vectorArray:
                        waypoints = vectorArray;
                        return true;

                    case IList<Vector3> vectorList:
                        waypoints = new Vector3[vectorList.Count];
                        for (int i = 0; i < vectorList.Count; i++)
                        {
                            waypoints[i] = vectorList[i];
                        }

                        return true;

                    case IEnumerable enumerable:
                    {
                        var tmp = new System.Collections.Generic.List<Vector3>();
                        foreach (object item in enumerable)
                        {
                            if (item is Vector3 vector)
                            {
                                tmp.Add(vector);
                            }
                        }

                        waypoints = tmp.ToArray();
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static T Read<T>(FieldInfo field, object source, T fallback)
        {
            if (field == null || source == null)
            {
                return fallback;
            }

            try
            {
                object value = field.GetValue(source);
                if (value is T typed)
                {
                    return typed;
                }
            }
            catch
            {
                return fallback;
            }

            return fallback;
        }

        private static void ApplyLookAt(DOTweenPath path, TweenerCore<Vector3, Path, PathOptions> tween)
        {
            if (path == null || tween == null)
            {
                return;
            }

            EnsureInitialized();
            if (orientTypeField == null)
            {
                return;
            }

            try
            {
                object orientType = orientTypeField.GetValue(path);
                if (orientType == null)
                {
                    return;
                }

                string orientName = orientType.ToString();
                if (orientName.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return;
                }

                Transform lookAtTransform = Read<Transform>(lookAtTransformField, path, null);
                if (lookAtTransform != null)
                {
                    tween.SetLookAt(lookAtTransform);
                    return;
                }

                Vector3 lookAtPosition = Read(lookAtPositionField, path, Vector3.zero);
                if (lookAtPosition != Vector3.zero)
                {
                    tween.SetLookAt(lookAtPosition);
                    return;
                }

                float lookAhead = Read(lookAheadField, path, 0.01f);
                if (lookAhead > 0f)
                {
                    tween.SetLookAt(lookAhead);
                }
            }
            catch
            {
                // Ignore look-at setup errors and keep tween usable.
            }
        }
    }
}

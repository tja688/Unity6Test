using DG.Tweening;
using DG.Tweening.Plugins.Core.PathCore;
using System.Collections.Generic;
using UnityEngine;

namespace GestureTween
{
    /// <summary>
    /// 手绘动效预设资产：
    /// 1) 速度曲线（easeCurve）
    /// 2) 可选路径通道（localPathPoints）
    /// 3) 可选缩放/旋转通道（scaleCurve/rotationCurve）
    /// </summary>
    [CreateAssetMenu(fileName = "GestureCurve", menuName = "GestureTween/Curve Preset")]
    public class GestureCurvePreset : ScriptableObject
    {
        [Tooltip("手绘生成的缓动曲线")]
        public AnimationCurve easeCurve = new AnimationCurve(
            new Keyframe(0f, 0f, 0f, 1f),
            new Keyframe(1f, 1f, 1f, 0f)
        );

        [Tooltip("推荐时长（秒）")]
        public float recommendedDuration = 1f;

        [Tooltip("路径通道：是否有效")]
        public bool hasPositionPath;

        [Tooltip("路径通道：以目标当前 localPosition 为原点的局部偏移点集（第一个点通常为 0）")]
        public List<Vector3> localPathPoints = new() { Vector3.zero, Vector3.right };

        [Tooltip("缩放通道：是否有效")]
        public bool hasScaleChannel;

        [Tooltip("缩放通道（值为倍率，1 表示原始大小）")]
        public AnimationCurve scaleCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        [Tooltip("旋转通道：是否有效")]
        public bool hasRotationChannel;

        [Tooltip("旋转通道（值为 Z 轴角度增量，单位：度）")]
        public AnimationCurve rotationCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);

        [Tooltip("曲线描述")]
        [TextArea(2, 4)]
        public string description;

        /// <summary>
        /// 应用曲线到 DOTweenAnimation 组件
        /// </summary>
        public void ApplyTo(DOTweenAnimation anim)
        {
            if (anim == null) return;

            anim.easeType = Ease.INTERNAL_Custom;
            anim.easeCurve = new AnimationCurve(easeCurve.keys);
            anim.duration = recommendedDuration;
        }

        /// <summary>
        /// 使用此曲线创建一个 virtual tween（用于预览进度）
        /// </summary>
        public Tween CreatePreviewTween(DG.Tweening.TweenCallback<float> onUpdate, float duration = -1f)
        {
            float dur = duration > 0 ? duration : recommendedDuration;
            return DOVirtual.Float(0f, 1f, dur, onUpdate).SetEase(easeCurve);
        }

        /// <summary>
        /// 创建并返回一个可复用的 Sequence。返回前已 Pause，调用方可自行 Play/Restart。
        /// </summary>
        public Sequence CreateTransformSequence(Transform target, bool useLocalSpace = true, bool includeScale = true, bool includeRotation = true)
        {
            if (target == null) return null;

            float duration = Mathf.Max(0.05f, recommendedDuration);
            var sequence = DOTween.Sequence();

            // Position path
            if (hasPositionPath && localPathPoints != null && localPathPoints.Count >= 2)
            {
                var waypoints = BuildWaypoints(target, useLocalSpace);
                if (waypoints.Length > 0)
                {
                    Tween pathTween = useLocalSpace
                        ? target.DOLocalPath(waypoints, duration, PathType.CatmullRom, PathMode.TopDown2D)
                        : target.DOPath(waypoints, duration, PathType.CatmullRom, PathMode.TopDown2D);

                    if (HasCurve(easeCurve))
                    {
                        pathTween.SetEase(easeCurve);
                    }
                    else
                    {
                        pathTween.SetEase(Ease.OutQuad);
                    }
                    sequence.Join(pathTween);
                }
            }

            // Scale channel
            if (includeScale && hasScaleChannel && HasCurve(scaleCurve))
            {
                Vector3 baseScale = target.localScale;
                sequence.Join(
                    DOVirtual.Float(0f, 1f, duration, t =>
                    {
                        float factor = Mathf.Max(0.0001f, scaleCurve.Evaluate(t));
                        target.localScale = baseScale * factor;
                    }).SetEase(Ease.Linear)
                );
            }

            // Rotation channel (2D-friendly: apply on Z)
            if (includeRotation && hasRotationChannel && HasCurve(rotationCurve))
            {
                Vector3 baseEuler = useLocalSpace ? target.localEulerAngles : target.eulerAngles;
                sequence.Join(
                    DOVirtual.Float(0f, 1f, duration, t =>
                    {
                        float z = baseEuler.z + rotationCurve.Evaluate(t);
                        if (useLocalSpace)
                        {
                            target.localEulerAngles = new Vector3(baseEuler.x, baseEuler.y, z);
                        }
                        else
                        {
                            target.eulerAngles = new Vector3(baseEuler.x, baseEuler.y, z);
                        }
                    }).SetEase(Ease.Linear)
                );
            }

            if (sequence.Duration(includeLoops: false) <= 0f)
            {
                sequence.Kill();
                return null;
            }

            sequence.Pause();
            return sequence;
        }

        private Vector3[] BuildWaypoints(Transform target, bool useLocalSpace)
        {
            // DOTween path APIs use the current transform position as start.
            // Therefore waypoints exclude index 0.
            int count = localPathPoints.Count - 1;
            var waypoints = new Vector3[count];
            var parent = target.parent;
            Vector3 startLocal = target.localPosition;

            for (int i = 1; i < localPathPoints.Count; i++)
            {
                Vector3 offset = localPathPoints[i];
                Vector3 localPoint = startLocal + offset;
                waypoints[i - 1] = useLocalSpace
                    ? localPoint
                    : (parent != null ? parent.TransformPoint(localPoint) : localPoint);
            }

            return waypoints;
        }

        private static bool HasCurve(AnimationCurve curve)
        {
            return curve != null && curve.length >= 2;
        }
    }
}

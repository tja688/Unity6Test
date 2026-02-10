using System.Collections.Generic;
using DG.Tweening;
using Dott;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GestureTween
{
    [DisallowMultipleComponent]
    public class GestureScaleTrack : MonoBehaviour, IDOTweenAnimation
    {
        [SerializeField] private string id = "Gesture Scale";
        [Min(0f)] [SerializeField] private float delay;
        [Min(0.01f)] [SerializeField] private float duration = 1f;
        [SerializeField] private int loops = 1;
        [SerializeField] private LoopType loopType = LoopType.Restart;
        [SerializeField] private bool isActive = true;

        [SerializeField] private bool autoFindTargetFromWorkspace = true;
        [SerializeField] private Transform target;

        [SerializeField] private AnimationCurve xCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [SerializeField] private AnimationCurve yCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [SerializeField] private AnimationCurve zCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [SerializeField] private float minScaleFactor = 0.01f;

        private Tween tween;

        public float TrackDuration
        {
            get => duration;
            set => duration = Mathf.Max(0.01f, value);
        }

        public AnimationCurve XCurve
        {
            get => xCurve;
            set => xCurve = EnsureCurve(value, 1f);
        }

        public AnimationCurve YCurve
        {
            get => yCurve;
            set => yCurve = EnsureCurve(value, 1f);
        }

        public AnimationCurve ZCurve
        {
            get => zCurve;
            set => zCurve = EnsureCurve(value, 1f);
        }

        public Transform ResolveTarget()
        {
            if (!autoFindTargetFromWorkspace)
            {
                return target;
            }

            if (target != null)
            {
                return target;
            }

            GestureWorkspace workspace = GetComponent<GestureWorkspace>();
            if (workspace != null && workspace.ResolvedRootTarget != null)
            {
                return workspace.ResolvedRootTarget;
            }

            return transform.parent;
        }

        public void SetTargetIfAuto(Transform newTarget)
        {
            if (!autoFindTargetFromWorkspace && target != null)
            {
                return;
            }

            target = newTarget;
        }

        public void SetCurves(AnimationCurve x, AnimationCurve y, AnimationCurve z, float generatedDuration)
        {
            duration = Mathf.Max(0.01f, generatedDuration);
            xCurve = EnsureCurve(x, 1f);
            yCurve = EnsureCurve(y, 1f);
            zCurve = EnsureCurve(z, 1f);
        }

        public Tween CreateTween(bool regenerateIfExists, bool andPlay = true)
        {
            if (tween != null)
            {
                if (tween.IsActive())
                {
                    if (!regenerateIfExists)
                    {
                        return tween;
                    }

                    tween.Kill();
                }

                tween = null;
            }

            Transform resolvedTarget = ResolveTarget();
            if (resolvedTarget == null)
            {
                return null;
            }

            Vector3 baseScale = resolvedTarget.localScale;
            tween = DOVirtual.Float(0f, 1f, duration, t =>
                {
                    float sx = Mathf.Max(minScaleFactor, xCurve.Evaluate(t));
                    float sy = Mathf.Max(minScaleFactor, yCurve.Evaluate(t));
                    float sz = Mathf.Max(minScaleFactor, zCurve.Evaluate(t));
                    resolvedTarget.localScale = Vector3.Scale(baseScale, new Vector3(sx, sy, sz));
                })
                .SetEase(Ease.Linear)
                .SetDelay(delay, asPrependedIntervalIfSequence: true)
                .SetLoops(loops, loopType)
                .OnRewind(() => resolvedTarget.localScale = baseScale);

            if (andPlay)
            {
                tween.Play();
            }
            else
            {
                tween.Pause();
            }

            return tween;
        }

        float IDOTweenAnimation.Delay
        {
            get => delay;
            set => delay = Mathf.Max(0f, value);
        }

        float IDOTweenAnimation.Duration => duration;
        int IDOTweenAnimation.Loops => loops;
        bool IDOTweenAnimation.IsValid => ResolveTarget() != null;
        bool IDOTweenAnimation.IsActive => isActive && isActiveAndEnabled;
        bool IDOTweenAnimation.IsFrom => false;
        string IDOTweenAnimation.Label => string.IsNullOrWhiteSpace(id) ? "Gesture Scale" : id;
        Component IDOTweenAnimation.Component => this;
        IEnumerable<Object> IDOTweenAnimation.Targets => ResolveTarget() != null ? new Object[] { ResolveTarget() } : System.Linq.Enumerable.Empty<Object>();
        Tween IDOTweenAnimation.CreateEditorPreview() => CreateTween(regenerateIfExists: true, andPlay: false);

        private static AnimationCurve EnsureCurve(AnimationCurve curve, float fallbackValue)
        {
            if (curve != null && curve.length >= 2)
            {
                return curve;
            }

            return AnimationCurve.Linear(0f, fallbackValue, 1f, fallbackValue);
        }

        private void OnDisable()
        {
            if (tween != null && tween.IsActive())
            {
                tween.Kill();
            }

            tween = null;
        }

        private void OnValidate()
        {
            duration = Mathf.Max(0.01f, duration);
            minScaleFactor = Mathf.Max(0.0001f, minScaleFactor);
            if (loops == 0)
            {
                loops = 1;
            }

            xCurve = EnsureCurve(xCurve, 1f);
            yCurve = EnsureCurve(yCurve, 1f);
            zCurve = EnsureCurve(zCurve, 1f);

            if (autoFindTargetFromWorkspace && target == null)
            {
                GestureWorkspace workspace = GetComponent<GestureWorkspace>();
                if (workspace != null && workspace.ResolvedRootTarget != null)
                {
                    target = workspace.ResolvedRootTarget;
                }
                else if (transform.parent != null)
                {
                    target = transform.parent;
                }
            }
        }
    }
}

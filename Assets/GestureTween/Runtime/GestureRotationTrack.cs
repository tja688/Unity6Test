using System.Collections.Generic;
using DG.Tweening;
using Dott;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GestureTween
{
    [DisallowMultipleComponent]
    public class GestureRotationTrack : MonoBehaviour, IDOTweenAnimation
    {
        [SerializeField] private string id = "Gesture Rotation";
        [Min(0f)] [SerializeField] private float delay;
        [Min(0.01f)] [SerializeField] private float duration = 1f;
        [SerializeField] private int loops = 1;
        [SerializeField] private LoopType loopType = LoopType.Restart;
        [SerializeField] private bool isActive = true;

        [SerializeField] private bool autoFindTargetFromWorkspace = true;
        [SerializeField] private Transform target;
        [SerializeField] private bool useLocalSpace = true;

        [SerializeField] private AnimationCurve xCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        [SerializeField] private AnimationCurve yCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);
        [SerializeField] private AnimationCurve zCurve = AnimationCurve.Linear(0f, 0f, 1f, 0f);

        private Tween tween;

        public float TrackDuration
        {
            get => duration;
            set => duration = Mathf.Max(0.01f, value);
        }

        public bool UseLocalSpace
        {
            get => useLocalSpace;
            set => useLocalSpace = value;
        }

        public AnimationCurve XCurve
        {
            get => xCurve;
            set => xCurve = EnsureCurve(value, 0f);
        }

        public AnimationCurve YCurve
        {
            get => yCurve;
            set => yCurve = EnsureCurve(value, 0f);
        }

        public AnimationCurve ZCurve
        {
            get => zCurve;
            set => zCurve = EnsureCurve(value, 0f);
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
            xCurve = EnsureCurve(x, 0f);
            yCurve = EnsureCurve(y, 0f);
            zCurve = EnsureCurve(z, 0f);
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

            Vector3 baseEuler = useLocalSpace ? resolvedTarget.localEulerAngles : resolvedTarget.eulerAngles;
            tween = DOVirtual.Float(0f, 1f, duration, t =>
                {
                    Vector3 delta = new Vector3(xCurve.Evaluate(t), yCurve.Evaluate(t), zCurve.Evaluate(t));
                    Vector3 euler = baseEuler + delta;
                    if (useLocalSpace)
                    {
                        resolvedTarget.localEulerAngles = euler;
                    }
                    else
                    {
                        resolvedTarget.eulerAngles = euler;
                    }
                })
                .SetEase(Ease.Linear)
                .SetDelay(delay, asPrependedIntervalIfSequence: true)
                .SetLoops(loops, loopType)
                .OnRewind(() =>
                {
                    if (useLocalSpace)
                    {
                        resolvedTarget.localEulerAngles = baseEuler;
                    }
                    else
                    {
                        resolvedTarget.eulerAngles = baseEuler;
                    }
                });

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
        string IDOTweenAnimation.Label => string.IsNullOrWhiteSpace(id) ? "Gesture Rotation" : id;
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
            if (loops == 0)
            {
                loops = 1;
            }

            xCurve = EnsureCurve(xCurve, 0f);
            yCurve = EnsureCurve(yCurve, 0f);
            zCurve = EnsureCurve(zCurve, 0f);

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

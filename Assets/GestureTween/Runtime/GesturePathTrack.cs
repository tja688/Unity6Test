using System.Collections.Generic;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Core.PathCore;
using DG.Tweening.Plugins.Options;
using Dott;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GestureTween
{
    [DisallowMultipleComponent]
    public class GesturePathTrack : MonoBehaviour, IDOTweenAnimation
    {
        [SerializeField] private string id = "Gesture Path";
        [Min(0f)] [SerializeField] private float delay;
        [Min(0.01f)] [SerializeField] private float duration = 1f;
        [SerializeField] private int loops = 1;
        [SerializeField] private LoopType loopType = LoopType.Restart;
        [SerializeField] private bool isActive = true;

        [SerializeField] private bool autoFindTargetFromWorkspace = true;
        [SerializeField] private Transform target;
        [SerializeField] private bool useLocalSpace = true;

        [SerializeField] private PathType pathType = PathType.CatmullRom;
        [SerializeField] private PathMode pathMode = PathMode.TopDown2D;
        [SerializeField] private bool isClosedPath;

        [SerializeField] private Ease easeType = Ease.INTERNAL_Custom;
        [SerializeField] private AnimationCurve easeCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        [SerializeField] private List<Vector3> localPathPoints = new() { Vector3.zero, Vector3.right };

        private Tween tween;

        public bool UseLocalSpace
        {
            get => useLocalSpace;
            set => useLocalSpace = value;
        }

        public float TrackDuration
        {
            get => duration;
            set => duration = Mathf.Max(0.01f, value);
        }

        public AnimationCurve EaseCurve
        {
            get => easeCurve;
            set => easeCurve = value ?? AnimationCurve.Linear(0f, 0f, 1f, 1f);
        }

        public List<Vector3> LocalPathPoints => localPathPoints;

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

        public void SetPathData(IReadOnlyList<Vector3> offsets, AnimationCurve generatedEase, float generatedDuration, bool localSpace)
        {
            useLocalSpace = localSpace;
            duration = Mathf.Max(0.01f, generatedDuration);
            easeType = Ease.INTERNAL_Custom;
            easeCurve = generatedEase != null && generatedEase.length >= 2
                ? new AnimationCurve(generatedEase.keys)
                : AnimationCurve.Linear(0f, 0f, 1f, 1f);

            localPathPoints ??= new List<Vector3>();
            localPathPoints.Clear();
            if (offsets != null && offsets.Count > 0)
            {
                for (int i = 0; i < offsets.Count; i++)
                {
                    localPathPoints.Add(offsets[i]);
                }
            }

            if (localPathPoints.Count < 2)
            {
                localPathPoints.Clear();
                localPathPoints.Add(Vector3.zero);
                localPathPoints.Add(Vector3.right);
            }
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

            if (!TryBuildWaypoints(out Transform resolvedTarget, out Vector3[] waypoints))
            {
                return null;
            }

            TweenerCore<Vector3, Path, PathOptions> pathTween = useLocalSpace
                ? resolvedTarget.DOLocalPath(waypoints, duration, pathType, pathMode)
                : resolvedTarget.DOPath(waypoints, duration, pathType, pathMode);

            pathTween.SetDelay(delay, asPrependedIntervalIfSequence: true)
                .SetLoops(loops, loopType)
                .SetOptions(isClosedPath);

            if (easeType == Ease.INTERNAL_Custom && HasCurve(easeCurve))
            {
                pathTween.SetEase(easeCurve);
            }
            else
            {
                pathTween.SetEase(easeType);
            }

            tween = pathTween;
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
        bool IDOTweenAnimation.IsValid => IsValidPath();
        bool IDOTweenAnimation.IsActive => isActive && isActiveAndEnabled;
        bool IDOTweenAnimation.IsFrom => false;
        string IDOTweenAnimation.Label => string.IsNullOrWhiteSpace(id) ? "Gesture Path" : id;
        Component IDOTweenAnimation.Component => this;
        IEnumerable<Object> IDOTweenAnimation.Targets => ResolveTarget() != null ? new Object[] { ResolveTarget() } : System.Linq.Enumerable.Empty<Object>();
        Tween IDOTweenAnimation.CreateEditorPreview() => CreateTween(regenerateIfExists: true, andPlay: false);

        private bool TryBuildWaypoints(out Transform resolvedTarget, out Vector3[] waypoints)
        {
            waypoints = null;
            resolvedTarget = ResolveTarget();
            if (resolvedTarget == null || localPathPoints == null || localPathPoints.Count < 2)
            {
                return false;
            }

            Transform parent = resolvedTarget.parent;
            Vector3 startLocal = resolvedTarget.localPosition;

            waypoints = new Vector3[localPathPoints.Count - 1];
            for (int i = 1; i < localPathPoints.Count; i++)
            {
                Vector3 localPoint = startLocal + localPathPoints[i];
                waypoints[i - 1] = useLocalSpace
                    ? localPoint
                    : (parent != null ? parent.TransformPoint(localPoint) : localPoint);
            }

            return waypoints.Length > 0;
        }

        private bool IsValidPath()
        {
            return ResolveTarget() != null && localPathPoints != null && localPathPoints.Count >= 2;
        }

        private static bool HasCurve(AnimationCurve curve)
        {
            return curve != null && curve.length >= 2;
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

            localPathPoints ??= new List<Vector3>();
            if (localPathPoints.Count < 2)
            {
                localPathPoints.Clear();
                localPathPoints.Add(Vector3.zero);
                localPathPoints.Add(Vector3.right);
            }

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

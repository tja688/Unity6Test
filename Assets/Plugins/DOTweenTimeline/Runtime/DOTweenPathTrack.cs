using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Dott
{
    [DisallowMultipleComponent]
    public class DOTweenPathTrack : MonoBehaviour, IDOTweenAnimation
    {
        [SerializeField] public string id;
        [Min(0f)] [SerializeField] public float delay;
        [SerializeField] public bool autoFindOnSameObject = true;
        [SerializeField] public DOTweenPath targetPath;
        [SerializeField] public bool isActive = true;

        private Tween tween;

        public DOTweenPath ResolvePath()
        {
            if (autoFindOnSameObject)
            {
                DOTweenPath sameObjectPath = GetComponent<DOTweenPath>();
                if (sameObjectPath != null)
                {
                    return sameObjectPath;
                }
            }

            return targetPath;
        }

        public Tween CreateTween(bool regenerateIfExists, bool andPlay = true)
        {
            if (tween != null)
            {
                if (tween.active)
                {
                    if (!regenerateIfExists)
                    {
                        return tween;
                    }

                    tween.Kill();
                }

                tween = null;
            }

            DOTweenPath path = ResolvePath();
            if (!DOTweenPathTweenFactory.TryCreateTween(path, out tween))
            {
                return null;
            }

            tween.SetDelay(delay, asPrependedIntervalIfSequence: true);

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
            set => delay = value;
        }

        float IDOTweenAnimation.Duration => DOTweenPathTweenFactory.TryGetDuration(ResolvePath(), out float duration)
            ? duration
            : 1f;

        int IDOTweenAnimation.Loops => DOTweenPathTweenFactory.TryGetLoops(ResolvePath(), out int loops)
            ? loops
            : 1;

        bool IDOTweenAnimation.IsValid => DOTweenPathTweenFactory.IsValid(ResolvePath());

        bool IDOTweenAnimation.IsActive
        {
            get
            {
                DOTweenPath path = ResolvePath();
                return isActive && path != null && path.isActiveAndEnabled;
            }
        }

        bool IDOTweenAnimation.IsFrom => false;

        string IDOTweenAnimation.Label
        {
            get
            {
                if (!string.IsNullOrEmpty(id))
                {
                    return id;
                }

                DOTweenPath path = ResolvePath();
                return path != null ? $"Path {path.name}" : "Path None";
            }
        }

        Component IDOTweenAnimation.Component => this;

        IEnumerable<Object> IDOTweenAnimation.Targets
        {
            get
            {
                DOTweenPath path = ResolvePath();
                if (path == null)
                {
                    return System.Linq.Enumerable.Empty<Object>();
                }

                return new Object[] { path.transform };
            }
        }

        Tween IDOTweenAnimation.CreateEditorPreview() => CreateTween(regenerateIfExists: true, andPlay: false);

        private void OnValidate()
        {
            if (autoFindOnSameObject && targetPath == null)
            {
                DOTweenPath sameObjectPath = GetComponent<DOTweenPath>();
                if (sameObjectPath != null)
                {
                    targetPath = sameObjectPath;
                }
            }
        }

        private void OnDisable()
        {
            if (tween != null && tween.IsActive())
            {
                tween.Kill();
            }

            tween = null;
        }
    }
}

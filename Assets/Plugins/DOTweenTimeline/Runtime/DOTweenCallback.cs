using DG.Tweening;
using UnityEngine;
using UnityEngine.Events;

namespace Dott
{
    public partial class DOTweenCallback : MonoBehaviour
    {
        [SerializeField] public string id;
        [Min(0)] [SerializeField] public float delay;
        [SerializeField] public UnityEvent onCallback;

        private Tween tween;

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

            tween = DOTween.Sequence().InsertCallback(delay, () => onCallback.Invoke());

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
    }
}
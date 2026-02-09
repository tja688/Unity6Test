using System;
using System.Collections.Generic;
using System.Linq;
using DG.Tweening;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

namespace Dott.Editor
{
    public static class DottEditorPreview
    {
        private struct TweenData
        {
            public readonly Tween Tween;
            public readonly bool IsFrom;

            public TweenData(Tween tween, bool isFrom)
            {
                Tween = tween;
                IsFrom = isFrom;
            }
        }

        private static readonly List<TweenData> Tweens = new();

        public static bool IsPlaying { get; private set; }
        public static double CurrentTime { get; private set; }
        public static event Action Completed;

        static DottEditorPreview()
        {
            if (!Application.isPlaying)
            {
                DOTween.useSafeMode = false;
            }
        }

        public static void Start()
        {
            if (IsPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            IsPlaying = true;
            CurrentTime = EditorApplication.timeSinceStartup;
            EditorApplication.update += Update;
        }

        public static void Stop()
        {
            IsPlaying = false;
            EditorApplication.update -= Update;
            CurrentTime = 0;

            for (var i = Tweens.Count - 1; i >= 0; i--)
            {
                var tweenData = Tweens[i];
                var tween = tweenData.Tween;

                if (tweenData.IsFrom)
                {
                    // Yes, this is a hack to rewind multiple "from" tweens for the same target
                    tween.Rewind();
                    tween.Complete();
                }
                else
                {
                    tween.Rewind();
                }

                tween.Kill();
            }

            Tweens.Clear();

            QueuePlayerLoopUpdate();
        }

        public static void GoTo(float time)
        {
            CurrentTime = time;
            DOTween.ManualUpdate(time, time);
            QueuePlayerLoopUpdate();
        }

        public static void Add([NotNull] Tween tween, bool isFrom, bool allowCallbacks)
        {
            Tweens.Add(new TweenData(tween, isFrom));
            tween.SetUpdate(UpdateType.Manual);
            tween.SetAutoKill(false);
            if (!allowCallbacks)
            {
                tween.OnComplete(null).OnStart(null).OnPlay(null).OnPause(null).OnUpdate(null).OnWaypointChange(null).OnStepComplete(null).OnRewind(null).OnKill(null);
            }

            tween.Play();
        }

        public static void QueuePlayerLoopUpdate()
        {
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private static void Update()
        {
            var prevTime = CurrentTime;
            CurrentTime = EditorApplication.timeSinceStartup;
            var delta = CurrentTime - prevTime;
            DOTween.ManualUpdate((float)delta, (float)delta);
            QueuePlayerLoopUpdate();

            var activeTweens = Tweens.Any(tweenData => tweenData.Tween.IsPlaying());
            if (!activeTweens)
            {
                Completed?.Invoke();
            }
        }
    }
}
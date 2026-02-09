using DG.Tweening;
using UnityEngine;

[DisallowMultipleComponent]
public class DOTweenPathPreviewProbe : MonoBehaviour
{
    [Tooltip("If enabled, DOTweenPath on the same GameObject is used first.")]
    public bool autoFindOnSameObject = true;

    [Tooltip("Optional manual reference. Used when same-object lookup is disabled or missing.")]
    public DOTweenPath targetPath;

    public DOTweenPath ResolvePath()
    {
        if (autoFindOnSameObject)
        {
            DOTweenPath sameObjectPath = GetComponent<DOTweenPath>();
            if (sameObjectPath != null) return sameObjectPath;
        }
        return targetPath;
    }

    private void OnValidate()
    {
        if (!autoFindOnSameObject) return;
        if (targetPath != null) return;

        DOTweenPath sameObjectPath = GetComponent<DOTweenPath>();
        if (sameObjectPath != null)
        {
            targetPath = sameObjectPath;
        }
    }
}

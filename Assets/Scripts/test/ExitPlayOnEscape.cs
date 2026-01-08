using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Stops Play Mode when the Escape key is pressed while the editor is running.
/// </summary>
public class ExitPlayOnEscape : MonoBehaviour
{
#if UNITY_EDITOR
    private void Update()
    {
        if (!EditorApplication.isPlaying) return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            EditorApplication.isPlaying = false;
        }
    }
#endif
}

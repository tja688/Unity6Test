using UnityEngine;

/// <summary>
/// Tracks how many times the space bar is pressed while this object is active.
/// </summary>
public class SpacePressCounter : MonoBehaviour
{
    [SerializeField]
    private int pressCount;

    private void Update()
    {
        if (!Input.GetKeyDown(KeyCode.Space))
        {
            return;
        }

        pressCount++;
        Debug.Log($"Space pressed {pressCount} time(s)");
    }
}

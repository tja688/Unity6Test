using UnityEngine;

// Moves the attached GameObject to a fixed spot whenever the player presses the "1" key.
public class MoveToPositionOnKey : MonoBehaviour
{
    [SerializeField]
    private Vector3 targetPosition = new Vector3(0f, 1f, 0f);

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
        {
            transform.position = targetPosition;
        }
    }
}

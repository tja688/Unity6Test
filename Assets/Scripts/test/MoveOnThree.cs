using UnityEngine;

public class MoveOnThree : MonoBehaviour
{
    [SerializeField] private Transform target;

    private void Awake()
    {
        if (target == null)
        {
            target = transform;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
        {
            target.position = new Vector3(5f, 4f, 3f);
        }
    }
}

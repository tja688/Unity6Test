using UnityEngine;

public class KeyMoveTarget : MonoBehaviour
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
        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            target.position = new Vector3(0f, 1f, 0f);
        }

        if (Input.GetKeyDown(KeyCode.Q))
        {
            target.position = Vector3.zero;
        }
    }
}

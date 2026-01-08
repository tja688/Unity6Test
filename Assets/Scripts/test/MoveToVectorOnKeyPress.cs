using UnityEngine;

public class MoveToVectorOnKeyPress : MonoBehaviour
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
        if (Input.GetKeyDown(KeyCode.Alpha2))
        {
            target.position = new Vector3(3f, 3f, 3f);
        }
    }
}

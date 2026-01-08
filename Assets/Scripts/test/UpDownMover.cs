using UnityEngine;

public class UpDownMover : MonoBehaviour
{
    public float amplitude = 1f;
    public float speed = 1f;

    private Vector3 _startPos;

    private void Start()
    {
        _startPos = transform.position;
    }

    private void Update()
    {
        float offset = Mathf.Sin(Time.time * speed) * amplitude;
        transform.position = _startPos + Vector3.up * offset;
    }
}

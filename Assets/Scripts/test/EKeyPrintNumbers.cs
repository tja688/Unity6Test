using UnityEngine;

public class EKeyPrintNumbers : MonoBehaviour
{
    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.E))
        {
            for (int i = 1; i <= 10; i++)
            {
                Debug.Log(i);
            }
        }
    }
}

using TMPro;
using UnityEngine;

public class SpaceCounter : MonoBehaviour
{
    [SerializeField] private TMP_Text countText;
    private int count;

    private void Start()
    {
        UpdateText();
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            count++;
            UpdateText();
        }
    }

    private void UpdateText()
    {
        if (countText != null)
        {
            countText.text = count.ToString();
        }
    }
}

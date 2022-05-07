using UnityEngine;

public class TimeWarp : MonoBehaviour
{
    private void Awake()
    {
        Time.timeScale = 1f;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.Period))
        {
            Time.timeScale *= 2f;
            Time.fixedDeltaTime *= 2f;
            PrintTime();
        }
        if (Input.GetKeyDown(KeyCode.Comma))
        {
            Time.timeScale /= 2f;
            Time.fixedDeltaTime /= 2f;
            PrintTime();
        }
    }

    private void PrintTime()
    {
        Debug.Log("TimeScale "+Time.timeScale);
        Debug.Log("FixedDeltaTime "+Time.fixedDeltaTime);
    }
}

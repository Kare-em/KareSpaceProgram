using UnityEngine;

public class Fuel : MonoBehaviour
{
    [SerializeField] private float volume;

    public bool ExpendFuel(float deltaVolume)
    {
        if (volume > 0f)
        {
            volume -= deltaVolume;
            return true;
        }
        else
        {
            return false;
        }
    }
}
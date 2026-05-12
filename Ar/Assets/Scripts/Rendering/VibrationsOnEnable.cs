using System;
using UnityEngine;

public class VibrationsOnEnable : MonoBehaviour
{
    private void OnEnable()
    {
        GetComponent<FeelVibrationPlayer>().PlayConstant(99);
    }

    private void OnDisable()
    {
        GetComponent<FeelVibrationPlayer>().Stop();
    }

}

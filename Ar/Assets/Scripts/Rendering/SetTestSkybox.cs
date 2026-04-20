using UnityEngine;

public class SetTestSkybox : MonoBehaviour
{
    [SerializeField] private Material skyboxMaterial;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // Set Environment skybox
        RenderSettings.skybox = skyboxMaterial;
    }
}

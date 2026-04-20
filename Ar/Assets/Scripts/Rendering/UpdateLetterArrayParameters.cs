using UnityEngine;

namespace Rendering{
[ExecuteAlways]
public class UpdateLetterArrayParameters : MonoBehaviour
{
    [SerializeField] float noiseAmplitude = 0.5f;
    [SerializeField] float noiseScale = 0.5f;

    [SerializeField] [ColorUsage(false, true)]
    private Color color = new Color(0.0f, 6.79047918f, 14.3498688f, 1.0f);
    
    void OnValidate()
    {
        Update();
    }

    void Update()
    {
        foreach (Transform letter in transform)
        {
            letter.GetComponent<Renderer>().sharedMaterial.SetFloat("_NoiseAmplitude", noiseAmplitude);
            letter.GetComponent<Renderer>().sharedMaterial.SetFloat("_NoiseScale", noiseScale);
            letter.GetComponent<Renderer>().sharedMaterial.SetColor("_Color", color);
            
        }
    }
}
}
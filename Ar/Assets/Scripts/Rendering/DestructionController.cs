using UnityEditor;
using UnityEngine;

namespace Rendering
{
    [ExecuteAlways] // Runs in both Editor and Play mode
    public class DestructionController : MonoBehaviour
    {
        private Matrix4x4 lastMatrix;
        [SerializeField] private bool isWorldToLocal = true;
        [SerializeField] private GameObject[] destructables;
        [SerializeField] private string materialParameter = "_WorldToLocal";

        void Awake()
        {
            lastMatrix = transform.worldToLocalMatrix;
        }
    
        void OnValidate()
        {
            Update();
        }
        
        void Update()
        {
            if (transform.worldToLocalMatrix != lastMatrix)
            {
                lastMatrix = isWorldToLocal? transform.worldToLocalMatrix : transform.localToWorldMatrix;
                foreach (GameObject destructable in destructables)
                {
                    destructable.GetComponent<Renderer>().sharedMaterial.SetMatrix(materialParameter , lastMatrix);
                }
            }
        }
    
    }
}

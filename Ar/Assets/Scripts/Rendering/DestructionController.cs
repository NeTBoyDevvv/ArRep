using UnityEditor;
using UnityEngine;

namespace Rendering
{
    [ExecuteAlways] // Runs in both Editor and Play mode
    public class DestructionController : MonoBehaviour
    {
        private Matrix4x4 lastMatrix;
        private Material lastMaterial;
        private Renderer _renderer;
        [SerializeField] private bool isWorldToLocal = true;
        [SerializeField] private GameObject[] destructables;
        [SerializeField] private string materialParameter = "_WorldToLocal";

    
        void OnValidate()
        {
            Update();
        }

        void Start()
        {
            _renderer = GetComponent<Renderer>();
        }
        
        void Update()
        {
            //if (transform.worldToLocalMatrix != lastMatrix ) // TODO: Material change must be considered as well
            //{
                lastMatrix = isWorldToLocal? transform.worldToLocalMatrix : transform.localToWorldMatrix;
                foreach (GameObject destructable in destructables)
                {
                    destructable.GetComponent<Renderer>().sharedMaterial.SetMatrix(materialParameter , lastMatrix);
                }
            //}
        }
    
    }
}

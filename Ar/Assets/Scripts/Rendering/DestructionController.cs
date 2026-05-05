using UnityEditor;
using UnityEngine;

namespace Rendering
{
    [ExecuteAlways] // Runs in both Editor and Play mode
    public class DestructionController : MonoBehaviour
    {
        private Matrix4x4 lastMatrix;
        [SerializeField] private GameObject[] destructables;
    
#if UNITY_EDITOR
        void OnEnable()
        {
            EditorApplication.update += EditorUpdate;
            lastMatrix = transform.worldToLocalMatrix;
        }
    
        void OnDisable()
        {
            EditorApplication.update -= EditorUpdate;
        }
    
        void EditorUpdate()
        {
            // This runs every frame in Editor
            CheckLocationChanges();
        }
    
        void OnValidate()
        {
            // Runs when any serialized field changes
            CheckLocationChanges();
        }
#endif
        void CheckLocationChanges()
        {
            if (transform.worldToLocalMatrix != lastMatrix)
            {
                lastMatrix = transform.worldToLocalMatrix;
                foreach (GameObject destructable in destructables)
                {
                    destructable.GetComponent<Renderer>().sharedMaterial.SetMatrix("_WorldToLocal", lastMatrix);
                }
            }
        }
    
    
    }
}

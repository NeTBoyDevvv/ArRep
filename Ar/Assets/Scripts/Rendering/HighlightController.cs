using UnityEditor;
using UnityEngine;

namespace Rendering
{
    [ExecuteAlways] // Runs in both Editor and Play mode
    public class HighlightController : MonoBehaviour
    {
        [SerializeField] private bool logChanges = true;
        [SerializeField] private bool autoUpdateChildren = false;
    
        private Vector3 _lastPosition;
    
#if UNITY_EDITOR
        void OnEnable()
        {
            EditorApplication.update += EditorUpdate;
            _lastPosition = transform.localPosition;
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
            if (transform.localPosition != _lastPosition)
            {
                OnPositionChanged();
                _lastPosition = transform.localPosition;
            }
        }
    
        void OnPositionChanged()
        {
            SetHighlightPosition();
        }
    
        void SetHighlightPosition()
        {
            transform.parent.GetComponent<Renderer>().sharedMaterial.SetVector("_HighlightPosition", new Vector4(transform.localPosition.x,
                transform.localPosition.y,
                transform.localPosition.z, 0));
        }
    }
}
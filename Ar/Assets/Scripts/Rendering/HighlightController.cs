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
    
        void Awake()
        {
            _lastPosition = transform.localPosition;
        }
        
        void OnValidate()
        {
            Update();
        }
        
        void Update()
        {
            if (transform.localPosition != _lastPosition)
            {
                _lastPosition = transform.localPosition;
                transform.parent.GetComponent<Renderer>().sharedMaterial.SetVector(
                    "_HighlightPosition", new Vector4(
                        _lastPosition.x,
                        _lastPosition.y,
                        _lastPosition.z, 0));
            }
        }
    }
}
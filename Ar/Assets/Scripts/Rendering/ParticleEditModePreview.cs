using UnityEngine;
using CommonTools.Extensions;

[ExecuteAlways]
public class ParticleEditModePreview : MonoBehaviour
{
    private ParticleSystem ps;

    private void OnEnable()
    {
        ps = GetComponent<ParticleSystem>();

#if UNITY_EDITOR
        if (!Application.isPlaying && ps != null)
        {
            ps.PlayInEditor(true);
        }
#endif
    }

    [ContextMenu("Play Particles In Edit Mode")]
    private void PlayInEditMode()
    {
#if UNITY_EDITOR
        if (ps == null)
            ps = GetComponent<ParticleSystem>();

        if (ps != null)
        {
            ps.PlayInEditor(true);
        }
#endif
    }
}
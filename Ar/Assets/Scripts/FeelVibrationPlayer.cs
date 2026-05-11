using Lofelt.NiceVibrations;
using UnityEngine;

public class FeelVibrationPlayer : MonoBehaviour
{
    [Header("Startup")]
    [SerializeField] private bool initializeOnAwake = true;
    [SerializeField] private bool hapticsEnabled = true;
    [SerializeField, Min(0f)] private float outputLevel = 1f;

    [Header("Default")]
    [SerializeField] private HapticPatterns.PresetType defaultPreset = HapticPatterns.PresetType.MediumImpact;
    [SerializeField] private HapticClip defaultClip;
    [SerializeField] private HapticSource hapticSource;

    private void Awake()
    {
        ApplySettings();

        if (initializeOnAwake)
        {
            HapticController.Init();
        }
    }

    private void OnValidate()
    {
        outputLevel = Mathf.Max(0f, outputLevel);
    }

    public void ApplySettings()
    {
        HapticController.hapticsEnabled = hapticsEnabled;
        HapticController.outputLevel = outputLevel;
    }

    public void SetHapticsEnabled(bool isEnabled)
    {
        hapticsEnabled = isEnabled;
        HapticController.hapticsEnabled = hapticsEnabled;
    }

    public void SetOutputLevel(float level)
    {
        outputLevel = Mathf.Max(0f, level);
        HapticController.outputLevel = outputLevel;
    }

    public void PlayDefault()
    {
        PlayPreset(defaultPreset);
    }

    public void Vibrate()
    {
        PlayDefault();
    }

    public void Vibrate(HapticPatterns.PresetType preset)
    {
        PlayPreset(preset);
    }

    public void PlayDefaultClip()
    {
        PlayClip(defaultClip);
    }

    public void PlayHapticSource()
    {
        if (hapticSource != null)
        {
            hapticSource.Play();
        }
    }

    public void PlayClip(HapticClip clip)
    {
        if (clip == null)
        {
            return;
        }

        HapticController.Play(clip);
    }

    public void PlayPreset(HapticPatterns.PresetType preset)
    {
        HapticPatterns.PlayPreset(preset);
    }

    public void PlaySelection()
    {
        PlayPreset(HapticPatterns.PresetType.Selection);
    }

    public void PlaySuccess()
    {
        PlayPreset(HapticPatterns.PresetType.Success);
    }

    public void PlayWarning()
    {
        PlayPreset(HapticPatterns.PresetType.Warning);
    }

    public void PlayFailure()
    {
        PlayPreset(HapticPatterns.PresetType.Failure);
    }

    public void PlayLightImpact()
    {
        PlayPreset(HapticPatterns.PresetType.LightImpact);
    }

    public void VibrateLight()
    {
        PlayLightImpact();
    }

    public void PlayMediumImpact()
    {
        PlayPreset(HapticPatterns.PresetType.MediumImpact);
    }

    public void VibrateMedium()
    {
        PlayMediumImpact();
    }

    public void PlayHeavyImpact()
    {
        PlayPreset(HapticPatterns.PresetType.HeavyImpact);
    }

    public void VibrateHeavy()
    {
        PlayHeavyImpact();
    }

    public void PlayRigidImpact()
    {
        PlayPreset(HapticPatterns.PresetType.RigidImpact);
    }

    public void PlaySoftImpact()
    {
        PlayPreset(HapticPatterns.PresetType.SoftImpact);
    }

    public void PlayEmphasis(float amplitude)
    {
        PlayEmphasis(amplitude, 0.5f);
    }

    public void PlayEmphasis(float amplitude, float frequency)
    {
        HapticPatterns.PlayEmphasis(amplitude, frequency);
    }

    public void PlayConstant(float duration)
    {
        PlayConstant(1f, 0.5f, duration);
    }

    public void PlayConstant(float amplitude, float frequency, float duration)
    {
        HapticPatterns.PlayConstant(amplitude, frequency, duration);
    }

    public void Stop()
    {
        HapticController.Stop();
    }
}

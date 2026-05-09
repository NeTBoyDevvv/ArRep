using UnityEngine;

public class CountdownTimerMaterialTimeLeftBridge : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private bool findCountdownTimerAutomatically = true;
    [SerializeField] private NetworkCountdownTimer countdownTimer;

    [Header("Minutes")]
    [SerializeField] private MeshRenderer minutesRenderer;
    [SerializeField] [Min(0)] private int minutesMaterialIndex;

    [Header("Seconds")]
    [SerializeField] private MeshRenderer secondsRenderer;
    [SerializeField] [Min(0)] private int secondsMaterialIndex;

    [Header("Shader")]
    [SerializeField] private string timeLeftPropertyName = "TimeLeft";

    [Header("Mode")]
    [SerializeField] private bool useWholeSeconds = true;

    private int timeLeftPropertyId;
    private float? lastLoggedMinutesValue;
    private float? lastLoggedSecondsValue;

    private void Awake()
    {
        if ((findCountdownTimerAutomatically || countdownTimer == null) && countdownTimer == null)
            countdownTimer = FindFirstObjectByType<NetworkCountdownTimer>();

        timeLeftPropertyId = Shader.PropertyToID(timeLeftPropertyName);
    }

    private void OnEnable()
    {
        PushTimeLeft();
    }

    private void Update()
    {
        PushTimeLeft();
    }

    private void PushTimeLeft()
    {
        if (countdownTimer == null)
            return;

        float totalSeconds = useWholeSeconds
            ? countdownTimer.RemainingWholeSeconds
            : countdownTimer.RemainingSecondsFloat;

        ApplyTimeLeftToRenderer(minutesRenderer, minutesMaterialIndex, totalSeconds, "Minutes", ref lastLoggedMinutesValue);
        ApplyTimeLeftToRenderer(secondsRenderer, secondsMaterialIndex, totalSeconds, "Seconds", ref lastLoggedSecondsValue);
    }

    private void ApplyTimeLeftToRenderer(
        MeshRenderer targetRenderer,
        int materialIndex,
        float totalSeconds,
        string channelName,
        ref float? lastLoggedValue)
    {
        if (targetRenderer == null)
            return;

        Material[] materials = targetRenderer.materials;
        if (materialIndex < 0 || materialIndex >= materials.Length)
            return;

        Material targetMaterial = materials[materialIndex];
        if (targetMaterial == null)
            return;

        targetMaterial.SetFloat(timeLeftPropertyId, totalSeconds);

        float appliedValue = targetMaterial.GetFloat(timeLeftPropertyId);
        if (!lastLoggedValue.HasValue || !Mathf.Approximately(lastLoggedValue.Value, appliedValue))
        {
            Debug.Log($"[{nameof(CountdownTimerMaterialTimeLeftBridge)}] {channelName} TimeLeft = {appliedValue}", targetRenderer);
            lastLoggedValue = appliedValue;
        }
    }
}

using UnityEngine;
using UnityEngine.Events;

public class CountdownTimerMaterialTimeLeftBridge : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private bool findCountdownTimerAutomatically = true;
    [SerializeField] private NetworkCountdownTimer countdownTimer;

    [Header("Hours")]
    [SerializeField] private MeshRenderer hoursRenderer;
    [SerializeField] [Min(0)] private int hoursMaterialIndex;

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

    [Header("Events")]
    [SerializeField] private UnityEvent onTimerCompleted;

    private bool hasInvokedCompleted;
    private float? lastLoggedHoursValue;
    private float? lastLoggedMinutesValue;
    private float? lastLoggedSecondsValue;

    private void Awake()
    {
        if ((findCountdownTimerAutomatically || countdownTimer == null) && countdownTimer == null)
            countdownTimer = FindFirstObjectByType<NetworkCountdownTimer>(FindObjectsInactive.Include);
    }

    public void ForceComplete()
    {
        if (hasInvokedCompleted)
        {
            Debug.Log($"[TimerBridge] ForceComplete skipped — already invoked.", this);
            return;
        }

        Debug.Log($"[TimerBridge] ForceComplete invoked. Listeners: {onTimerCompleted?.GetPersistentEventCount()}", this);
        hasInvokedCompleted = true;
        onTimerCompleted?.Invoke();
    }

    private void OnEnable()
    {
        CrossPlatformARImageMarkerLauncher launcher = FindFirstObjectByType<CrossPlatformARImageMarkerLauncher>(FindObjectsInactive.Include);
        if (launcher != null && launcher.IsTimerBypassed)
        {
            ForceComplete();
            return;
        }

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

        ApplyTimeLeftToRenderer(hoursRenderer, hoursMaterialIndex, totalSeconds, "Hours", ref lastLoggedHoursValue);
        ApplyTimeLeftToRenderer(minutesRenderer, minutesMaterialIndex, totalSeconds, "Minutes", ref lastLoggedMinutesValue);
        ApplyTimeLeftToRenderer(secondsRenderer, secondsMaterialIndex, totalSeconds, "Seconds", ref lastLoggedSecondsValue);

        if (!hasInvokedCompleted && totalSeconds <= 0f)
        {
            hasInvokedCompleted = true;
            onTimerCompleted?.Invoke();
        }
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

        Material[] materials = targetRenderer.sharedMaterials;
        if (materialIndex < 0 || materialIndex >= materials.Length)
            return;

        Material targetMaterial = materials[materialIndex];
        if (targetMaterial == null)
            return;

        targetMaterial.SetFloat(timeLeftPropertyName, totalSeconds);

        float appliedValue = targetMaterial.GetFloat(timeLeftPropertyName);
        if (!lastLoggedValue.HasValue || !Mathf.Approximately(lastLoggedValue.Value, appliedValue))
        {
            Debug.Log($"[{nameof(CountdownTimerMaterialTimeLeftBridge)}] {channelName} TimeLeft = {appliedValue}", targetRenderer);
            lastLoggedValue = appliedValue;
        }
    }
}

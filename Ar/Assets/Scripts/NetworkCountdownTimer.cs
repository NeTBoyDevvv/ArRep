using System;
using System.Collections;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

public class NetworkCountdownTimer : MonoBehaviour
{
    [Serializable]
    public class CountdownReachedEvent : UnityEvent
    {
    }

    [Header("UI")]
    [SerializeField] private TMP_Text timerText;
    [SerializeField] private bool appendTimeSourceLabel;
    [SerializeField] private string expiredText = "00:00:00";

    [Header("Target Time")]
    [SerializeField] private string targetMoscowTimeIso = "2026-05-18T19:00:00+03:00";

    [Header("Sync")]
    [SerializeField] [Min(1)] private int requestTimeoutSeconds = 10;
    [SerializeField] [Min(5f)] private float retryDelayIfOffline = 30f;
    [SerializeField] [Min(30f)] private float resyncDelayIfOnline = 300f;

    [Header("Completion Spawn")]
    [SerializeField] private GameObject completionSpawnPrefab;
    [SerializeField] private Transform completionSpawnReference;
    [SerializeField] private bool parentSpawnedPrefabToReference;
    [SerializeField] private Vector3 localPositionOffset = Vector3.zero;
    [SerializeField] private Vector3 localEulerOffset = Vector3.zero;
    [SerializeField] private Vector3 localScaleMultiplier = Vector3.one;
    [SerializeField] private bool disableTimerGameObjectAfterCompletion = true;

    [Header("Event")]
    [SerializeField] private CountdownReachedEvent onReachedUsingInternetTime;

    private DateTimeOffset targetTime;
    private DateTimeOffset lastFetchedInternetUtc;
    private TimeSpan internetTimeOffset = TimeSpan.Zero;
    private bool hasTrustedInternetTime;
    private bool hasInvokedReachedEvent;
    private bool lastFetchSucceeded;
    private Coroutine syncRoutine;
    private GameObject spawnedCompletionObject;

    public bool HasTrustedInternetTime => hasTrustedInternetTime;
    public bool IsUsingLocalFallback => !hasTrustedInternetTime;
    public bool HasInvokedReachedEvent => hasInvokedReachedEvent;
    public TimeSpan RemainingTime => GetRemainingTime();
    public float RemainingSecondsFloat => Mathf.Max(0f, (float)GetRemainingTime().TotalSeconds);
    public int RemainingWholeSeconds => Mathf.Max(0, Mathf.CeilToInt((float)GetRemainingTime().TotalSeconds));
    public GameObject SpawnedCompletionObject => spawnedCompletionObject;

    private void Awake()
    {
        if (timerText == null)
        {
            timerText = GetComponent<TMP_Text>();
        }

        if (!TryParseTargetTime(out targetTime))
        {
            Debug.LogError($"[{nameof(NetworkCountdownTimer)}] Invalid target time: {targetMoscowTimeIso}", this);
            enabled = false;
        }
    }

    private void OnEnable()
    {
        if (!enabled)
        {
            return;
        }

        UpdateTimerText();

        if (syncRoutine == null)
        {
            syncRoutine = StartCoroutine(SyncTimeLoop());
        }
    }

    private void OnDisable()
    {
        if (syncRoutine != null)
        {
            StopCoroutine(syncRoutine);
            syncRoutine = null;
        }
    }

    private void Update()
    {
        UpdateTimerText();
        TryInvokeReachedEvent();
    }

    private bool TryParseTargetTime(out DateTimeOffset parsedTime)
    {
        return DateTimeOffset.TryParse(
            targetMoscowTimeIso,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsedTime);
    }

    private IEnumerator SyncTimeLoop()
    {
        while (true)
        {
            yield return TrySyncInternetTime();

            float delay = hasTrustedInternetTime ? resyncDelayIfOnline : retryDelayIfOffline;
            yield return new WaitForSecondsRealtime(delay);
        }
    }

    private IEnumerator TrySyncInternetTime()
    {
        yield return TryFetchUtcFromWorldTimeApi(
            "https://worldtimeapi.org/api/timezone/Etc/UTC",
            result => lastFetchedInternetUtc = result);

        if (lastFetchSucceeded)
        {
            ApplyInternetTime(lastFetchedInternetUtc);
            yield break;
        }

        yield return TryFetchUtcFromResponseHeader(
            "https://www.google.com/generate_204",
            result => lastFetchedInternetUtc = result);

        if (lastFetchSucceeded)
        {
            ApplyInternetTime(lastFetchedInternetUtc);
            yield break;
        }

        yield return TryFetchUtcFromResponseHeader(
            "https://www.cloudflare.com/cdn-cgi/trace",
            result => lastFetchedInternetUtc = result);

        if (lastFetchSucceeded)
        {
            ApplyInternetTime(lastFetchedInternetUtc);
        }
    }

    private void ApplyInternetTime(DateTimeOffset internetUtcTime)
    {
        internetTimeOffset = internetUtcTime - DateTimeOffset.UtcNow;

        if (!hasTrustedInternetTime)
        {
            Debug.Log($"[{nameof(NetworkCountdownTimer)}] Internet time sync established.", this);
        }

        hasTrustedInternetTime = true;
        UpdateTimerText();
        TryInvokeReachedEvent();
    }

    private IEnumerator TryFetchUtcFromWorldTimeApi(string url, Action<DateTimeOffset> onSuccess)
    {
        lastFetchSucceeded = false;

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = requestTimeoutSeconds;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                yield break;
            }

            WorldTimeApiResponse response = JsonUtility.FromJson<WorldTimeApiResponse>(request.downloadHandler.text);
            if (response == null)
            {
                yield break;
            }

            if (TryParseInternetTimestamp(response.utc_datetime, out DateTimeOffset parsedUtc) ||
                TryParseInternetTimestamp(response.datetime, out parsedUtc))
            {
                onSuccess(parsedUtc.ToUniversalTime());
                lastFetchSucceeded = true;
            }
        }
    }

    private IEnumerator TryFetchUtcFromResponseHeader(string url, Action<DateTimeOffset> onSuccess)
    {
        lastFetchSucceeded = false;

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = requestTimeoutSeconds;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                yield break;
            }

            string dateHeader = request.GetResponseHeader("Date");
            if (string.IsNullOrWhiteSpace(dateHeader))
            {
                yield break;
            }

            if (DateTimeOffset.TryParse(
                dateHeader,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsedUtc))
            {
                onSuccess(parsedUtc.ToUniversalTime());
                lastFetchSucceeded = true;
            }
        }
    }

    private bool TryParseInternetTimestamp(string rawValue, out DateTimeOffset parsedValue)
    {
        return DateTimeOffset.TryParse(
            rawValue,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out parsedValue);
    }

    private DateTimeOffset GetCurrentReferenceTime()
    {
        if (hasTrustedInternetTime)
        {
            return DateTimeOffset.UtcNow + internetTimeOffset;
        }

        return DateTimeOffset.Now;
    }

    private TimeSpan GetRemainingTime()
    {
        TimeSpan remaining = targetTime - GetCurrentReferenceTime();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void UpdateTimerText()
    {
        if (timerText == null)
        {
            return;
        }

        TimeSpan remaining = GetRemainingTime();
        if (remaining <= TimeSpan.Zero)
        {
            timerText.text = BuildFinalText(expiredText);
            return;
        }

        string text = $"{Mathf.Max(0, (int)remaining.TotalHours):00}:{remaining.Minutes:00}:{remaining.Seconds:00}";

        timerText.text = BuildFinalText(text);
    }

    private string BuildFinalText(string baseText)
    {
        if (!appendTimeSourceLabel)
        {
            return baseText;
        }

        return hasTrustedInternetTime
            ? $"{baseText} [NET]"
            : $"{baseText} [LOCAL]";
    }

    private void TryInvokeReachedEvent()
    {
        if (hasInvokedReachedEvent || !hasTrustedInternetTime)
        {
            return;
        }

        if (GetCurrentReferenceTime() < targetTime)
        {
            return;
        }

        hasInvokedReachedEvent = true;
        SpawnCompletionPrefab();
        onReachedUsingInternetTime?.Invoke();

        if (disableTimerGameObjectAfterCompletion)
        {
            gameObject.SetActive(false);
        }
    }

    private void SpawnCompletionPrefab()
    {
        if (completionSpawnPrefab == null || spawnedCompletionObject != null)
        {
            return;
        }

        Quaternion localRotationOffset = Quaternion.Euler(localEulerOffset);
        Vector3 spawnedScale = Vector3.Scale(completionSpawnPrefab.transform.localScale, localScaleMultiplier);

        if (completionSpawnReference != null && parentSpawnedPrefabToReference)
        {
            spawnedCompletionObject = Instantiate(completionSpawnPrefab, completionSpawnReference, false);
            spawnedCompletionObject.transform.localPosition = localPositionOffset;
            spawnedCompletionObject.transform.localRotation = localRotationOffset;
            spawnedCompletionObject.transform.localScale = spawnedScale;
            return;
        }

        Vector3 worldPosition = localPositionOffset;
        Quaternion worldRotation = localRotationOffset;
        if (completionSpawnReference != null)
        {
            worldPosition = completionSpawnReference.TransformPoint(localPositionOffset);
            worldRotation = completionSpawnReference.rotation * localRotationOffset;
        }

        spawnedCompletionObject = Instantiate(completionSpawnPrefab, worldPosition, worldRotation);
        spawnedCompletionObject.transform.localScale = spawnedScale;
    }

    [Serializable]
    private class WorldTimeApiResponse
    {
        public string datetime;
        public string utc_datetime;
    }
}

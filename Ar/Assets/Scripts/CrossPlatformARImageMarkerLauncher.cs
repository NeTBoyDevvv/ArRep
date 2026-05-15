using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class CrossPlatformARImageMarkerLauncher : MonoBehaviour
{
    [System.Serializable]
    private class JsonTransformSettingsRow
    {
        public string key;
        public string id;
        public string name;
        public string marker;
        public string markerName;
        public string trackedImage;
        public string trackedImageName;
        public string image;
        public string imageName;
        public string objectName;
        public string prefabName;

        public float[] position;
        public float[] localPosition;
        public float[] positionOffset;
        public float[] localPositionOffset;
        public float[] spawnPosition;
        public float[] spawnPositionOffset;

        public float[] rotation;
        public float[] localRotation;
        public float[] euler;
        public float[] localEuler;
        public float[] localEulerOffset;
        public float[] spawnRotation;

        public float[] scale;
        public float[] localScale;
        public float[] scaleMultiplier;
        public float[] localScaleMultiplier;
        public float[] spawnScale;

        public string targetTime;
        public string targetMoscowTime;
        public string endTime;
    }

    [System.Serializable]
    private sealed class JsonTransformSettingsDocument : JsonTransformSettingsRow
    {
        public JsonTransformSettingsRow[] rows;
        public JsonTransformSettingsRow[] items;
        public JsonTransformSettingsRow[] transforms;
        public JsonTransformSettingsRow[] settings;
    }

    private sealed class RuntimeValueControl
    {
        public InputField InputField;
    }

    [Header("Launch")]
    [SerializeField] private Button startArButton;
    [SerializeField] private Camera sceneCameraToDisable;
    [SerializeField] private GameObject[] objectsToDisableOnStart;
    [SerializeField] private GameObject[] objectsToKeepActiveOnStart;
    [SerializeField] private CanvasGroup[] canvasGroupsToHideOnStart;

    [Header("AR Camera")]
    [SerializeField] private float arCameraNearClipPlane = 0.01f;
    [SerializeField] private float arCameraFarClipPlane = 100f;
    [SerializeField] private bool enablePostProcessing = false;

    [Header("Marker Tracking")]
    [SerializeField] private XRReferenceImageLibrary referenceImageLibrary;
    [SerializeField] private string trackedImageNameFilter = "";
    [SerializeField] private int maxMovingImages = 0;
    [SerializeField] private bool hideContentWhenMarkerNotTracked = true;

    [Header("Content")]
    [SerializeField] private GameObject placementPrefab;
    [SerializeField] private float fallbackCubeSizeMeters = 0.15f;
    [SerializeField] private float markerlessSpawnDistance = 1.5f;
    [SerializeField] private Vector3 localPositionOffset = Vector3.zero;
    [SerializeField] private Vector3 localEulerOffset = Vector3.zero;
    [SerializeField] private Vector3 localScaleMultiplier = Vector3.one;
    [SerializeField] private bool useFixedWorldScale = true;
    [SerializeField] private bool lockContentAfterFirstMarkerFound = true;
    [SerializeField] private float lockDelaySeconds = 0f;
    [SerializeField] private bool keepPlacedObjectFacingCameraOnY = false;



    [Header("Adjustment UI")]
    [SerializeField] private bool showAdjustmentUiAfterLock = true;
    [SerializeField] private float positionAdjustmentRange = 1f;
    [SerializeField] private float scaleAdjustmentMin = 0.01f;
    [SerializeField] private float scaleAdjustmentMax = 3f;
    [SerializeField] private bool enableAdjustmentUi = true;

    [Header("Performance")]
    [SerializeField] private bool disableVSync = true;
    [SerializeField] private bool useDisplayRefreshRate = true;
    [SerializeField] private int fallbackTargetFrameRate = 120;

    [Header("Events")]
    [SerializeField] private UnityEvent onArStarted;
    [SerializeField] private UnityEvent onMarkerFound;
    [SerializeField] private UnityEvent onArUnsupported;

    [Header("Web Transform Settings")]
    [SerializeField, InspectorName("Transform Settings Url")] private string transformSettingsCsvUrl = "";
    [SerializeField] private string transformSettingsRowKey = "";
    [SerializeField] private bool loadTransformSettingsOnStart = true;
    [SerializeField] private bool refreshTransformSettingsPeriodically;
    [SerializeField, Min(1)] private int transformSettingsRequestTimeoutSeconds = 10;
    [SerializeField, Min(5f)] private float transformSettingsRefreshSeconds = 30f;

    [Header("Timer Settings")]
    [SerializeField] private NetworkCountdownTimer networkCountdownTimer;
    [SerializeField] private bool refreshTargetTimePeriodically;


    private readonly Dictionary<TrackableId, GameObject> spawnedContent = new Dictionary<TrackableId, GameObject>();

    private GameObject arSessionObject;
    private GameObject xrOriginObject;
    private GameObject arCameraObject;
    private ARSession arSession;
    private ARInputManager arInputManager;
    private XROrigin xrOrigin;
    private Camera arCamera;
    private ARTrackedImageManager trackedImageManager;
    private ARAnchorManager arAnchorManager;
    private ARCameraManager arCameraManager;
    private ARCameraPoseFallback arCameraPoseFallback;
    private bool arStartRequested;
    private bool didInvokeStarted;
    private bool contentLockedToWorld;
    private TrackableId pendingLockTrackableId;
    private GameObject pendingLockContent;
    private float pendingLockElapsed;
    private float logTimer;
    private GameObject lockedContentObject;
    private ARAnchor lockedContentAnchor;
    private GameObject adjustmentCanvasObject;
    private GameObject adjustmentPanelObject;
    private Font runtimeUiFont;
    private Sprite runtimeUiSprite;
    private bool suppressAdjustmentUiCallbacks;
    private RuntimeValueControl positionXControl;
    private RuntimeValueControl positionYControl;
    private RuntimeValueControl positionZControl;
    private RuntimeValueControl rotationXControl;
    private RuntimeValueControl rotationYControl;
    private RuntimeValueControl rotationZControl;
    private RuntimeValueControl scaleControl;
    private Text recommendedSpawnValuesText;
    private bool hasLockedReferencePose;
    private Vector3 lockedReferencePosition;
    private Quaternion lockedReferenceRotation = Quaternion.identity;
    private Vector3 lockedReferenceLossyScale = Vector3.one;
    private bool spawnWithoutMarkerOnStart;
    private Coroutine transformSettingsRoutine;
    private bool timerBypassed;

    private void Awake()
    {
        if (networkCountdownTimer == null)
            networkCountdownTimer = FindFirstObjectByType<NetworkCountdownTimer>(FindObjectsInactive.Include);

        ApplyPerformanceSettings();
        BuildARRig();
        BuildAdjustmentUi();
        Debug.Log($"[AR Marker] Awake. Initial state = {ARSession.state}");
    }

    private void OnEnable()
    {
        if (startArButton != null)
            startArButton.onClick.AddListener(StartAR);

        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.AddListener(OnTrackedImagesChanged);

        if (loadTransformSettingsOnStart)
            StartTransformSettingsRoutine();
    }

    private void OnDisable()
    {
        if (startArButton != null)
            startArButton.onClick.RemoveListener(StartAR);

        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);

        StopTransformSettingsRoutine();
    }

    private void Update()
    {
        if (!arStartRequested)
            return;

        logTimer += Time.deltaTime;
        if (logTimer >= 1f)
        {
            logTimer = 0f;
            Debug.Log($"[AR Marker] State={ARSession.state} tracked={spawnedContent.Count}");
        }

        if (!didInvokeStarted &&
            (ARSession.state == ARSessionState.SessionInitializing ||
             ARSession.state == ARSessionState.SessionTracking))
        {
            didInvokeStarted = true;
            onArStarted?.Invoke();
        }

        if (ARSession.state == ARSessionState.Unsupported)
        {
            onArUnsupported?.Invoke();
            arStartRequested = false;
            return;
        }

        ApplyAdjustmentUiToLockedContent();
        UpdatePendingWorldLock();

        if (keepPlacedObjectFacingCameraOnY && !contentLockedToWorld)
            UpdateSpawnedContentFacing();
    }

    public void StartAR()
    {
        StartARInternal(false);
    }

    public bool StartARWithCode(string code)
    {
        if (code == "1419")
        {
            StartARAndSpawnWithoutMarker();
            ForceTimerCompletion();
            return true;
        }

        if (code == "0000")
        {
            StartAR();
            ForceTimerCompletion();
            return true;
        }

        if (code == "1111")
        {
            StartAR();
            ForceTimerCompletion();
            enableAdjustmentUi = true;
            showAdjustmentUiAfterLock = true;
            BuildAdjustmentUi();
            SetAdjustmentUiVisible(true);
            return true;
        }

        return false;
    }

    public bool IsTimerBypassed => timerBypassed;

    private void ForceTimerCompletion()
    {
        timerBypassed = true;

        CountdownTimerMaterialTimeLeftBridge[] bridges = FindObjectsByType<CountdownTimerMaterialTimeLeftBridge>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        Debug.Log($"[AR Code] ForceTimerCompletion: found {bridges.Length} bridge(s).");

        foreach (CountdownTimerMaterialTimeLeftBridge bridge in bridges)
            bridge.ForceComplete();
    }

    public void StartARAndSpawnWithoutMarker()
    {
        if (arStartRequested)
        {
            SpawnContentWithoutMarker();
            return;
        }

        StartARInternal(true);
    }

    private void StartARInternal(bool spawnWithoutMarker)
    {
        if (arStartRequested)
            return;

        if (!spawnWithoutMarker && referenceImageLibrary == null)
        {
            Debug.LogError("[AR Marker] Reference Image Library is not assigned.");
            return;
        }

        spawnWithoutMarkerOnStart = spawnWithoutMarker;
        arStartRequested = true;
        didInvokeStarted = false;
        contentLockedToWorld = false;
        pendingLockTrackableId = default;
        pendingLockContent = null;
        pendingLockElapsed = 0f;
        lockedContentObject = null;
        SetAdjustmentUiVisible(false);
        ApplyPerformanceSettings();
        if (loadTransformSettingsOnStart)
            StartTransformSettingsRoutine();
        StartCoroutine(StartARRoutine());
    }

    public void RefreshTransformSettingsFromGoogleSheet()
    {
        RefreshTransformSettingsFromWeb();
    }

    public void RefreshTransformSettingsFromWeb()
    {
        if (string.IsNullOrWhiteSpace(transformSettingsCsvUrl))
        {
            Debug.LogWarning("[AR Marker] Transform settings URL is empty.", this);
            return;
        }

        StopTransformSettingsRoutine();
        transformSettingsRoutine = StartCoroutine(TransformSettingsRoutine(oneShot: true));
    }

    public void StopAR()
    {
        arStartRequested = false;
        trackedImageManager.enabled = false;
        arSessionObject?.SetActive(false);
        xrOriginObject?.SetActive(false);
        contentLockedToWorld = false;
        pendingLockTrackableId = default;
        pendingLockContent = null;
        pendingLockElapsed = 0f;
        spawnWithoutMarkerOnStart = false;
        SetAdjustmentUiVisible(false);
        DestroySpawnedContent();
    }

    private IEnumerator StartARRoutine()
    {
#if UNITY_ANDROID
        yield return RequestCameraPermission();
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Debug.LogWarning("[AR Marker] Camera permission denied.");
            arStartRequested = false;
            yield break;
        }
#endif
        DisableSceneObjectsForAR();
        arSessionObject.SetActive(true);
        xrOriginObject.SetActive(true);
        RefreshArInputManager();
        ApplyArCameraClipping();

        if (spawnWithoutMarkerOnStart)
        {
            trackedImageManager.enabled = false;
        }
        else
        {
            trackedImageManager.referenceLibrary = referenceImageLibrary;
            trackedImageManager.requestedMaxNumberOfMovingImages = Mathf.Max(0, maxMovingImages);
            trackedImageManager.enabled = true;
        }

        Debug.Log("[AR Marker] Checking availability...");
        yield return ARSession.CheckAvailability();
        Debug.Log($"[AR Marker] Availability state = {ARSession.state}");

        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            Debug.Log("[AR Marker] Installing XR support...");
            yield return ARSession.Install();
            Debug.Log($"[AR Marker] State after install = {ARSession.state}");
        }

        if (ARSession.state == ARSessionState.Unsupported)
        {
            Debug.LogError("[AR Marker] Device does not support AR.");
            onArUnsupported?.Invoke();
            arStartRequested = false;
            yield break;
        }

        float elapsed = 0f;
        while (ARSession.state != ARSessionState.SessionInitializing &&
               ARSession.state != ARSessionState.SessionTracking)
        {
            elapsed += Time.deltaTime;
            if (elapsed >= 20f)
            {
                Debug.LogWarning($"[AR Marker] Session did not start in time. Current state = {ARSession.state}");
                break;
            }

            yield return null;
        }

        RefreshArInputManager();
        Debug.Log($"[AR Marker] Runtime session state = {ARSession.state}");

        if (spawnWithoutMarkerOnStart)
            SpawnContentWithoutMarker();
    }

    private void StartTransformSettingsRoutine()
    {
        if (transformSettingsRoutine != null || string.IsNullOrWhiteSpace(transformSettingsCsvUrl))
            return;

        transformSettingsRoutine = StartCoroutine(TransformSettingsRoutine(oneShot: false));
    }

    private void StopTransformSettingsRoutine()
    {
        if (transformSettingsRoutine == null)
            return;

        StopCoroutine(transformSettingsRoutine);
        transformSettingsRoutine = null;
    }

    private IEnumerator TransformSettingsRoutine(bool oneShot)
    {
        do
        {
            yield return LoadTransformSettingsFromWeb();

            if (oneShot || (!refreshTransformSettingsPeriodically && !refreshTargetTimePeriodically))
                break;

            yield return new WaitForSecondsRealtime(Mathf.Max(5f, transformSettingsRefreshSeconds));
        }
        while (isActiveAndEnabled);

        transformSettingsRoutine = null;
    }

    private IEnumerator LoadTransformSettingsFromWeb()
    {
        string url = BuildTransformSettingsUrl(transformSettingsCsvUrl);
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = Mathf.Max(1, transformSettingsRequestTimeoutSeconds);
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[AR Marker] Could not load transform settings from web. {request.error}", this);
                yield break;
            }

            if (TryApplyTransformSettingsPayload(request.downloadHandler.text, out string resultMessage))
                Debug.Log($"[AR Marker] Transform settings loaded from web. {resultMessage}", this);
            else
                Debug.LogWarning($"[AR Marker] Web transform settings were not applied. {resultMessage}", this);
        }
    }

    private bool TryApplyTransformSettingsPayload(string text, out string resultMessage)
    {
        string trimmedText = text.TrimStart();
        if (trimmedText.StartsWith("{") || trimmedText.StartsWith("["))
            return TryApplyTransformSettingsJson(text, out resultMessage);

        return TryApplyTransformSettingsCsv(text, out resultMessage);
    }

    private bool TryApplyTransformSettingsJson(string jsonText, out string resultMessage)
    {
        resultMessage = "";
        string trimmedJson = jsonText.Trim();
        if (trimmedJson.StartsWith("["))
            trimmedJson = "{\"rows\":" + trimmedJson + "}";

        JsonTransformSettingsDocument document;
        try
        {
            document = JsonUtility.FromJson<JsonTransformSettingsDocument>(trimmedJson);
        }
        catch (System.Exception exception)
        {
            resultMessage = $"JSON parse error: {exception.Message}";
            return false;
        }

        if (document == null)
        {
            resultMessage = "JSON response is empty or invalid.";
            return false;
        }

        JsonTransformSettingsRow row = FindTransformSettingsRow(document, transformSettingsRowKey);
        if (row == null)
        {
            resultMessage = string.IsNullOrWhiteSpace(transformSettingsRowKey)
                ? "No JSON row with transform values was found."
                : $"No JSON row matched key '{transformSettingsRowKey}'.";
            return false;
        }

        bool applied = false;
        StringBuilder sb = new StringBuilder("JSON:");

        if (TryReadTransformValues(row, out Vector3 position, out Vector3 rotation, out Vector3 scale))
        {
            bool changed = position != localPositionOffset || rotation != localEulerOffset || scale != localScaleMultiplier;
            localPositionOffset = position;
            localEulerOffset = rotation;
            localScaleMultiplier = scale;
            if (changed)
                ApplyRuntimeTransformSettings();
            sb.Append($" pos={FormatVector3(localPositionOffset)}, rot={FormatVector3(localEulerOffset)}, scale={FormatVector3(localScaleMultiplier)}.");
            applied = true;
        }

        string rawTargetTime = !string.IsNullOrWhiteSpace(row.targetTime) ? row.targetTime
            : !string.IsNullOrWhiteSpace(row.targetMoscowTime) ? row.targetMoscowTime
            : row.endTime;
        if (!string.IsNullOrWhiteSpace(rawTargetTime) && networkCountdownTimer != null &&
            networkCountdownTimer.ApplyTargetTimeFromSettings(rawTargetTime))
        {
            sb.Append($" targetTime={rawTargetTime}.");
            applied = true;
        }

        if (!applied)
        {
            resultMessage = "Selected JSON row does not contain transform or timer values.";
            return false;
        }

        resultMessage = sb.ToString();
        return true;
    }

    private bool TryApplyTransformSettingsCsv(string csvText, out string resultMessage)
    {
        resultMessage = "";

        List<List<string>> rows = ParseDelimitedText(csvText);
        if (rows.Count < 2)
        {
            resultMessage = "The CSV must contain a header row and at least one data row.";
            return false;
        }

        Dictionary<string, int> headerMap = BuildHeaderMap(rows[0]);
        if (headerMap.Count == 0)
        {
            resultMessage = "No usable headers found.";
            return false;
        }

        int selectedRowIndex = FindTransformSettingsRow(rows, headerMap, transformSettingsRowKey);
        if (selectedRowIndex < 0)
        {
            resultMessage = string.IsNullOrWhiteSpace(transformSettingsRowKey)
                ? "No row with transform values was found."
                : $"No row matched key '{transformSettingsRowKey}'.";
            return false;
        }

        bool applied = false;
        StringBuilder sb = new StringBuilder($"Row {selectedRowIndex + 1}:");

        if (TryReadTransformValues(rows[selectedRowIndex], headerMap, out Vector3 position, out Vector3 rotation, out Vector3 scale))
        {
            bool changed = position != localPositionOffset || rotation != localEulerOffset || scale != localScaleMultiplier;
            localPositionOffset = position;
            localEulerOffset = rotation;
            localScaleMultiplier = scale;
            if (changed)
                ApplyRuntimeTransformSettings();
            sb.Append($" pos={FormatVector3(localPositionOffset)}, rot={FormatVector3(localEulerOffset)}, scale={FormatVector3(localScaleMultiplier)}.");
            applied = true;
        }

        string[] targetTimeAliases = { "targetTime", "targetMoscowTime", "endTime" };
        foreach (string alias in targetTimeAliases)
        {
            if (TryGetRawField(rows[selectedRowIndex], headerMap, alias, out string rawTargetTime) &&
                !string.IsNullOrWhiteSpace(rawTargetTime) &&
                networkCountdownTimer != null &&
                networkCountdownTimer.ApplyTargetTimeFromSettings(rawTargetTime))
            {
                sb.Append($" targetTime={rawTargetTime}.");
                applied = true;
                break;
            }
        }

        if (!applied)
        {
            resultMessage = $"Row {selectedRowIndex + 1} does not contain transform or timer columns.";
            return false;
        }

        resultMessage = sb.ToString();
        return true;
    }

    private JsonTransformSettingsRow FindTransformSettingsRow(JsonTransformSettingsDocument document, string rowKey)
    {
        JsonTransformSettingsRow[] rows = document.rows ?? document.items ?? document.transforms ?? document.settings;
        string effectiveRowKey = !string.IsNullOrWhiteSpace(rowKey) ? rowKey : trackedImageNameFilter;
        if (rows != null && rows.Length > 0)
        {
            if (!string.IsNullOrWhiteSpace(effectiveRowKey))
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (JsonRowMatchesKey(rows[i], effectiveRowKey))
                        return rows[i];
                }

                if (!string.IsNullOrWhiteSpace(rowKey))
                    return null;
            }

            for (int i = 0; i < rows.Length; i++)
            {
                if (TryReadTransformValues(rows[i], out _, out _, out _))
                    return rows[i];
            }
        }

        if (!string.IsNullOrWhiteSpace(effectiveRowKey) && !JsonRowMatchesKey(document, effectiveRowKey))
            return null;

        return TryReadTransformValues(document, out _, out _, out _) ? document : null;
    }

    private bool TryReadTransformValues(
        JsonTransformSettingsRow row,
        out Vector3 position,
        out Vector3 rotation,
        out Vector3 scale)
    {
        bool hasAnyValue = false;
        position = localPositionOffset;
        rotation = localEulerOffset;
        scale = localScaleMultiplier;
        if (row == null)
            return false;

        if (TryReadVector3(GetFirstVector(row.position, row.localPosition, row.positionOffset, row.localPositionOffset, row.spawnPosition, row.spawnPositionOffset),
                position, false, out Vector3 parsedPosition))
        {
            position = parsedPosition;
            hasAnyValue = true;
        }

        if (TryReadVector3(GetFirstVector(row.rotation, row.localRotation, row.euler, row.localEuler, row.localEulerOffset, row.spawnRotation),
                rotation, false, out Vector3 parsedRotation))
        {
            rotation = parsedRotation;
            hasAnyValue = true;
        }

        if (TryReadVector3(GetFirstVector(row.scale, row.localScale, row.scaleMultiplier, row.localScaleMultiplier, row.spawnScale),
                scale, true, out Vector3 parsedScale))
        {
            scale = parsedScale;
            hasAnyValue = true;
        }

        return hasAnyValue;
    }

    private static float[] GetFirstVector(params float[][] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] != null && values[i].Length > 0)
                return values[i];
        }

        return null;
    }

    private static bool TryReadVector3(float[] values, Vector3 fallback, bool allowUniformSingleValue, out Vector3 value)
    {
        value = fallback;
        if (values == null || values.Length == 0)
            return false;

        if (values.Length == 1 && allowUniformSingleValue)
        {
            value = Vector3.one * values[0];
            return true;
        }

        if (values.Length < 3)
            return false;

        value = new Vector3(values[0], values[1], values[2]);
        return true;
    }

    private static bool JsonRowMatchesKey(JsonTransformSettingsRow row, string rowKey)
    {
        if (row == null)
            return false;

        return ValuesMatch(row.key, rowKey) ||
               ValuesMatch(row.id, rowKey) ||
               ValuesMatch(row.name, rowKey) ||
               ValuesMatch(row.marker, rowKey) ||
               ValuesMatch(row.markerName, rowKey) ||
               ValuesMatch(row.trackedImage, rowKey) ||
               ValuesMatch(row.trackedImageName, rowKey) ||
               ValuesMatch(row.image, rowKey) ||
               ValuesMatch(row.imageName, rowKey) ||
               ValuesMatch(row.objectName, rowKey) ||
               ValuesMatch(row.prefabName, rowKey);
    }

    private void ApplyRuntimeTransformSettings()
    {
        if (lockedContentObject != null)
        {
            ApplyLockedContentFromInspectorValues();
            SyncAdjustmentUiFromContent(lockedContentObject);
            return;
        }

        foreach (GameObject content in spawnedContent.Values)
        {
            if (content == null)
                continue;

            content.transform.localPosition = localPositionOffset;
            content.transform.localRotation = Quaternion.Euler(localEulerOffset);
            ApplyContentScale(content.transform, content.transform.parent);
        }
    }

    private int FindTransformSettingsRow(List<List<string>> rows, Dictionary<string, int> headerMap, string rowKey)
    {
        string effectiveRowKey = !string.IsNullOrWhiteSpace(rowKey) ? rowKey : trackedImageNameFilter;
        if (!string.IsNullOrWhiteSpace(effectiveRowKey))
        {
            for (int i = 1; i < rows.Count; i++)
            {
                if (RowMatchesKey(rows[i], headerMap, effectiveRowKey))
                    return i;
            }

            if (!string.IsNullOrWhiteSpace(rowKey))
                return -1;
        }

        for (int i = 1; i < rows.Count; i++)
        {
            if (TryReadTransformValues(rows[i], headerMap, out _, out _, out _))
                return i;
        }

        return -1;
    }

    private bool TryReadTransformValues(
        List<string> row,
        Dictionary<string, int> headerMap,
        out Vector3 position,
        out Vector3 rotation,
        out Vector3 scale)
    {
        bool hasAnyValue = false;
        position = localPositionOffset;
        rotation = localEulerOffset;
        scale = localScaleMultiplier;

        if (TryReadVector3(row, headerMap, position, false, out Vector3 parsedPosition,
                new[] { "position", "localPosition", "positionOffset", "localPositionOffset", "spawnPosition", "spawnPositionOffset" },
                new[] { "posX", "positionX", "localPositionX", "offsetX", "spawnPositionX", "x" },
                new[] { "posY", "positionY", "localPositionY", "offsetY", "spawnPositionY", "y" },
                new[] { "posZ", "positionZ", "localPositionZ", "offsetZ", "spawnPositionZ", "z" }))
        {
            position = parsedPosition;
            hasAnyValue = true;
        }

        if (TryReadVector3(row, headerMap, rotation, false, out Vector3 parsedRotation,
                new[] { "rotation", "localRotation", "euler", "localEuler", "localEulerOffset", "spawnRotation" },
                new[] { "rotX", "rotationX", "eulerX", "localEulerX", "rx", "spawnRotationX" },
                new[] { "rotY", "rotationY", "eulerY", "localEulerY", "ry", "spawnRotationY" },
                new[] { "rotZ", "rotationZ", "eulerZ", "localEulerZ", "rz", "spawnRotationZ" }))
        {
            rotation = parsedRotation;
            hasAnyValue = true;
        }

        if (TryReadVector3(row, headerMap, scale, true, out Vector3 parsedScale,
                new[] { "scale", "localScale", "scaleMultiplier", "localScaleMultiplier", "spawnScale" },
                new[] { "scaleX", "localScaleX", "scaleMultiplierX", "sx" },
                new[] { "scaleY", "localScaleY", "scaleMultiplierY", "sy" },
                new[] { "scaleZ", "localScaleZ", "scaleMultiplierZ", "sz" }))
        {
            scale = parsedScale;
            hasAnyValue = true;
        }

        return hasAnyValue;
    }

    private static bool TryReadVector3(
        List<string> row,
        Dictionary<string, int> headerMap,
        Vector3 fallback,
        bool allowUniformSingleValue,
        out Vector3 value,
        string[] vectorAliases,
        string[] xAliases,
        string[] yAliases,
        string[] zAliases)
    {
        value = fallback;

        foreach (string alias in vectorAliases)
        {
            if (TryGetRawField(row, headerMap, alias, out string rawValue) &&
                TryParseVector3(rawValue, allowUniformSingleValue, out Vector3 parsedVector))
            {
                value = parsedVector;
                return true;
            }
        }

        bool hasAnyComponent = false;
        if (TryReadFloat(row, headerMap, xAliases, out float x))
        {
            value.x = x;
            hasAnyComponent = true;
        }

        if (TryReadFloat(row, headerMap, yAliases, out float y))
        {
            value.y = y;
            hasAnyComponent = true;
        }

        if (TryReadFloat(row, headerMap, zAliases, out float z))
        {
            value.z = z;
            hasAnyComponent = true;
        }

        return hasAnyComponent;
    }

    private static bool RowMatchesKey(List<string> row, Dictionary<string, int> headerMap, string rowKey)
    {
        string[] keyAliases =
        {
            "key", "id", "name", "marker", "markerName", "trackedImage", "trackedImageName",
            "image", "imageName", "object", "objectName", "prefab", "prefabName"
        };

        foreach (string alias in keyAliases)
        {
            if (TryGetRawField(row, headerMap, alias, out string rawValue) && ValuesMatch(rawValue, rowKey))
                return true;
        }

        return row.Count > 0 && ValuesMatch(row[0], rowKey);
    }

    private static bool TryReadFloat(List<string> row, Dictionary<string, int> headerMap, string[] aliases, out float value)
    {
        foreach (string alias in aliases)
        {
            if (TryGetRawField(row, headerMap, alias, out string rawValue) && TryParseFloat(rawValue, out value))
                return true;
        }

        value = 0f;
        return false;
    }

    private static bool TryGetRawField(List<string> row, Dictionary<string, int> headerMap, string alias, out string value)
    {
        value = "";
        if (!headerMap.TryGetValue(NormalizeHeader(alias), out int index))
            return false;

        if (index < 0 || index >= row.Count)
            return false;

        value = row[index].Trim();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryParseVector3(string rawValue, bool allowUniformSingleValue, out Vector3 value)
    {
        value = Vector3.zero;
        string normalized = rawValue
            .Replace(';', ' ')
            .Replace('|', ' ')
            .Replace(',', ' ');
        string[] parts = normalized.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 1 && allowUniformSingleValue && TryParseFloat(parts[0], out float uniformValue))
        {
            value = Vector3.one * uniformValue;
            return true;
        }

        if (parts.Length < 3)
            return false;

        if (!TryParseFloat(parts[0], out float x) ||
            !TryParseFloat(parts[1], out float y) ||
            !TryParseFloat(parts[2], out float z))
        {
            return false;
        }

        value = new Vector3(x, y, z);
        return true;
    }

    private static bool TryParseFloat(string rawValue, out float value)
    {
        return float.TryParse(
            rawValue.Trim().Replace(',', '.'),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool ValuesMatch(string value, string expected)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(expected))
            return false;

        string trimmedValue = value.Trim();
        string trimmedExpected = expected.Trim();
        return string.Equals(trimmedValue, trimmedExpected, System.StringComparison.OrdinalIgnoreCase) ||
               string.Equals(NormalizeHeader(trimmedValue), NormalizeHeader(trimmedExpected), System.StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildHeaderMap(List<string> headerRow)
    {
        Dictionary<string, int> headerMap = new Dictionary<string, int>();
        for (int i = 0; i < headerRow.Count; i++)
        {
            string normalizedHeader = NormalizeHeader(headerRow[i]);
            if (!string.IsNullOrEmpty(normalizedHeader) && !headerMap.ContainsKey(normalizedHeader))
                headerMap.Add(normalizedHeader, i);
        }

        return headerMap;
    }

    private static string NormalizeHeader(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = char.ToLowerInvariant(value[i]);
            if (char.IsLetterOrDigit(c))
                builder.Append(c);
        }

        return builder.ToString();
    }

    private static string BuildTransformSettingsUrl(string url)
    {
        string trimmedUrl = url.Trim();
        if (trimmedUrl.IndexOf("docs.google.com/spreadsheets", System.StringComparison.OrdinalIgnoreCase) < 0)
            return trimmedUrl;

        if (trimmedUrl.IndexOf("output=csv", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmedUrl.IndexOf("format=csv", System.StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return trimmedUrl;
        }

        int publishedHtmlIndex = trimmedUrl.IndexOf("/pubhtml", System.StringComparison.OrdinalIgnoreCase);
        if (publishedHtmlIndex >= 0)
        {
            string baseUrl = trimmedUrl.Substring(0, publishedHtmlIndex);
            string gid = ExtractGoogleSheetGid(trimmedUrl);
            return $"{baseUrl}/pub?output=csv&gid={gid}";
        }

        int editIndex = trimmedUrl.IndexOf("/edit", System.StringComparison.OrdinalIgnoreCase);
        if (editIndex < 0)
            return trimmedUrl;

        string exportBaseUrl = trimmedUrl.Substring(0, editIndex);
        string exportGid = ExtractGoogleSheetGid(trimmedUrl);
        return $"{exportBaseUrl}/export?format=csv&gid={exportGid}";
    }

    private static string ExtractGoogleSheetGid(string url)
    {
        int gidIndex = url.IndexOf("gid=", System.StringComparison.OrdinalIgnoreCase);
        if (gidIndex < 0)
            return "0";

        gidIndex += 4;
        StringBuilder builder = new StringBuilder();
        while (gidIndex < url.Length && char.IsDigit(url[gidIndex]))
        {
            builder.Append(url[gidIndex]);
            gidIndex++;
        }

        return builder.Length > 0 ? builder.ToString() : "0";
    }

    private static List<List<string>> ParseDelimitedText(string text)
    {
        List<List<string>> rows = new List<List<string>>();
        if (string.IsNullOrEmpty(text))
            return rows;

        char delimiter = DetectDelimiter(text);
        List<string> row = new List<string>();
        StringBuilder field = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                row.Add(field.ToString());
                field.Length = 0;
            }
            else if ((c == '\n' || c == '\r') && !inQuotes)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                AddParsedRow(rows, row, field);
            }
            else
            {
                field.Append(c);
            }
        }

        AddParsedRow(rows, row, field);
        return rows;
    }

    private static void AddParsedRow(List<List<string>> rows, List<string> row, StringBuilder field)
    {
        row.Add(field.ToString());
        field.Length = 0;

        bool hasValue = false;
        for (int i = 0; i < row.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(row[i]))
            {
                hasValue = true;
                break;
            }
        }

        if (hasValue || row.Count > 1)
            rows.Add(new List<string>(row));

        row.Clear();
    }

    private static char DetectDelimiter(string text)
    {
        int lineEnd = text.IndexOfAny(new[] { '\r', '\n' });
        string firstLine = lineEnd >= 0 ? text.Substring(0, lineEnd) : text;
        int commaCount = CountCharacter(firstLine, ',');
        int semicolonCount = CountCharacter(firstLine, ';');
        int tabCount = CountCharacter(firstLine, '\t');

        if (tabCount > commaCount && tabCount > semicolonCount)
            return '\t';

        return semicolonCount > commaCount ? ';' : ',';
    }

    private static int CountCharacter(string value, char target)
    {
        int count = 0;
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == target)
                count++;
        }

        return count;
    }

#if UNITY_ANDROID
    private IEnumerator RequestCameraPermission()
    {
        if (Permission.HasUserAuthorizedPermission(Permission.Camera))
            yield break;

        bool responded = false;
        PermissionCallbacks callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => responded = true;
        callbacks.PermissionDenied += _ => responded = true;

        Permission.RequestUserPermission(Permission.Camera, callbacks);
        while (!responded)
            yield return null;
    }
#endif

    private void BuildARRig()
    {
        arSessionObject = new GameObject("AR Session");
        arSessionObject.SetActive(false);
        arSession = arSessionObject.AddComponent<ARSession>();
        arInputManager = arSessionObject.AddComponent<ARInputManager>();
        arSession.attemptUpdate = true;
        arSession.matchFrameRateRequested = false;
        arSession.requestedTrackingMode = TrackingMode.PositionAndRotation;

        xrOriginObject = new GameObject("XR Origin");
        xrOriginObject.SetActive(false);
        xrOrigin = xrOriginObject.AddComponent<XROrigin>();
        trackedImageManager = xrOriginObject.AddComponent<ARTrackedImageManager>();
        trackedImageManager.enabled = false;
        arAnchorManager = xrOriginObject.AddComponent<ARAnchorManager>();

        GameObject cameraOffset = new GameObject("Camera Offset");
        cameraOffset.transform.SetParent(xrOriginObject.transform, false);

        arCameraObject = new GameObject("AR Camera");
        arCameraObject.transform.SetParent(cameraOffset.transform, false);

        arCamera = arCameraObject.AddComponent<Camera>();
        arCamera.clearFlags = CameraClearFlags.SolidColor;
        arCamera.backgroundColor = Color.black;
        ApplyArCameraClipping();
        arCamera.tag = "MainCamera";

        UniversalAdditionalCameraData urpData = arCameraObject.GetComponent<UniversalAdditionalCameraData>();
        if (urpData == null)
            urpData = arCameraObject.AddComponent<UniversalAdditionalCameraData>();
        urpData.renderPostProcessing = enablePostProcessing;

        arCameraObject.AddComponent<AudioListener>();
        arCameraManager = arCameraObject.AddComponent<ARCameraManager>();
        arCameraManager.autoFocusRequested = true;
        arCameraObject.AddComponent<ARCameraBackground>();

        TrackedPoseDriver trackedPoseDriver = arCameraObject.AddComponent<TrackedPoseDriver>();
        trackedPoseDriver.trackingType = TrackedPoseDriver.TrackingType.RotationAndPosition;
        trackedPoseDriver.updateType = TrackedPoseDriver.UpdateType.UpdateAndBeforeRender;
        trackedPoseDriver.ignoreTrackingState = true;
        trackedPoseDriver.positionInput = new InputActionProperty(CreatePositionAction());
        trackedPoseDriver.rotationInput = new InputActionProperty(CreateRotationAction());
        trackedPoseDriver.trackingStateInput = new InputActionProperty(CreateTrackingStateAction());

        arCameraPoseFallback = arCameraObject.AddComponent<ARCameraPoseFallback>();

        xrOrigin.CameraFloorOffsetObject = cameraOffset;
        xrOrigin.Camera = arCamera;
    }

    private void ApplyArCameraClipping()
    {
        if (arCamera == null)
            return;

        float nearClip = Mathf.Max(0.001f, arCameraNearClipPlane);
        float farClip = Mathf.Max(nearClip + 0.01f, arCameraFarClipPlane);
        arCamera.nearClipPlane = nearClip;
        arCamera.farClipPlane = farClip;
    }

    private void OnTrackedImagesChanged(ARTrackablesChangedEventArgs<ARTrackedImage> eventArgs)
    {
        if (contentLockedToWorld)
            return;

        foreach (ARTrackedImage trackedImage in eventArgs.added)
        {
            UpdateTrackedImageContent(trackedImage, true);
            if (contentLockedToWorld)
                return;
        }

        foreach (ARTrackedImage trackedImage in eventArgs.updated)
        {
            UpdateTrackedImageContent(trackedImage, false);
            if (contentLockedToWorld)
                return;
        }

        for (int i = 0; i < eventArgs.removed.Count; i++)
        {
            KeyValuePair<TrackableId, ARTrackedImage> removedPair = eventArgs.removed[i];
            if (spawnedContent.TryGetValue(removedPair.Key, out GameObject content) && content != null)
                Destroy(content);

            spawnedContent.Remove(removedPair.Key);
        }
    }

    private void UpdateTrackedImageContent(ARTrackedImage trackedImage, bool invokeMarkerEvent)
    {
        if (!MatchesTrackedImage(trackedImage))
            return;

        bool isTracked = trackedImage.trackingState == TrackingState.Tracking || trackedImage.trackingState == TrackingState.Limited;
        if (!spawnedContent.TryGetValue(trackedImage.trackableId, out GameObject content) || content == null)
        {
            content = CreatePlacementContentInstance(trackedImage.referenceImage.name);
            spawnedContent[trackedImage.trackableId] = content;
            if (invokeMarkerEvent)
                onMarkerFound?.Invoke();
        }

        content.transform.SetParent(trackedImage.transform, false);
        content.transform.localPosition = localPositionOffset;
        content.transform.localRotation = Quaternion.Euler(localEulerOffset);
        ApplyContentScale(content.transform, trackedImage.transform);
        content.SetActive(!hideContentWhenMarkerNotTracked || isTracked);

        if (lockContentAfterFirstMarkerFound && isTracked)
            TryLockContentToWorld(trackedImage.trackableId, content);
        else if (!isTracked && pendingLockTrackableId == trackedImage.trackableId)
            ResetPendingLock();
    }

    private bool MatchesTrackedImage(ARTrackedImage trackedImage)
    {
        if (trackedImage == null)
            return false;

        if (string.IsNullOrWhiteSpace(trackedImageNameFilter))
            return true;

        return string.Equals(trackedImage.referenceImage.name, trackedImageNameFilter, System.StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyContentScale(Transform contentTransform, Transform trackedImageTransform)
    {
        Vector3 desiredWorldScale;
        if (placementPrefab == null)
        {
            desiredWorldScale = Vector3.Scale(Vector3.one * fallbackCubeSizeMeters, localScaleMultiplier);
        }
        else
        {
            desiredWorldScale = Vector3.Scale(placementPrefab.transform.localScale, localScaleMultiplier);
        }

        if (!useFixedWorldScale || trackedImageTransform == null)
        {
            contentTransform.localScale = desiredWorldScale;
            return;
        }

        Vector3 parentLossyScale = trackedImageTransform.lossyScale;
        contentTransform.localScale = new Vector3(
            SafeDivide(desiredWorldScale.x, parentLossyScale.x),
            SafeDivide(desiredWorldScale.y, parentLossyScale.y),
            SafeDivide(desiredWorldScale.z, parentLossyScale.z));
    }

    private GameObject CreatePlacementContentInstance(string trackedImageName)
    {
        GameObject container = new GameObject(string.IsNullOrWhiteSpace(trackedImageName)
            ? "AR Marker Content"
            : $"AR Marker Content ({trackedImageName})");

        GameObject visual = placementPrefab != null
            ? Instantiate(placementPrefab, container.transform, false)
            : GameObject.CreatePrimitive(PrimitiveType.Cube);

        visual.name = placementPrefab != null ? placementPrefab.name : "Fallback Cube";
        visual.transform.SetParent(container.transform, false);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;
        visual.transform.localScale = Vector3.one;

        if (placementPrefab == null)
        {
            Renderer renderer = visual.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = CreateUnlitMaterial(new Color(0.15f, 1f, 0.45f), false);
        }

        DisableContentCameraComponents(container);
        return container;
    }

    private void DisableContentCameraComponents(GameObject contentRoot)
    {
        if (contentRoot == null)
            return;

        Camera[] contentCameras = contentRoot.GetComponentsInChildren<Camera>(true);
        foreach (Camera contentCamera in contentCameras)
        {
            if (contentCamera != null)
                contentCamera.enabled = false;
        }

        AudioListener[] audioListeners = contentRoot.GetComponentsInChildren<AudioListener>(true);
        foreach (AudioListener audioListener in audioListeners)
        {
            if (audioListener != null)
                audioListener.enabled = false;
        }

        Behaviour[] behaviours = contentRoot.GetComponentsInChildren<Behaviour>(true);
        foreach (Behaviour behaviour in behaviours)
        {
            if (behaviour == null)
                continue;

            string typeName = behaviour.GetType().FullName ?? behaviour.GetType().Name;
            if (typeName.IndexOf("FreeCamera", System.StringComparison.OrdinalIgnoreCase) >= 0)
                behaviour.enabled = false;
        }
    }

    private void UpdateSpawnedContentFacing()
    {
        Quaternion targetRotation = BuildFacingRotation();
        foreach (GameObject content in spawnedContent.Values)
        {
            if (content != null && content.activeInHierarchy)
                content.transform.rotation = targetRotation * Quaternion.Euler(localEulerOffset);
        }
    }

    private void LockContentToWorld(GameObject content)
    {
        if (content == null || contentLockedToWorld)
            return;

        CaptureLockedReferencePose(content.transform.parent);
        Pose anchorPose = new Pose(lockedReferencePosition, lockedReferenceRotation);
        contentLockedToWorld = true;
        ResetPendingLock();
        content.transform.SetParent(null, true);
        content.SetActive(true);
        lockedContentObject = content;

        if (trackedImageManager != null)
            trackedImageManager.enabled = false;

        SyncAdjustmentUiFromContent(content);
        SetAdjustmentUiVisible(showAdjustmentUiAfterLock);
        CreateLockedContentAnchorAsync(content, anchorPose);

        Debug.Log("[AR Marker] Content locked in world space after first marker detection.", content);
    }

    private async void CreateLockedContentAnchorAsync(GameObject content, Pose anchorPose)
    {
        if (content == null || arAnchorManager == null)
        {
            Debug.LogWarning("[AR Marker] ARAnchorManager is not available. Content remains locked in Unity world space.");
            return;
        }

        // Subsystem may not be ready yet at the moment of first marker detection — wait up to 5 s
        float waitedSeconds = 0f;
        while (waitedSeconds < 5f)
        {
            if (arAnchorManager.enabled && arAnchorManager.subsystem != null && arAnchorManager.subsystem.running)
                break;
            await System.Threading.Tasks.Task.Delay(200);
            waitedSeconds += 0.2f;
            if (content == null || lockedContentObject != content)
                return;
        }

        if (!arAnchorManager.enabled || arAnchorManager.subsystem == null || !arAnchorManager.subsystem.running)
        {
            Debug.LogWarning($"[AR Marker] ARAnchorManager subsystem not ready after {waitedSeconds:0.0}s. Content remains locked in Unity world space.");
            return;
        }

        Result<ARAnchor> anchorResult;
        try
        {
            anchorResult = await arAnchorManager.TryAddAnchorAsync(anchorPose);
        }
        catch (System.Exception exception)
        {
            Debug.LogWarning($"[AR Marker] Could not create AR anchor. Content remains locked in Unity world space. {exception.Message}");
            return;
        }

        if (content == null || lockedContentObject != content)
            return;

        if (!anchorResult.status.IsSuccess() || anchorResult.value == null)
        {
            Debug.LogWarning($"[AR Marker] Could not create AR anchor. Content remains locked in Unity world space. Status = {anchorResult.status}");
            return;
        }

        lockedContentAnchor = anchorResult.value;
        lockedContentAnchor.name = "Locked AR Marker Anchor";
        content.transform.SetParent(lockedContentAnchor.transform, true);
        UpdateLockedReferencePoseFromAnchor();
        ApplyLockedContentFromInspectorValues();
        Debug.Log("[AR Marker] Content attached to AR anchor.", lockedContentAnchor);
    }

    private void SpawnContentWithoutMarker()
    {
        if (lockedContentObject != null)
        {
            lockedContentObject.SetActive(true);
            SetAdjustmentUiVisible(showAdjustmentUiAfterLock);
            return;
        }

        Transform cameraTransform = arCamera != null ? arCamera.transform : null;
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        if (cameraTransform == null)
        {
            Debug.LogWarning("[AR Marker] Cannot spawn without marker because no AR camera is available yet.");
            return;
        }

        DestroySpawnedContent();
        ResetPendingLock();

        lockedReferencePosition = cameraTransform.position + cameraTransform.forward * Mathf.Max(0.05f, markerlessSpawnDistance);
        lockedReferenceRotation = Quaternion.LookRotation(cameraTransform.forward, Vector3.up);
        lockedReferenceLossyScale = Vector3.one;
        hasLockedReferencePose = true;
        contentLockedToWorld = true;

        if (trackedImageManager != null)
            trackedImageManager.enabled = false;

        GameObject content = CreatePlacementContentInstance("Code Launch");
        content.transform.position = TransformReferencePoint(localPositionOffset);
        content.transform.rotation = lockedReferenceRotation * Quaternion.Euler(localEulerOffset);
        content.transform.localScale = GetLockedLocalScale(null);
        content.SetActive(true);
        lockedContentObject = content;

        SyncAdjustmentUiFromContent(content);
        SetAdjustmentUiVisible(showAdjustmentUiAfterLock);

        Debug.Log("[AR Marker] Content spawned without marker — world space, no anchor.", content);
    }

    private void TryLockContentToWorld(TrackableId trackableId, GameObject content)
    {
        if (content == null || contentLockedToWorld)
            return;

        if (lockDelaySeconds <= 0f)
        {
            LockContentToWorld(content);
            return;
        }

        if (pendingLockTrackableId != trackableId || pendingLockContent != content)
        {
            pendingLockTrackableId = trackableId;
            pendingLockContent = content;
            pendingLockElapsed = 0f;
        }
    }

    private void UpdatePendingWorldLock()
    {
        if (contentLockedToWorld || pendingLockContent == null)
            return;

        if (lockDelaySeconds <= 0f)
        {
            LockContentToWorld(pendingLockContent);
            return;
        }

        pendingLockElapsed += Time.unscaledDeltaTime;
        if (pendingLockElapsed >= lockDelaySeconds)
            LockContentToWorld(pendingLockContent);
    }

    private void ResetPendingLock()
    {
        pendingLockTrackableId = default;
        pendingLockContent = null;
        pendingLockElapsed = 0f;
    }

    private Quaternion BuildFacingRotation()
    {
        if (xrOrigin == null || xrOrigin.Camera == null)
            return Quaternion.identity;

        Vector3 forward = xrOrigin.Camera.transform.forward;
        forward.y = 0f;
        return forward.sqrMagnitude < 0.001f
            ? Quaternion.identity
            : Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    private void DestroySpawnedContent()
    {
        GameObject lockedObject = lockedContentObject;

        foreach (GameObject content in spawnedContent.Values)
        {
            if (content != null)
                Destroy(content);
        }

        if (lockedObject != null && !spawnedContent.ContainsValue(lockedObject))
            Destroy(lockedObject);

        spawnedContent.Clear();
        lockedContentObject = null;
        DestroyLockedAnchor();
    }

    private void DestroyLockedAnchor()
    {
        if (lockedContentAnchor == null)
            return;

        if (arAnchorManager != null && arAnchorManager.enabled && arAnchorManager.subsystem != null)
        {
            try
            {
                if (!arAnchorManager.TryRemoveAnchor(lockedContentAnchor))
                    Destroy(lockedContentAnchor.gameObject);
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[AR Marker] Could not remove AR anchor through ARAnchorManager. {exception.Message}");
                Destroy(lockedContentAnchor.gameObject);
            }
        }
        else
        {
            Destroy(lockedContentAnchor.gameObject);
        }

        lockedContentAnchor = null;
    }

    private void BuildAdjustmentUi()
    {
        if (!enableAdjustmentUi || adjustmentCanvasObject != null)
            return;

        runtimeUiFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? Resources.GetBuiltinResource<Font>("Arial.ttf");
        runtimeUiSprite = CreateFallbackUiSprite();

        if (FindFirstObjectByType<EventSystem>() == null)
        {
            GameObject eventSystemObject = new GameObject("Runtime EventSystem");
            eventSystemObject.AddComponent<EventSystem>();
            eventSystemObject.AddComponent<InputSystemUIInputModule>();
        }

        adjustmentCanvasObject = new GameObject("AR Adjustment Canvas");
        Canvas canvas = adjustmentCanvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        adjustmentCanvasObject.AddComponent<GraphicRaycaster>();

        CanvasScaler scaler = adjustmentCanvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);

        adjustmentPanelObject = new GameObject("Adjustment Panel");
        adjustmentPanelObject.transform.SetParent(adjustmentCanvasObject.transform, false);

        RectTransform panelRect = adjustmentPanelObject.AddComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0f, 0f);
        panelRect.anchorMax = new Vector2(1f, 0f);
        panelRect.pivot = new Vector2(0.5f, 0f);
        panelRect.offsetMin = new Vector2(24f, 24f);
        panelRect.offsetMax = new Vector2(-24f, 720f);

        Image panelImage = adjustmentPanelObject.AddComponent<Image>();
        panelImage.sprite = runtimeUiSprite;
        panelImage.type = Image.Type.Sliced;
        panelImage.color = new Color(0f, 0f, 0f, 0.78f);

        VerticalLayoutGroup layout = adjustmentPanelObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(18, 18, 18, 18);
        layout.spacing = 6f;
        layout.childControlWidth = true;
        layout.childControlHeight = false;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        GameObject contentRowObject = new GameObject("Adjustment Content Row");
        contentRowObject.transform.SetParent(adjustmentPanelObject.transform, false);
        LayoutElement contentRowLayout = contentRowObject.AddComponent<LayoutElement>();
        contentRowLayout.flexibleHeight = 1f;
        contentRowLayout.preferredHeight = 620f;

        HorizontalLayoutGroup contentRowGroup = contentRowObject.AddComponent<HorizontalLayoutGroup>();
        contentRowGroup.spacing = 18f;
        contentRowGroup.childControlWidth = true;
        contentRowGroup.childControlHeight = true;
        contentRowGroup.childForceExpandWidth = true;
        contentRowGroup.childForceExpandHeight = true;

        GameObject controlsColumnObject = CreateAdjustmentColumn(contentRowObject.transform, "Transform Controls", 1.2f);
        GameObject infoColumnObject = CreateAdjustmentColumn(contentRowObject.transform, "Transform Info", 0.8f);

        CreateHeaderText(controlsColumnObject.transform, "Transform Inspector");

        GameObject fieldsGridObject = new GameObject("Transform Fields Grid");
        fieldsGridObject.transform.SetParent(controlsColumnObject.transform, false);
        LayoutElement fieldsGridLayout = fieldsGridObject.AddComponent<LayoutElement>();
        fieldsGridLayout.preferredHeight = 190f;

        HorizontalLayoutGroup fieldsGridGroup = fieldsGridObject.AddComponent<HorizontalLayoutGroup>();
        fieldsGridGroup.spacing = 12f;
        fieldsGridGroup.childControlWidth = true;
        fieldsGridGroup.childControlHeight = true;
        fieldsGridGroup.childForceExpandWidth = true;
        fieldsGridGroup.childForceExpandHeight = true;

        GameObject transformLeftColumn = CreateAdjustmentColumn(fieldsGridObject.transform, "Scale And Position", 1f);
        GameObject transformRightColumn = CreateAdjustmentColumn(fieldsGridObject.transform, "Rotation", 1f);

        scaleControl = CreateValueFieldControl(transformLeftColumn.transform, "Scale");
        positionXControl = CreateValueFieldControl(transformLeftColumn.transform, "Pos X");
        positionYControl = CreateValueFieldControl(transformLeftColumn.transform, "Pos Y");
        positionZControl = CreateValueFieldControl(transformLeftColumn.transform, "Pos Z");
        rotationXControl = CreateValueFieldControl(transformRightColumn.transform, "Rot X");
        rotationYControl = CreateValueFieldControl(transformRightColumn.transform, "Rot Y");
        rotationZControl = CreateValueFieldControl(transformRightColumn.transform, "Rot Z");

        CreateHeaderText(infoColumnObject.transform, "Object Transform");
        recommendedSpawnValuesText = CreateMultilineValueText(infoColumnObject.transform, "Waiting for marker lock...");

        SetAdjustmentUiVisible(false);
    }

    private void ApplyAdjustmentUiToLockedContent()
    {
        if (lockedContentObject == null || adjustmentPanelObject == null || !adjustmentPanelObject.activeInHierarchy || suppressAdjustmentUiCallbacks)
            return;

        Vector3 newPositionOffset = new Vector3(
            ParseControlValue(positionXControl, localPositionOffset.x),
            ParseControlValue(positionYControl, localPositionOffset.y),
            ParseControlValue(positionZControl, localPositionOffset.z));
        Vector3 newEulerOffset = new Vector3(
            ParseControlValue(rotationXControl, localEulerOffset.x),
            ParseControlValue(rotationYControl, localEulerOffset.y),
            ParseControlValue(rotationZControl, localEulerOffset.z));
        float uniformScale = Mathf.Clamp(
            ParseControlValue(scaleControl, GetUniformScaleValue(localScaleMultiplier)),
            scaleAdjustmentMin,
            scaleAdjustmentMax);
        Vector3 newScaleMultiplier = Vector3.one * uniformScale;

        bool valuesChanged = !Vector3ApproxEqual(newPositionOffset, localPositionOffset)
            || !Vector3ApproxEqual(newEulerOffset, localEulerOffset)
            || !Vector3ApproxEqual(newScaleMultiplier, localScaleMultiplier);

        localPositionOffset = newPositionOffset;
        localEulerOffset = newEulerOffset;
        localScaleMultiplier = newScaleMultiplier;

        // Only overwrite the AR transform when the user actually changed a value.
        // Calling ApplyLockedContentFromInspectorValues every frame interferes with the
        // ARAnchor subsystem: it resets localPosition/world-position each frame before AR
        // Foundation can apply its own correction, causing the object to follow the camera.
        if (valuesChanged)
            ApplyLockedContentFromInspectorValues();

        UpdateRecommendedSpawnValues();
    }

    private void SyncAdjustmentUiFromContent(GameObject content)
    {
        if (content == null || adjustmentPanelObject == null)
            return;

        suppressAdjustmentUiCallbacks = true;

        SetControlText(positionXControl, localPositionOffset.x);
        SetControlText(positionYControl, localPositionOffset.y);
        SetControlText(positionZControl, localPositionOffset.z);
        SetControlText(rotationXControl, NormalizeAngle(localEulerOffset.x));
        SetControlText(rotationYControl, NormalizeAngle(localEulerOffset.y));
        SetControlText(rotationZControl, NormalizeAngle(localEulerOffset.z));
        SetControlText(scaleControl, GetUniformScaleValue(localScaleMultiplier));

        UpdateRecommendedSpawnValues();
        suppressAdjustmentUiCallbacks = false;
    }

    private void SetAdjustmentUiVisible(bool visible)
    {
        if (adjustmentPanelObject != null)
            adjustmentPanelObject.SetActive(visible);
    }

    private GameObject CreateAdjustmentColumn(Transform parent, string name, float flexibleWidth)
    {
        GameObject columnObject = new GameObject(name);
        columnObject.transform.SetParent(parent, false);

        LayoutElement layout = columnObject.AddComponent<LayoutElement>();
        layout.flexibleWidth = flexibleWidth;

        VerticalLayoutGroup columnGroup = columnObject.AddComponent<VerticalLayoutGroup>();
        columnGroup.spacing = 8f;
        columnGroup.childControlWidth = true;
        columnGroup.childControlHeight = false;
        columnGroup.childForceExpandWidth = true;
        columnGroup.childForceExpandHeight = false;

        return columnObject;
    }

    private RuntimeValueControl CreateValueFieldControl(Transform parent, string label)
    {
        GameObject rowObject = new GameObject(label + " Row");
        rowObject.transform.SetParent(parent, false);

        LayoutElement rowLayout = rowObject.AddComponent<LayoutElement>();
        rowLayout.preferredHeight = 38f;

        HorizontalLayoutGroup rowGroup = rowObject.AddComponent<HorizontalLayoutGroup>();
        rowGroup.spacing = 10f;
        rowGroup.childAlignment = TextAnchor.MiddleLeft;
        rowGroup.childControlWidth = false;
        rowGroup.childControlHeight = true;
        rowGroup.childForceExpandWidth = false;
        rowGroup.childForceExpandHeight = false;

        Text labelText = CreateLabelText(rowObject.transform, label, 58f);
        labelText.alignment = TextAnchor.MiddleLeft;

        GameObject inputObject = new GameObject(label + " Input");
        inputObject.transform.SetParent(rowObject.transform, false);
        LayoutElement inputLayout = inputObject.AddComponent<LayoutElement>();
        inputLayout.flexibleWidth = 1f;
        inputLayout.preferredHeight = 32f;

        Image background = inputObject.AddComponent<Image>();
        background.sprite = runtimeUiSprite;
        background.type = Image.Type.Sliced;
        background.color = new Color(0.18f, 0.18f, 0.18f, 0.95f);

        InputField inputField = inputObject.AddComponent<InputField>();
        inputField.lineType = InputField.LineType.SingleLine;
        inputField.contentType = InputField.ContentType.Standard;
        inputField.characterValidation = InputField.CharacterValidation.None;

        GameObject textObject = new GameObject("Text");
        textObject.transform.SetParent(inputObject.transform, false);
        Text inputText = textObject.AddComponent<Text>();
        inputText.font = runtimeUiFont;
        inputText.fontSize = 19;
        inputText.alignment = TextAnchor.MiddleLeft;
        inputText.color = Color.white;

        RectTransform textRect = textObject.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12f, 4f);
        textRect.offsetMax = new Vector2(-12f, -4f);

        GameObject placeholderObject = new GameObject("Placeholder");
        placeholderObject.transform.SetParent(inputObject.transform, false);
        Text placeholderText = placeholderObject.AddComponent<Text>();
        placeholderText.font = runtimeUiFont;
        placeholderText.fontSize = 19;
        placeholderText.alignment = TextAnchor.MiddleLeft;
        placeholderText.color = new Color(1f, 1f, 1f, 0.35f);
        placeholderText.text = "0";

        RectTransform placeholderRect = placeholderObject.GetComponent<RectTransform>();
        placeholderRect.anchorMin = Vector2.zero;
        placeholderRect.anchorMax = Vector2.one;
        placeholderRect.offsetMin = new Vector2(12f, 4f);
        placeholderRect.offsetMax = new Vector2(-12f, -4f);

        inputField.textComponent = inputText;
        inputField.placeholder = placeholderText;
        inputField.text = "0";

        return new RuntimeValueControl
        {
            InputField = inputField
        };
    }

    private Text CreateHeaderText(Transform parent, string text)
    {
        Text header = CreateLabelText(parent, text, -1f);
        header.fontSize = 26;
        header.alignment = TextAnchor.MiddleCenter;
        header.color = Color.white;
        LayoutElement layout = header.gameObject.AddComponent<LayoutElement>();
        layout.preferredHeight = 34f;
        return header;
    }

    private Text CreateMultilineValueText(Transform parent, string text)
    {
        Text valueText = CreateLabelText(parent, text, -1f);
        valueText.alignment = TextAnchor.UpperLeft;
        valueText.horizontalOverflow = HorizontalWrapMode.Wrap;
        valueText.verticalOverflow = VerticalWrapMode.Overflow;
        LayoutElement layout = valueText.GetComponent<LayoutElement>();
        layout.preferredHeight = 150f;
        return valueText;
    }

    private Text CreateLabelText(Transform parent, string text, float preferredWidth)
    {
        GameObject textObject = new GameObject(text + " Text");
        textObject.transform.SetParent(parent, false);
        Text uiText = textObject.AddComponent<Text>();
        uiText.font = runtimeUiFont;
        uiText.fontSize = 22;
        uiText.color = new Color(0.95f, 0.95f, 0.95f, 1f);
        uiText.text = text;
        uiText.horizontalOverflow = HorizontalWrapMode.Overflow;
        uiText.verticalOverflow = VerticalWrapMode.Overflow;

        LayoutElement layout = textObject.AddComponent<LayoutElement>();
        if (preferredWidth > 0f)
            layout.preferredWidth = preferredWidth;

        return uiText;
    }

    private void SetControlText(RuntimeValueControl control, float value)
    {
        if (control == null || control.InputField == null)
            return;

        control.InputField.text = value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static float ParseControlValue(RuntimeValueControl control, float fallbackValue)
    {
        if (control == null || control.InputField == null)
            return fallbackValue;

        string raw = control.InputField.text;
        if (string.IsNullOrWhiteSpace(raw))
            return fallbackValue;

        raw = raw.Trim().Replace(',', '.');
        if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedValue))
            return parsedValue;

        return fallbackValue;
    }

    private static float NormalizeAngle(float angle)
    {
        angle %= 360f;
        if (angle > 180f)
            angle -= 360f;
        return angle;
    }

    private void CaptureLockedReferencePose(Transform referenceTransform)
    {
        if (referenceTransform == null)
        {
            hasLockedReferencePose = false;
            lockedReferencePosition = Vector3.zero;
            lockedReferenceRotation = Quaternion.identity;
            lockedReferenceLossyScale = Vector3.one;
            return;
        }

        hasLockedReferencePose = true;
        lockedReferencePosition = referenceTransform.position;
        lockedReferenceRotation = referenceTransform.rotation;
        lockedReferenceLossyScale = referenceTransform.lossyScale;
    }

    private void UpdateLockedReferencePoseFromAnchor()
    {
        if (lockedContentAnchor == null)
            return;

        Transform anchorTransform = lockedContentAnchor.transform;
        hasLockedReferencePose = true;
        lockedReferencePosition = anchorTransform.position;
        lockedReferenceRotation = anchorTransform.rotation;
    }

    private void UpdateRecommendedSpawnValues()
    {
        if (recommendedSpawnValuesText == null)
            return;

        if (lockedContentObject == null || !hasLockedReferencePose)
        {
            recommendedSpawnValuesText.text = "Waiting for marker lock...";
            return;
        }

        Transform contentTransform = lockedContentObject.transform;
        Transform parentTransform = contentTransform.parent;
        Camera activeArCamera = xrOrigin != null ? xrOrigin.Camera : null;
        Camera mainCamera = Camera.main;
        recommendedSpawnValuesText.text =
            $"Object: {lockedContentObject.name}\n" +
            $"Parent: {(parentTransform != null ? parentTransform.name : "World")}\n" +
            $"Anchor: {(lockedContentAnchor != null ? "Active" : "Pending")}\n" +
            $"Content Pos: {FormatVector3(contentTransform.position)}\n" +
            $"Rotation: {FormatVector3(NormalizeEuler(contentTransform.eulerAngles))}\n" +
            $"Scale: {FormatVector3(contentTransform.localScale)}\n" +
            $"World Scale: {FormatVector3(contentTransform.lossyScale)}\n\n" +
            $"AR Camera: {(activeArCamera != null ? activeArCamera.name : "None")}\n" +
            $"Main Camera: {(mainCamera != null ? mainCamera.name : "None")}\n" +
            $"XR Input: {GetArInputStatus()}\n" +
            $"Pose Input: {(arCameraPoseFallback != null ? arCameraPoseFallback.DebugStatus : "None")}\n" +
            $"Camera Pos: {(activeArCamera != null ? FormatVector3(activeArCamera.transform.position) : "None")}\n\n" +
            $"Spawn Position: {FormatVector3(localPositionOffset)}\n" +
            $"Spawn Rotation: {FormatVector3(localEulerOffset)}\n" +
            $"Spawn Scale: {GetUniformScaleValue(localScaleMultiplier):0.###}";
    }

    private Vector3 GetBasePlacementScale()
    {
        return placementPrefab != null
            ? placementPrefab.transform.localScale
            : Vector3.one * fallbackCubeSizeMeters;
    }

    private void ApplyLockedContentFromInspectorValues()
    {
        if (lockedContentObject == null || !hasLockedReferencePose)
            return;

        UpdateLockedReferencePoseFromAnchor();

        Transform contentTransform = lockedContentObject.transform;
        if (lockedContentAnchor != null && contentTransform.parent == lockedContentAnchor.transform)
        {
            contentTransform.localPosition = GetLockedLocalPosition();
            contentTransform.localRotation = Quaternion.Euler(localEulerOffset);
            contentTransform.localScale = GetLockedLocalScale(contentTransform.parent);
            return;
        }

        contentTransform.position = TransformReferencePoint(localPositionOffset);
        contentTransform.rotation = lockedReferenceRotation * Quaternion.Euler(localEulerOffset);
        contentTransform.localScale = GetLockedLocalScale(contentTransform.parent);
    }

    private Vector3 TransformReferencePoint(Vector3 localPoint)
    {
        Vector3 scaledPoint = Vector3.Scale(localPoint, lockedReferenceLossyScale);
        return lockedReferencePosition + lockedReferenceRotation * scaledPoint;
    }

    private Vector3 GetPreviewWorldScale()
    {
        Vector3 baseScale = Vector3.Scale(GetBasePlacementScale(), localScaleMultiplier);
        return useFixedWorldScale
            ? baseScale
            : Vector3.Scale(baseScale, lockedReferenceLossyScale);
    }

    private Vector3 GetLockedLocalPosition()
    {
        return Vector3.Scale(localPositionOffset, lockedReferenceLossyScale);
    }

    private Vector3 GetLockedLocalScale(Transform parent)
    {
        Vector3 desiredWorldScale = GetPreviewWorldScale();
        if (parent == null)
            return desiredWorldScale;

        Vector3 parentLossyScale = parent.lossyScale;
        return new Vector3(
            SafeDivide(desiredWorldScale.x, parentLossyScale.x),
            SafeDivide(desiredWorldScale.y, parentLossyScale.y),
            SafeDivide(desiredWorldScale.z, parentLossyScale.z));
    }

    private static Vector3 DivideComponents(Vector3 value, Vector3 divisor)
    {
        return new Vector3(
            SafeDivide(value.x, divisor.x),
            SafeDivide(value.y, divisor.y),
            SafeDivide(value.z, divisor.z));
    }

    private static float GetUniformScaleValue(Vector3 value)
    {
        return (value.x + value.y + value.z) / 3f;
    }

    private static Vector3 NormalizeEuler(Vector3 eulerAngles)
    {
        return new Vector3(
            NormalizeAngle(eulerAngles.x),
            NormalizeAngle(eulerAngles.y),
            NormalizeAngle(eulerAngles.z));
    }

    private static string FormatVector3(Vector3 value)
    {
        return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
    }

    private void DisableSceneObjectsForAR()
    {
        if (sceneCameraToDisable == null)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.gameObject != arCameraObject)
                sceneCameraToDisable = mainCamera;
        }

        DisableNonArSceneCameras();
        HideCanvasGroupsForAR();

        if (objectsToDisableOnStart == null)
            return;

        foreach (GameObject go in objectsToDisableOnStart)
        {
            if (go != null && !ShouldKeepActive(go))
                go.SetActive(false);
        }
    }

    private void RefreshArInputManager()
    {
        if (arInputManager == null)
            return;

        if (arInputManager.subsystem != null && arInputManager.subsystem.running)
            return;

        arInputManager.enabled = false;
        arInputManager.enabled = true;
    }

    private string GetArInputStatus()
    {
        if (arInputManager == null)
            return "No Manager";

        if (arInputManager.subsystem == null)
            return "No Subsystem";

        return arInputManager.subsystem.running ? "Running" : "Stopped";
    }

    private void HideCanvasGroupsForAR()
    {
        if (canvasGroupsToHideOnStart == null)
            return;

        foreach (CanvasGroup group in canvasGroupsToHideOnStart)
        {
            if (group == null)
                continue;

            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }
    }

    private bool ShouldKeepActive(GameObject go)
    {
        if (go == null || objectsToKeepActiveOnStart == null)
            return false;

        foreach (GameObject keep in objectsToKeepActiveOnStart)
        {
            if (keep == null)
                continue;
            if (go == keep || go.transform.IsChildOf(keep.transform))
                return true;
        }

        return false;
    }

    private void DisableNonArSceneCameras()
    {
        Camera[] sceneCameras = FindObjectsByType<Camera>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (Camera sceneCamera in sceneCameras)
        {
            if (sceneCamera == null)
                continue;

            if (sceneCamera.gameObject == arCameraObject)
                continue;

            if (xrOriginObject != null && sceneCamera.transform.IsChildOf(xrOriginObject.transform))
                continue;

            if (ShouldKeepActive(sceneCamera.gameObject))
                continue;

            sceneCamera.gameObject.SetActive(false);
        }

        if (sceneCameraToDisable != null && sceneCameraToDisable.gameObject != arCameraObject
            && !ShouldKeepActive(sceneCameraToDisable.gameObject))
            sceneCameraToDisable.gameObject.SetActive(false);
    }

    private void ApplyPerformanceSettings()
    {
        if (disableVSync)
            QualitySettings.vSyncCount = 0;

        OnDemandRendering.renderFrameInterval = 1;

        int targetFrameRate = fallbackTargetFrameRate;
        if (useDisplayRefreshRate)
        {
            float displayRefreshRate = (float)Screen.currentResolution.refreshRateRatio.value;
            if (displayRefreshRate >= 1f)
                targetFrameRate = Mathf.RoundToInt(displayRefreshRate);
        }

        if (targetFrameRate < 60)
            targetFrameRate = 60;

        Application.targetFrameRate = targetFrameRate;
    }

    private static Material CreateUnlitMaterial(Color color, bool transparent)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null)
            shader = Shader.Find("Unlit/Color");
        if (shader == null)
            shader = Shader.Find("Sprites/Default");

        Material material = new Material(shader);
        ApplyColor(material, color);

        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", transparent ? 1f : 0f);
        if (material.HasProperty("_Blend"))
            material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_AlphaClip"))
            material.SetFloat("_AlphaClip", 0f);
        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", (float)CullMode.Off);
        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", transparent ? 0f : 1f);

        if (transparent)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)RenderQueue.Transparent;
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else
        {
            material.SetOverrideTag("RenderType", "Opaque");
            material.renderQueue = (int)RenderQueue.Geometry;
            material.SetInt("_SrcBlend", (int)BlendMode.One);
            material.SetInt("_DstBlend", (int)BlendMode.Zero);
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        return material;
    }

    private static void ApplyColor(Material material, Color color)
    {
        if (material == null)
            return;

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
    }

    private static Sprite CreateFallbackUiSprite()
    {
        Texture2D texture = Texture2D.whiteTexture;
        return Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
    }

    private static InputAction CreatePositionAction()
    {
        InputAction action = new InputAction("AR Camera Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
        action.AddBinding("<HandheldARInputDevice>/devicePosition");
        return action;
    }

    private static InputAction CreateRotationAction()
    {
        InputAction action = new InputAction("AR Camera Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
        action.AddBinding("<HandheldARInputDevice>/deviceRotation");
        return action;
    }

    private static InputAction CreateTrackingStateAction()
    {
        InputAction action = new InputAction("AR Camera Tracking State", binding: "<XRHMD>/trackingState", expectedControlType: "Integer");
        action.AddBinding("<HandheldARInputDevice>/trackingState");
        return action;
    }

    private static float SafeDivide(float value, float divisor)
    {
        return Mathf.Abs(divisor) < 0.0001f ? value : value / divisor;
    }

    private static bool Vector3ApproxEqual(Vector3 a, Vector3 b)
    {
        return Mathf.Approximately(a.x, b.x) && Mathf.Approximately(a.y, b.y) && Mathf.Approximately(a.z, b.z);
    }
}

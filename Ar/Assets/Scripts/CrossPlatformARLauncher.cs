using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class CrossPlatformARLauncher : MonoBehaviour
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
    }

    [System.Serializable]
    private sealed class JsonTransformSettingsDocument : JsonTransformSettingsRow
    {
        public JsonTransformSettingsRow[] rows;
        public JsonTransformSettingsRow[] items;
        public JsonTransformSettingsRow[] transforms;
        public JsonTransformSettingsRow[] settings;
    }

    [Header("Launch")]
    [SerializeField] private Button startArButton;
    [SerializeField] private Camera sceneCameraToDisable;
    [SerializeField] private GameObject[] objectsToDisableOnStart;

    [Header("Placement")]
    [SerializeField] private GameObject placementPrefab;
    [SerializeField] private float fallbackCubeSizeMeters = 0.15f;
    [SerializeField] private bool placeOnFirstDetectedPlane = true;
    [SerializeField] private bool keepUpdatingPositionUntilFound = true;
    [SerializeField] private bool requireHorizontalUpwardPlane = true;
    [SerializeField] private bool allowFeaturePointReticleFallback = true;
    [SerializeField, Range(0.2f, 0.8f)] private float raycastViewportY = 0.38f;
    [SerializeField] private float minimumPlacementDistanceMeters = 0.2f;
    [SerializeField] private float maximumPlacementDistanceMeters = 4f;
    [SerializeField] private int stablePlaneFramesRequired = 6;
    [SerializeField] private float placementRotationYOffset = 180f;
    [SerializeField] private bool keepPlacedObjectFacingCameraOnY = false;
    [SerializeField] private Vector3 localPositionOffset = Vector3.zero;
    [SerializeField] private Vector3 localEulerOffset = Vector3.zero;
    [SerializeField] private Vector3 localScaleMultiplier = Vector3.one;

    [Header("Web Transform Settings")]
    [SerializeField, InspectorName("Transform Settings Url")] private string transformSettingsCsvUrl = "";
    [SerializeField] private string transformSettingsRowKey = "";
    [SerializeField] private bool loadTransformSettingsOnStart = true;
    [SerializeField] private bool refreshTransformSettingsPeriodically;
    [SerializeField, Min(1)] private int transformSettingsRequestTimeoutSeconds = 10;
    [SerializeField, Min(5f)] private float transformSettingsRefreshSeconds = 30f;

    [Header("Plane Visual")]
    [SerializeField] private bool showDetectedPlanes = true;
    [SerializeField] private bool hideDetectedPlanesAfterPlacement = true;
    [SerializeField] private Color planeFillColor = new Color(0.1f, 0.9f, 0.55f, 0.22f);
    [SerializeField] private Color planeOutlineColor = new Color(0.15f, 1f, 0.65f, 0.95f);
    [SerializeField] private float planeOutlineWidth = 0.018f;

    [Header("Performance")]
    [SerializeField] private bool disableVSync = true;
    [SerializeField] private bool useDisplayRefreshRate = true;
    [SerializeField] private int fallbackTargetFrameRate = 120;

    [Header("Events")]
    [SerializeField] private UnityEvent onArStarted;
    [SerializeField] private UnityEvent onObjectPlaced;
    [SerializeField] private UnityEvent onArUnsupported;

    private readonly List<ARRaycastHit> raycastHits = new List<ARRaycastHit>();

    private GameObject arSessionObject;
    private GameObject xrOriginObject;
    private GameObject arCameraObject;
    private ARSession arSession;
    private XROrigin xrOrigin;
    private ARRaycastManager arRaycastManager;
    private ARPlaneManager arPlaneManager;
    private ARCameraManager arCameraManager;
    private GameObject placedObject;
    private Vector3 placedObjectBaseScale = Vector3.one;
    private bool hasLastPlacementPose;
    private Vector3 lastPlacementPosition;
    private Quaternion lastPlacementBaseRotation = Quaternion.identity;
    private GameObject planeVisualizationPrefab;
    private Material planeFillMaterial;
    private Material planeLineMaterial;
    private bool arStartRequested;
    private bool objectPlacementLocked;
    private bool didInvokeStarted;
    private int stablePlaneHitFrames;

    private GameObject reticleObject;
    private Material reticleMaterial;
    private float reticleAngle;

    private GameObject hintCanvasObject;
    private Text hintText;
    private RawImage centerRingImage;

    private float logTimer;
    private Coroutine transformSettingsRoutine;

    private void Awake()
    {
        ApplyPerformanceSettings();
        BuildPlaneVisualizationPrefab();
        BuildARRig();
        BuildReticle();
        Debug.Log($"[AR] Awake. Initial state = {ARSession.state}");
    }

    private void OnEnable()
    {
        if (startArButton != null)
            startArButton.onClick.AddListener(StartAR);

        if (loadTransformSettingsOnStart)
            StartTransformSettingsRoutine();
    }

    private void OnDisable()
    {
        if (startArButton != null)
            startArButton.onClick.RemoveListener(StartAR);

        StopTransformSettingsRoutine();
    }

    private void Update()
    {
        if (!arStartRequested || arRaycastManager == null)
            return;

        logTimer += Time.deltaTime;
        if (logTimer >= 1f)
        {
            logTimer = 0f;
            Debug.Log($"[AR] State={ARSession.state} planeFrames={stablePlaneHitFrames}");
        }

        if (!didInvokeStarted &&
            (ARSession.state == ARSessionState.SessionInitializing ||
             ARSession.state == ARSessionState.SessionTracking))
        {
            didInvokeStarted = true;
            onArStarted?.Invoke();
            if (hintCanvasObject != null)
                hintCanvasObject.SetActive(true);
        }

        if (ARSession.state == ARSessionState.Unsupported)
        {
            onArUnsupported?.Invoke();
            arStartRequested = false;
            stablePlaneHitFrames = 0;
            SetReticleVisible(false);
            if (hintCanvasObject != null)
                hintCanvasObject.SetActive(false);
            return;
        }

        if (objectPlacementLocked)
        {
            UpdatePlacedObjectFacing();
            SetReticleVisible(false);
            if (hintCanvasObject != null)
                hintCanvasObject.SetActive(false);
            return;
        }

        if (xrOrigin == null || xrOrigin.Camera == null)
            return;

        Vector2 scanPoint = new Vector2(Screen.width * 0.5f, Screen.height * raycastViewportY);
        bool hasPlane = TryGetBestPlaneHit(scanPoint, out Pose planePose);

        if (!hasPlane && allowFeaturePointReticleFallback &&
            arRaycastManager.Raycast(scanPoint, raycastHits, TrackableType.FeaturePoint))
        {
            stablePlaneHitFrames = 0;
            UpdateReticle(raycastHits[0].pose, false);
            SetCenterRingColor(new Color(1f, 0.8f, 0.1f, 0.9f));
            SetHintText("Ищу ровную поверхность...\nНаведите камеру чуть ниже");
            return;
        }

        if (!hasPlane)
        {
            stablePlaneHitFrames = 0;
            SetReticleVisible(false);
            SetCenterRingColor(new Color(1f, 1f, 1f, 0.6f));
            SetHintText("Медленно поводите камерой\nнад полом или столом");
            return;
        }

        UpdateReticle(planePose, true);

        if (stablePlaneHitFrames < Mathf.Max(1, stablePlaneFramesRequired))
        {
            SetCenterRingColor(new Color(1f, 0.85f, 0.2f, 0.95f));
            SetHintText("Фиксирую поверхность...");
            return;
        }

        SetCenterRingColor(new Color(0.2f, 1f, 0.5f, 0.95f));
        SetHintText("Поверхность найдена");
        PlaceOrMoveObject(planePose);
    }

    public void StartAR()
    {
        if (arStartRequested)
            return;

        arStartRequested = true;
        didInvokeStarted = false;
        stablePlaneHitFrames = 0;
        ApplyPerformanceSettings();
        RestorePlaneVisualization();
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
        if (!isActiveAndEnabled)
            return;

        StopTransformSettingsRoutine();
        transformSettingsRoutine = StartCoroutine(TransformSettingsRoutine(oneShot: true));
    }

    public void StopAR()
    {
        arStartRequested = false;
        stablePlaneHitFrames = 0;
        arSessionObject?.SetActive(false);
        xrOriginObject?.SetActive(false);
        SetReticleVisible(false);
        RestorePlaneVisualization();
        if (hintCanvasObject != null)
            hintCanvasObject.SetActive(false);
    }

    public void ResetPlacement()
    {
        objectPlacementLocked = false;
        stablePlaneHitFrames = 0;
        RestorePlaneVisualization();
        if (placedObject != null)
        {
            Destroy(placedObject);
            placedObject = null;
        }

        placedObjectBaseScale = Vector3.one;
        hasLastPlacementPose = false;
    }

    private IEnumerator StartARRoutine()
    {
#if UNITY_ANDROID
        yield return RequestCameraPermission();
        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Debug.LogWarning("[AR] Camera permission denied.");
            arStartRequested = false;
            yield break;
        }
#endif
        DisableSceneObjectsForAR();
        arSessionObject.SetActive(true);
        xrOriginObject.SetActive(true);
        if (hintCanvasObject != null)
            hintCanvasObject.SetActive(true);

        Debug.Log("[AR] Checking availability...");
        yield return ARSession.CheckAvailability();
        Debug.Log($"[AR] Availability state = {ARSession.state}");

        if (ARSession.state == ARSessionState.NeedsInstall)
        {
            Debug.Log("[AR] Installing XR support...");
            yield return ARSession.Install();
            Debug.Log($"[AR] State after install = {ARSession.state}");
        }

        if (ARSession.state == ARSessionState.Unsupported)
        {
            Debug.LogError("[AR] Device does not support AR.");
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
                Debug.LogWarning($"[AR] Session did not start in time. Current state = {ARSession.state}");
                break;
            }

            yield return null;
        }

        Debug.Log($"[AR] Runtime session state = {ARSession.state}");
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

            if (oneShot || !refreshTransformSettingsPeriodically)
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
                Debug.LogWarning($"[AR] Could not load transform settings from web. {request.error}", this);
                yield break;
            }

            if (TryApplyTransformSettingsPayload(request.downloadHandler.text, out string resultMessage))
                Debug.Log($"[AR] Transform settings loaded from web. {resultMessage}", this);
            else
                Debug.LogWarning($"[AR] Web transform settings were not applied. {resultMessage}", this);
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

        if (!TryReadTransformValues(row, out Vector3 position, out Vector3 rotation, out Vector3 scale))
        {
            resultMessage = "Selected JSON row does not contain transform values.";
            return false;
        }

        bool changed = position != localPositionOffset || rotation != localEulerOffset || scale != localScaleMultiplier;
        localPositionOffset = position;
        localEulerOffset = rotation;
        localScaleMultiplier = scale;

        if (changed)
            ApplyRuntimeTransformSettings();

        resultMessage = $"JSON: pos={FormatVector3(localPositionOffset)}, rot={FormatVector3(localEulerOffset)}, scale={FormatVector3(localScaleMultiplier)}.";
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

        if (!TryReadTransformValues(rows[selectedRowIndex], headerMap, out Vector3 position, out Vector3 rotation, out Vector3 scale))
        {
            resultMessage = $"Row {selectedRowIndex + 1} does not contain transform columns.";
            return false;
        }

        bool changed = position != localPositionOffset || rotation != localEulerOffset || scale != localScaleMultiplier;
        localPositionOffset = position;
        localEulerOffset = rotation;
        localScaleMultiplier = scale;

        if (changed)
            ApplyRuntimeTransformSettings();

        resultMessage = $"Row {selectedRowIndex + 1}: pos={FormatVector3(localPositionOffset)}, rot={FormatVector3(localEulerOffset)}, scale={FormatVector3(localScaleMultiplier)}.";
        return true;
    }

    private JsonTransformSettingsRow FindTransformSettingsRow(JsonTransformSettingsDocument document, string rowKey)
    {
        JsonTransformSettingsRow[] rows = document.rows ?? document.items ?? document.transforms ?? document.settings;
        if (rows != null && rows.Length > 0)
        {
            if (!string.IsNullOrWhiteSpace(rowKey))
            {
                for (int i = 0; i < rows.Length; i++)
                {
                    if (JsonRowMatchesKey(rows[i], rowKey))
                        return rows[i];
                }

                return null;
            }

            for (int i = 0; i < rows.Length; i++)
            {
                if (TryReadTransformValues(rows[i], out _, out _, out _))
                    return rows[i];
            }
        }

        if (!string.IsNullOrWhiteSpace(rowKey) && !JsonRowMatchesKey(document, rowKey))
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
        if (placedObject == null || !hasLastPlacementPose)
            return;

        ApplyPlacedObjectTransform(lastPlacementPosition, lastPlacementBaseRotation);
    }

    private int FindTransformSettingsRow(List<List<string>> rows, Dictionary<string, int> headerMap, string rowKey)
    {
        if (!string.IsNullOrWhiteSpace(rowKey))
        {
            for (int i = 1; i < rows.Count; i++)
            {
                if (RowMatchesKey(rows[i], headerMap, rowKey))
                    return i;
            }

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

    private static string FormatVector3(Vector3 value)
    {
        return $"({value.x:0.###}, {value.y:0.###}, {value.z:0.###})";
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
        arSessionObject.AddComponent<ARInputManager>();
        arSession.attemptUpdate = true;
        arSession.matchFrameRateRequested = false;

        xrOriginObject = new GameObject("XR Origin");
        xrOriginObject.SetActive(false);
        xrOrigin = xrOriginObject.AddComponent<XROrigin>();
        arRaycastManager = xrOriginObject.AddComponent<ARRaycastManager>();
        arPlaneManager = xrOriginObject.AddComponent<ARPlaneManager>();
        arPlaneManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
        arPlaneManager.planePrefab = showDetectedPlanes ? planeVisualizationPrefab : null;

        GameObject cameraOffset = new GameObject("Camera Offset");
        cameraOffset.transform.SetParent(xrOriginObject.transform, false);

        arCameraObject = new GameObject("AR Camera");
        arCameraObject.transform.SetParent(cameraOffset.transform, false);

        Camera arCamera = arCameraObject.AddComponent<Camera>();
        arCamera.clearFlags = CameraClearFlags.SolidColor;
        arCamera.backgroundColor = Color.black;
        arCamera.nearClipPlane = 0.05f;
        arCamera.farClipPlane = 20f;
        arCamera.tag = "MainCamera";

        UniversalAdditionalCameraData urpData = arCameraObject.GetComponent<UniversalAdditionalCameraData>();
        if (urpData == null)
            urpData = arCameraObject.AddComponent<UniversalAdditionalCameraData>();
        urpData.renderPostProcessing = false;

        arCameraObject.AddComponent<AudioListener>();
        arCameraManager = arCameraObject.AddComponent<ARCameraManager>();
        arCameraManager.autoFocusRequested = true;
        arCameraObject.AddComponent<ARCameraBackground>();

        TrackedPoseDriver trackedPoseDriver = arCameraObject.AddComponent<TrackedPoseDriver>();
        trackedPoseDriver.positionInput = new InputActionProperty(CreatePositionAction());
        trackedPoseDriver.rotationInput = new InputActionProperty(CreateRotationAction());
        trackedPoseDriver.trackingStateInput = new InputActionProperty(CreateTrackingStateAction());

        xrOrigin.CameraFloorOffsetObject = cameraOffset;
        xrOrigin.Camera = arCamera;
    }

    private void BuildReticle()
    {
        reticleObject = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        reticleObject.name = "AR Reticle";
        if (reticleObject.TryGetComponent<Collider>(out Collider collider))
            Destroy(collider);

        reticleObject.transform.localScale = new Vector3(0.25f, 0.002f, 0.25f);
        Renderer reticleRenderer = reticleObject.GetComponent<Renderer>();
        reticleMaterial = CreateUnlitMaterial(new Color(0.15f, 1f, 0.45f), false);
        reticleRenderer.sharedMaterial = reticleMaterial;
        ApplyColor(reticleMaterial, new Color(0.15f, 1f, 0.45f));
        reticleObject.SetActive(false);

        hintCanvasObject = new GameObject("AR Hint Canvas");
        Canvas canvas = hintCanvasObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 10;

        CanvasScaler scaler = hintCanvasObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);

        GameObject ringObject = new GameObject("Center Ring");
        ringObject.transform.SetParent(hintCanvasObject.transform, false);
        RectTransform ringRect = ringObject.AddComponent<RectTransform>();
        ringRect.anchorMin = ringRect.anchorMax = new Vector2(0.5f, 0.5f);
        ringRect.sizeDelta = new Vector2(110f, 110f);
        ringRect.anchoredPosition = Vector2.zero;
        centerRingImage = ringObject.AddComponent<RawImage>();
        centerRingImage.texture = CreateRingTexture(128, 0.7f);
        centerRingImage.color = new Color(1f, 1f, 1f, 0.7f);

        AddCenteredRect(hintCanvasObject.transform, "Dot", new Vector2(10f, 10f))
            .gameObject.AddComponent<Image>().color = new Color(1f, 1f, 1f, 0.9f);

        RectTransform bgRect = AddCenteredRect(hintCanvasObject.transform, "Hint BG", Vector2.zero);
        bgRect.anchorMin = Vector2.zero;
        bgRect.anchorMax = new Vector2(1f, 0.11f);
        bgRect.offsetMin = Vector2.zero;
        bgRect.offsetMax = Vector2.zero;
        bgRect.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.5f);

        RectTransform textRect = AddCenteredRect(hintCanvasObject.transform, "Hint Text", Vector2.zero);
        textRect.anchorMin = new Vector2(0.05f, 0f);
        textRect.anchorMax = new Vector2(0.95f, 0.11f);
        textRect.offsetMin = Vector2.zero;
        textRect.offsetMax = Vector2.zero;
        hintText = textRect.gameObject.AddComponent<Text>();
        hintText.text = "Медленно поводите камерой над горизонтальной поверхностью";
        hintText.alignment = TextAnchor.MiddleCenter;
        hintText.fontSize = 44;
        hintText.color = Color.white;
        hintText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf") ?? Resources.GetBuiltinResource<Font>("Arial.ttf");

        hintCanvasObject.SetActive(false);
    }

    private void BuildPlaneVisualizationPrefab()
    {
        planeVisualizationPrefab = new GameObject("AR Plane Visual");
        planeVisualizationPrefab.hideFlags = HideFlags.HideAndDontSave;

        ARPlane plane = planeVisualizationPrefab.AddComponent<ARPlane>();
        planeVisualizationPrefab.AddComponent<ARPlaneMeshVisualizer>();

        planeVisualizationPrefab.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = planeVisualizationPrefab.AddComponent<MeshRenderer>();
        LineRenderer lineRenderer = planeVisualizationPrefab.AddComponent<LineRenderer>();

        planeFillMaterial = CreateUnlitMaterial(planeFillColor, true);
        planeLineMaterial = CreateUnlitMaterial(planeOutlineColor, true);

        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        meshRenderer.sharedMaterial = planeFillMaterial;

        lineRenderer.loop = true;
        lineRenderer.useWorldSpace = false;
        lineRenderer.widthMultiplier = planeOutlineWidth;
        lineRenderer.positionCount = 0;
        lineRenderer.sharedMaterial = planeLineMaterial;
        lineRenderer.startColor = planeOutlineColor;
        lineRenderer.endColor = planeOutlineColor;
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;

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

    private bool TryGetBestPlaneHit(Vector2 screenPoint, out Pose pose)
    {
        pose = default;

        bool hitFound = arRaycastManager.Raycast(
            screenPoint,
            raycastHits,
            TrackableType.PlaneWithinPolygon | TrackableType.PlaneWithinBounds | TrackableType.PlaneWithinInfinity);

        if (!hitFound)
        {
            stablePlaneHitFrames = 0;
            return false;
        }

        for (int i = 0; i < raycastHits.Count; i++)
        {
            ARRaycastHit hit = raycastHits[i];
            if (hit.distance < minimumPlacementDistanceMeters || hit.distance > maximumPlacementDistanceMeters)
                continue;

            ARPlane plane = hit.trackable as ARPlane;
            if (plane == null)
                continue;

            if (requireHorizontalUpwardPlane && plane.alignment != PlaneAlignment.HorizontalUp)
                continue;

            pose = hit.pose;
            stablePlaneHitFrames++;
            return true;
        }

        stablePlaneHitFrames = 0;
        return false;
    }

    private static RectTransform AddCenteredRect(Transform parent, string name, Vector2 size)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        RectTransform rectTransform = go.AddComponent<RectTransform>();
        rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        rectTransform.sizeDelta = size;
        rectTransform.anchoredPosition = Vector2.zero;
        return rectTransform;
    }

    private static Texture2D CreateRingTexture(int size, float innerFraction)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float center = size * 0.5f;
        float outerRadius = center - 0.5f;
        float innerRadius = center * innerFraction;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Mathf.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                texture.SetPixel(x, y, distance >= innerRadius && distance <= outerRadius ? Color.white : Color.clear);
            }
        }

        texture.Apply();
        return texture;
    }

    private void UpdateReticle(Pose pose, bool isPlane)
    {
        if (reticleObject == null)
            return;

        reticleObject.SetActive(true);
        reticleObject.transform.position = pose.position + Vector3.up * 0.003f;

        reticleAngle = (reticleAngle + 40f * Time.deltaTime) % 360f;
        reticleObject.transform.rotation = Quaternion.Euler(0f, reticleAngle, 0f);

        float scale = isPlane ? 1f : (0.88f + 0.12f * Mathf.Sin(Time.time * 5f));
        reticleObject.transform.localScale = new Vector3(0.25f * scale, 0.002f, 0.25f * scale);

        Color color = isPlane ? new Color(0.15f, 1f, 0.45f) : new Color(1f, 0.75f, 0.1f);
        ApplyColor(reticleMaterial, color);
    }

    private void SetReticleVisible(bool visible)
    {
        if (reticleObject != null)
            reticleObject.SetActive(visible);
    }

    private void SetHintText(string text)
    {
        if (hintText != null)
            hintText.text = text;
    }

    private void SetCenterRingColor(Color color)
    {
        if (centerRingImage != null)
            centerRingImage.color = color;
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

    private void DisableSceneObjectsForAR()
    {
        if (sceneCameraToDisable == null)
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null && mainCamera.gameObject != arCameraObject)
                sceneCameraToDisable = mainCamera;
        }

        if (sceneCameraToDisable != null && sceneCameraToDisable.gameObject != arCameraObject)
            sceneCameraToDisable.gameObject.SetActive(false);

        if (objectsToDisableOnStart == null)
            return;

        foreach (GameObject go in objectsToDisableOnStart)
        {
            if (go != null)
                go.SetActive(false);
        }
    }

    private void PlaceOrMoveObject(Pose hitPose)
    {
        if (placedObject == null)
        {
            placedObject = placementPrefab != null
                ? Instantiate(placementPrefab)
                : GameObject.CreatePrimitive(PrimitiveType.Cube);
            placedObject.name = "AR Placement Object";
            if (placementPrefab == null)
                placedObject.transform.localScale = Vector3.one * fallbackCubeSizeMeters;

            placedObjectBaseScale = placedObject.transform.localScale;
            onObjectPlaced?.Invoke();
        }

        lastPlacementPosition = hitPose.position;
        lastPlacementBaseRotation = BuildPlacementRotation();
        hasLastPlacementPose = true;
        ApplyPlacedObjectTransform(lastPlacementPosition, lastPlacementBaseRotation);

        if (placeOnFirstDetectedPlane || !keepUpdatingPositionUntilFound)
        {
            objectPlacementLocked = true;
            if (hideDetectedPlanesAfterPlacement)
                SetPlaneVisualizationVisible(false);
        }
    }

    private void ApplyPlacedObjectTransform(Vector3 basePosition, Quaternion baseRotation)
    {
        if (placedObject == null)
            return;

        placedObject.transform.SetPositionAndRotation(
            basePosition + baseRotation * localPositionOffset,
            baseRotation * Quaternion.Euler(localEulerOffset));
        placedObject.transform.localScale = Vector3.Scale(placedObjectBaseScale, localScaleMultiplier);
    }

    private Quaternion BuildPlacementRotation()
    {
        if (xrOrigin == null || xrOrigin.Camera == null)
            return Quaternion.identity;

        Vector3 forward = xrOrigin.Camera.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            return Quaternion.Euler(0f, placementRotationYOffset, 0f);

        return Quaternion.LookRotation(forward.normalized, Vector3.up) *
               Quaternion.Euler(0f, placementRotationYOffset, 0f);
    }

    private void UpdatePlacedObjectFacing()
    {
        if (!keepPlacedObjectFacingCameraOnY || placedObject == null)
            return;

        if (!hasLastPlacementPose)
        {
            lastPlacementPosition = placedObject.transform.position;
            hasLastPlacementPose = true;
        }

        lastPlacementBaseRotation = BuildPlacementRotation();
        ApplyPlacedObjectTransform(lastPlacementPosition, lastPlacementBaseRotation);
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

    private void RestorePlaneVisualization()
    {
        if (arPlaneManager == null)
            return;

        arPlaneManager.planePrefab = showDetectedPlanes ? planeVisualizationPrefab : null;
        SetPlaneVisualizationVisible(showDetectedPlanes);
    }

    private void SetPlaneVisualizationVisible(bool visible)
    {
        if (arPlaneManager == null)
            return;

        if (!visible)
            arPlaneManager.planePrefab = null;

        foreach (ARPlane plane in arPlaneManager.trackables)
        {
            ARPlaneMeshVisualizer visualizer = plane.GetComponent<ARPlaneMeshVisualizer>();
            if (visualizer != null)
                visualizer.enabled = visible;

            MeshRenderer meshRenderer = plane.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
                meshRenderer.enabled = visible;

            LineRenderer lineRenderer = plane.GetComponent<LineRenderer>();
            if (lineRenderer != null)
                lineRenderer.enabled = visible;
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.XR;
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
    private sealed class RuntimeValueControl
    {
        public InputField InputField;
    }

    [Header("Launch")]
    [SerializeField] private Button startArButton;
    [SerializeField] private Camera sceneCameraToDisable;
    [SerializeField] private GameObject[] objectsToDisableOnStart;

    [Header("AR Camera")]
    [SerializeField] private float arCameraNearClipPlane = 0.01f;
    [SerializeField] private float arCameraFarClipPlane = 100f;

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

    private void Awake()
    {
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
    }

    private void OnDisable()
    {
        if (startArButton != null)
            startArButton.onClick.RemoveListener(StartAR);

        if (trackedImageManager != null)
            trackedImageManager.trackablesChanged.RemoveListener(OnTrackedImagesChanged);
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
        StartCoroutine(StartARRoutine());
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
        urpData.renderPostProcessing = false;

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
        CreateLockedContentAnchorAsync(content, new Pose(lockedReferencePosition, lockedReferenceRotation));

        Debug.Log("[AR Marker] Content spawned without marker after code launch.", content);
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

        bool valuesChanged = newPositionOffset != localPositionOffset
            || newEulerOffset != localEulerOffset
            || newScaleMultiplier != localScaleMultiplier;

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

        if (objectsToDisableOnStart == null)
            return;

        foreach (GameObject go in objectsToDisableOnStart)
        {
            if (go != null)
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

            sceneCamera.gameObject.SetActive(false);
        }

        if (sceneCameraToDisable != null && sceneCameraToDisable.gameObject != arCameraObject)
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
}

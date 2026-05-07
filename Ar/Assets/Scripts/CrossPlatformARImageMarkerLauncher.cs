using System.Collections;
using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class CrossPlatformARImageMarkerLauncher : MonoBehaviour
{
    [Header("Launch")]
    [SerializeField] private Button startArButton;
    [SerializeField] private Camera sceneCameraToDisable;
    [SerializeField] private GameObject[] objectsToDisableOnStart;

    [Header("Marker Tracking")]
    [SerializeField] private XRReferenceImageLibrary referenceImageLibrary;
    [SerializeField] private string trackedImageNameFilter = "";
    [SerializeField] private int maxMovingImages = 1;
    [SerializeField] private bool hideContentWhenMarkerNotTracked = true;

    [Header("Content")]
    [SerializeField] private GameObject placementPrefab;
    [SerializeField] private float fallbackCubeSizeMeters = 0.15f;
    [SerializeField] private Vector3 localPositionOffset = Vector3.zero;
    [SerializeField] private Vector3 localEulerOffset = Vector3.zero;
    [SerializeField] private Vector3 localScaleMultiplier = Vector3.one;
    [SerializeField] private bool useFixedWorldScale = true;
    [SerializeField] private bool lockContentAfterFirstMarkerFound = true;
    [SerializeField] private float lockDelaySeconds = 0.35f;
    [SerializeField] private bool keepPlacedObjectFacingCameraOnY = false;

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
    private XROrigin xrOrigin;
    private ARTrackedImageManager trackedImageManager;
    private ARCameraManager arCameraManager;
    private bool arStartRequested;
    private bool didInvokeStarted;
    private bool contentLockedToWorld;
    private TrackableId pendingLockTrackableId;
    private float pendingLockElapsed;
    private float logTimer;

    private void Awake()
    {
        ApplyPerformanceSettings();
        BuildARRig();
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

        if (keepPlacedObjectFacingCameraOnY && !contentLockedToWorld)
            UpdateSpawnedContentFacing();
    }

    public void StartAR()
    {
        if (arStartRequested)
            return;

        if (referenceImageLibrary == null)
        {
            Debug.LogError("[AR Marker] Reference Image Library is not assigned.");
            return;
        }

        arStartRequested = true;
        didInvokeStarted = false;
        contentLockedToWorld = false;
        pendingLockTrackableId = default;
        pendingLockElapsed = 0f;
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
        pendingLockElapsed = 0f;
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

        trackedImageManager.referenceLibrary = referenceImageLibrary;
        trackedImageManager.requestedMaxNumberOfMovingImages = Mathf.Max(1, maxMovingImages);
        trackedImageManager.enabled = true;

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

        Debug.Log($"[AR Marker] Runtime session state = {ARSession.state}");
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
        trackedImageManager = xrOriginObject.AddComponent<ARTrackedImageManager>();
        trackedImageManager.enabled = false;

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
            content = Instantiate(GetPlacementPrefab());
            content.name = string.IsNullOrWhiteSpace(trackedImage.referenceImage.name)
                ? "AR Marker Content"
                : $"AR Marker Content ({trackedImage.referenceImage.name})";
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

    private GameObject GetPlacementPrefab()
    {
        if (placementPrefab != null)
            return placementPrefab;

        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.localScale = Vector3.one * fallbackCubeSizeMeters;
        Renderer renderer = cube.GetComponent<Renderer>();
        if (renderer != null)
            renderer.sharedMaterial = CreateUnlitMaterial(new Color(0.15f, 1f, 0.45f), false);
        return cube;
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

        contentLockedToWorld = true;
        ResetPendingLock();
        content.transform.SetParent(null, true);
        content.SetActive(true);

        if (trackedImageManager != null)
            trackedImageManager.enabled = false;

        Debug.Log("[AR Marker] Content locked in world space after first marker detection.", content);
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

        if (pendingLockTrackableId != trackableId)
        {
            pendingLockTrackableId = trackableId;
            pendingLockElapsed = 0f;
        }

        pendingLockElapsed += Time.unscaledDeltaTime;
        if (pendingLockElapsed >= lockDelaySeconds)
            LockContentToWorld(content);
    }

    private void ResetPendingLock()
    {
        pendingLockTrackableId = default;
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
        foreach (GameObject content in spawnedContent.Values)
        {
            if (content != null)
                Destroy(content);
        }

        spawnedContent.Clear();
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

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;

using XRInputDevice = UnityEngine.XR.InputDevice;
using XRCommonUsages = UnityEngine.XR.CommonUsages;

[DefaultExecutionOrder(32000)]
public sealed class ARCameraPoseFallback : MonoBehaviour
{
    private static readonly List<XRInputDevice> XrDevices = new List<XRInputDevice>();

    private readonly InputAction positionAction = new InputAction("AR Camera Fallback Position", expectedControlType: "Vector3");
    private readonly InputAction rotationAction = new InputAction("AR Camera Fallback Rotation", expectedControlType: "Quaternion");

    private XRInputDevice trackingDevice;
    private bool lastPoseHadInputSystemPosition;
    private bool lastPoseHadInputSystemRotation;
    private bool lastPoseHadXrPosition;
    private bool lastPoseHadXrRotation;

    public string DebugStatus
    {
        get
        {
            string positionSource = lastPoseHadInputSystemPosition ? "InputSystem" : lastPoseHadXrPosition ? "XR" : "No";
            string rotationSource = lastPoseHadInputSystemRotation ? "InputSystem" : lastPoseHadXrRotation ? "XR" : "No";
            return $"Position={positionSource}, Rotation={rotationSource}";
        }
    }

    private void Awake()
    {
        positionAction.AddBinding("<HandheldARInputDevice>/devicePosition");
        positionAction.AddBinding("<XRHMD>/centerEyePosition");

        rotationAction.AddBinding("<HandheldARInputDevice>/deviceRotation");
        rotationAction.AddBinding("<XRHMD>/centerEyeRotation");
    }

    private void OnEnable()
    {
        positionAction.Enable();
        rotationAction.Enable();
        Application.onBeforeRender += ApplyPose;
        InputDevices.deviceConnected += OnDeviceConnected;
        InputDevices.deviceDisconnected += OnDeviceDisconnected;
        FindTrackingDevice();
    }

    private void OnDisable()
    {
        Application.onBeforeRender -= ApplyPose;
        InputDevices.deviceConnected -= OnDeviceConnected;
        InputDevices.deviceDisconnected -= OnDeviceDisconnected;
        positionAction.Disable();
        rotationAction.Disable();
    }

    private void OnDestroy()
    {
        positionAction.Dispose();
        rotationAction.Dispose();
    }

    private void Update()
    {
        ApplyPose();
    }

    private void OnDeviceConnected(XRInputDevice device)
    {
        if (!trackingDevice.isValid || !CanReadCompletePose(trackingDevice))
            FindTrackingDevice();
    }

    private void OnDeviceDisconnected(XRInputDevice device)
    {
        trackingDevice = default;
    }

    private void FindTrackingDevice()
    {
        XrDevices.Clear();
        InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.TrackedDevice, XrDevices);

        foreach (XRInputDevice device in XrDevices)
        {
            if (CanReadCompletePose(device))
            {
                trackingDevice = device;
                return;
            }
        }

        foreach (XRInputDevice device in XrDevices)
        {
            if (CanReadAnyPose(device))
            {
                trackingDevice = device;
                return;
            }
        }
    }

    private void ApplyPose()
    {
        lastPoseHadXrPosition = false;
        lastPoseHadXrRotation = false;
        lastPoseHadInputSystemPosition = false;
        lastPoseHadInputSystemRotation = false;

        // XR legacy API is the authoritative source — applies directly from the XR subsystem
        // without going through the Input System pipeline, so it works even when
        // HandheldARInputDevice is not yet registered or returns a zero position.
        if (!trackingDevice.isValid || !CanReadAnyPose(trackingDevice))
            FindTrackingDevice();

        if (trackingDevice.isValid)
        {
            lastPoseHadXrPosition = TryReadXrPosition(trackingDevice, out Vector3 xrPosition);
            lastPoseHadXrRotation = TryReadXrRotation(trackingDevice, out Quaternion xrRotation);

            if (lastPoseHadXrPosition)
                transform.localPosition = xrPosition;
            if (lastPoseHadXrRotation)
                transform.localRotation = xrRotation;
        }

        // Input System fallback — only used when XR legacy API has no device
        if (!lastPoseHadXrPosition)
        {
            lastPoseHadInputSystemPosition = TryReadInputAction(positionAction, out Vector3 isPosition);
            if (lastPoseHadInputSystemPosition)
                transform.localPosition = isPosition;
        }

        if (!lastPoseHadXrRotation)
        {
            lastPoseHadInputSystemRotation = TryReadInputAction(rotationAction, out Quaternion isRotation);
            if (lastPoseHadInputSystemRotation)
                transform.localRotation = isRotation;
        }
    }

    private static bool TryReadInputAction<TValue>(InputAction action, out TValue value)
        where TValue : struct
    {
        value = default;
        if (action == null || !action.enabled || action.controls.Count == 0)
            return false;

        value = action.ReadValue<TValue>();
        return true;
    }

    private static bool CanReadCompletePose(XRInputDevice device)
    {
        return TryReadXrPosition(device, out _) && TryReadXrRotation(device, out _);
    }

    private static bool CanReadAnyPose(XRInputDevice device)
    {
        return device.isValid && (TryReadXrPosition(device, out _) || TryReadXrRotation(device, out _));
    }

    private static bool TryReadXrPosition(XRInputDevice device, out Vector3 position)
    {
        position = Vector3.zero;
        return device.isValid &&
               (device.TryGetFeatureValue(XRCommonUsages.centerEyePosition, out position) ||
                device.TryGetFeatureValue(XRCommonUsages.colorCameraPosition, out position));
    }

    private static bool TryReadXrRotation(XRInputDevice device, out Quaternion rotation)
    {
        rotation = Quaternion.identity;
        return device.isValid &&
               (device.TryGetFeatureValue(XRCommonUsages.centerEyeRotation, out rotation) ||
                device.TryGetFeatureValue(XRCommonUsages.colorCameraRotation, out rotation));
    }
}

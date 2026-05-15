using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RapidClickCodeArStarter : MonoBehaviour
{
    [Header("Click Sequence")]
    [SerializeField] private Button clickButton;
    [SerializeField] private int requiredClicks = 5;
    [SerializeField] private float maxSecondsBetweenClicks = 0.45f;

    [Header("Code Panel")]
    [SerializeField] private CanvasGroup codeCanvasGroup;
    [SerializeField] private TMP_InputField codeInputField;
    [SerializeField] private Button confirmButton;
    [SerializeField] private bool hidePanelOnAwake = true;
    [SerializeField] private bool hidePanelAfterSuccess = true;

    [Header("AR")]
    [SerializeField] private CrossPlatformARImageMarkerLauncher arLauncher;

    private int clickCount;
    private float lastClickTime = -999f;

    private void Awake()
    {
        if (arLauncher == null)
            arLauncher = FindFirstObjectByType<CrossPlatformARImageMarkerLauncher>();

        if (hidePanelOnAwake)
            SetCodePanelVisible(false);
    }

    private void OnEnable()
    {
        if (clickButton != null)
            clickButton.onClick.AddListener(RegisterClick);

        if (confirmButton != null)
            confirmButton.onClick.AddListener(ConfirmCode);
    }

    private void OnDisable()
    {
        if (clickButton != null)
            clickButton.onClick.RemoveListener(RegisterClick);

        if (confirmButton != null)
            confirmButton.onClick.RemoveListener(ConfirmCode);
    }

    public void RegisterClick()
    {
        float now = Time.unscaledTime;
        if (now - lastClickTime > maxSecondsBetweenClicks)
            clickCount = 0;

        lastClickTime = now;
        clickCount++;

        if (clickCount >= Mathf.Max(1, requiredClicks))
        {
            clickCount = 0;
            ShowCodePanel();
        }
    }

    public void ConfirmCode()
    {
        string typedCode = codeInputField != null ? codeInputField.text.Trim() : string.Empty;

        if (arLauncher == null)
            arLauncher = FindFirstObjectByType<CrossPlatformARImageMarkerLauncher>();

        if (arLauncher == null)
        {
            Debug.LogError("[AR Code Starter] CrossPlatformARImageMarkerLauncher was not found in the scene.");
            return;
        }

        bool success = arLauncher.StartARWithCode(typedCode);

        if (!success)
        {
            if (codeInputField != null)
                codeInputField.text = string.Empty;

            Debug.LogWarning("[AR Code Starter] Wrong code entered.");
            return;
        }

        if (hidePanelAfterSuccess)
            SetCodePanelVisible(false);
    }

    public void HideCanvasGroup()
    {
        SetCodePanelVisible(false);
        
        if (codeInputField == null)
            return;

        codeInputField.text = string.Empty;
    }

    private void ShowCodePanel()
    {
        SetCodePanelVisible(true);

        if (codeInputField == null)
            return;

        codeInputField.text = string.Empty;
        codeInputField.Select();
        codeInputField.ActivateInputField();
    }

    private void SetCodePanelVisible(bool visible)
    {
        if (codeCanvasGroup == null)
            return;

        codeCanvasGroup.alpha = visible ? 1f : 0f;
        codeCanvasGroup.interactable = visible;
        codeCanvasGroup.blocksRaycasts = visible;
    }
}

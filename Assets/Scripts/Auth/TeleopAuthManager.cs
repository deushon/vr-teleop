using System;
using System.Collections;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

public class TeleopAuthManager : MonoBehaviour
{
    [Header("Connection")]
    private string ip = "127.0.0.1";
    private string port = "8080";
    public DefaultTextValue ipText;
    public DefaultTextValue portText;

    [Header("Credentials")]
    public DefaultTextValue usernameText;
    public DefaultTextValue passwordText;

    [Header("Navigation Elements")]
    [SerializeField] private GameObject ShowDashboard;
    [SerializeField] private GameObject HideDashboard;
    [SerializeField] private GameObject CameraPanelSettings;
    [SerializeField] private GameObject DashboardPanelSettings;
    [SerializeField] private GameObject LogoutButton;
    [SerializeField] private GameObject LoginButton;
    [SerializeField] private GameObject AuthCanvas;

    [Header("Optional UI")]
    [SerializeField] private AutoDestroyTMPText logs;

    public bool IsBusy { get; private set; }

    public string BaseUrl
    {
        get
        {
            string safeIp = (ip ?? string.Empty).Trim();
            string safePort = (port ?? string.Empty).Trim();
            return $"http://{safeIp}:{safePort}";
        }
    }

    public void Login()
    {
        if (IsBusy)
        {
            Debug.LogWarning("[AUTH] Login skipped: request already in progress.");
            return;
        }

        StartCoroutine(LoginCoroutine());
    }

    public void Logout()
    {
        if (IsBusy)
        {
            Debug.LogWarning("[AUTH] Logout skipped: request already in progress.");
            return;
        }

        StartCoroutine(LogoutCoroutine());
    }

    private IEnumerator LoginCoroutine()
    {
        IsBusy = true;
        SetStatus("[AUTH] Logging in...");

        ip = ipText.inputedValue;
        port = portText.inputedValue;

        string loginUrl = $"{BaseUrl}/api/teleoperator/login";

        var requestBody = new TeleopLoginRequest
        {
            login = usernameText.inputedValue,
            password = passwordText.inputedValue
        };

        string json = JsonUtility.ToJson(requestBody);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(json);

        using var request = new UnityWebRequest(loginUrl, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("accept", "application/json");
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        IsBusy = false;

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[AUTH] Login failed: {request.error}");
            SetStatus($"[AUTH] Login failed: {request.error}");
            yield break;
        }

        string responseText = request.downloadHandler.text;
        if (string.IsNullOrWhiteSpace(responseText))
        {
            Debug.LogError("[AUTH] Login failed: empty response.");
            SetStatus("[AUTH] Login failed: empty response.");
            yield break;
        }

        TeleopLoginResponse response;
        try
        {
            response = JsonUtility.FromJson<TeleopLoginResponse>(responseText);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[AUTH] Login parse failed: {ex.Message}\nResponse: {responseText}");
            SetStatus("[AUTH] Login parse failed.");
            yield break;
        }

        if (response == null)
        {
            Debug.LogError("[AUTH] Login failed: response is null.");
            SetStatus("[AUTH] Login failed: invalid response.");
            yield break;
        }

        if (!response.ok)
        {
            TeleopAuthSession.Clear();
            Debug.LogWarning("[AUTH] Login response returned ok=false.");
            SetStatus("[AUTH] Login rejected.");
            yield break;
        }

        TeleopAuthSession.SetFromResponse(response);

        ShowDashboard.SetActive(true);
        DashboardPanelSettings.SetActive(true);
        ShowDashboard.GetComponent<Button>().onClick.Invoke();
        LogoutButton.SetActive(true);
        LoginButton.SetActive(false);
        AuthCanvas.SetActive(false);

        Debug.Log($"[AUTH] Login OK. User={TeleopAuthSession.UserLogin}, id={TeleopAuthSession.UserId}");
        SetStatus($"[AUTH] Logged in as {TeleopAuthSession.UserLogin}");
    }

    private IEnumerator LogoutCoroutine()
    {
        IsBusy = true;
        SetStatus("[AUTH] Logging out...");

        string logoutUrl = $"{BaseUrl}/api/teleoperator/logout";

        using var request = new UnityWebRequest(logoutUrl, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(Array.Empty<byte>());
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("accept", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[AUTH] Logout request failed: {request.error}");
        }
        else
        {
            Debug.Log("[AUTH] Logout request completed.");
        }

        TeleopAuthSession.Clear();

        IsBusy = false;

        HideDashboard.SetActive(true);
        DashboardPanelSettings.SetActive(false);
        CameraPanelSettings.SetActive(false);
        HideDashboard.GetComponent<Button>().onClick.Invoke();
        ShowDashboard.SetActive(false);
        LogoutButton.SetActive(false);
        LoginButton.SetActive(true);
        SetStatus("[AUTH] Logged out.");
    }

    private void SetStatus(string text)
    {
        Debug.Log(text);

        if (logs != null)
            logs.SetText(text);
    }
}
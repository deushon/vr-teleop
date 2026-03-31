using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;

public class TeleopHelpRequestsManager : MonoBehaviour
{
    [Header("Connection")]
    private string ip = "127.0.0.1";
    private string port = "8080";
    public DefaultTextValue ipText;
    public DefaultTextValue portText;

    [Header("References")]
    [SerializeField] private TaskManager taskManager;

    [Header("Optional UI")]
    [SerializeField] private AutoDestroyTMPText logs;

    [Header("Behavior")]
    public bool clearExistingTasksBeforePopulate = false;

    public bool IsBusy { get; private set; }

    private string BaseUrl => $"http://{ip?.Trim()}:{port?.Trim()}";

    public void LoadHelpRequests()
    {
        ip = ipText.inputedValue;
        port = portText.inputedValue;
        if (IsBusy)
        {
            Debug.LogWarning("[HELP] Request already in progress.");
            return;
        }

        StartCoroutine(LoadHelpRequestsCoroutine());
    }

    private IEnumerator LoadHelpRequestsCoroutine()
    {
        if (taskManager == null)
        {
            Debug.LogError("[HELP] TaskManager is not assigned.");
            SetStatus("[HELP] TaskManager is not assigned.");
            yield break;
        }

        if (!TeleopAuthSession.IsAuthorized || string.IsNullOrWhiteSpace(TeleopAuthSession.AccessToken))
        {
            Debug.LogError("[HELP] No auth token. Login first.");
            SetStatus("[HELP] No auth token. Login first.");
            yield break;
        }

        IsBusy = true;
        SetStatus("[HELP] Loading help requests...");

        string url = $"{BaseUrl}/api/teleoperator/help-requests";

        using var request = UnityWebRequest.Get(url);
        request.SetRequestHeader("accept", "*/*");
        request.SetRequestHeader("Authorization", $"Bearer {TeleopAuthSession.AccessToken}");

        yield return request.SendWebRequest();

        IsBusy = false;

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[HELP] Request failed: {request.error}");
            SetStatus($"[HELP] Request failed: {request.error}");
            yield break;
        }

        string json = request.downloadHandler.text;
        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.LogWarning("[HELP] Empty response.");
            SetStatus("[HELP] Empty response.");
            yield break;
        }

        TeleopHelpRequestsResponse response;
        try
        {
            response = JsonConvert.DeserializeObject<TeleopHelpRequestsResponse>(json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HELP] JSON parse failed: {ex.Message}\n{json}");
            SetStatus("[HELP] JSON parse failed.");
            yield break;
        }

        if (response?.helpRequests == null)
        {
            Debug.LogWarning("[HELP] No helpRequests in response.");
            SetStatus("[HELP] No help requests found.");
            yield break;
        }

        if (clearExistingTasksBeforePopulate)
        {
            taskManager.ClearAllTasks();
        }

        int createdCount = 0;

        foreach (var requestDto in response.helpRequests)
        {
            if (requestDto == null)
                continue;

            taskManager.AddNewTask(requestDto);

            createdCount++;
        }

        Debug.Log($"[HELP] Loaded {createdCount} help requests.");
        SetStatus($"[HELP] Loaded {createdCount} help requests.");
    }

    public void AcceptHelpRequest(string requestId, Action<string> onDone)
    {
        if (IsBusy)
        {
            Debug.LogWarning("[HELP] Accept skipped: request already in progress.");
            onDone?.Invoke(null);
            return;
        }

        if (string.IsNullOrWhiteSpace(requestId))
        {
            Debug.LogWarning("[HELP] Accept skipped: requestId is empty.");
            onDone?.Invoke(null);
            return;
        }

        StartCoroutine(AcceptHelpRequestCoroutine(requestId, onDone));
    }

    private IEnumerator AcceptHelpRequestCoroutine(string requestId, Action<string> onDone)
    {
        if (!TeleopAuthSession.IsAuthorized || string.IsNullOrWhiteSpace(TeleopAuthSession.AccessToken))
        {
            Debug.LogError("[HELP] No auth token. Login first.");
            SetStatus("[HELP] No auth token. Login first.");
            onDone?.Invoke(null);
            yield break;
        }

        IsBusy = true;
        SetStatus($"[HELP] Accepting request {requestId}...");

        string url = $"{BaseUrl}/api/teleoperator/help-requests/{requestId}/accept";

        using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(Array.Empty<byte>());
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("accept", "*/*");
        request.SetRequestHeader("Authorization", $"Bearer {TeleopAuthSession.AccessToken}");

        yield return request.SendWebRequest();

        IsBusy = false;

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[HELP] Accept failed: {request.error}");
            SetStatus($"[HELP] Accept failed: {request.error}");
            onDone?.Invoke(null);
            yield break;
        }

        string json = request.downloadHandler.text;
        if (string.IsNullOrWhiteSpace(json))
        {
            Debug.LogWarning("[HELP] Accept returned empty response.");
            SetStatus("[HELP] Accept returned empty response.");
            onDone?.Invoke(null);
            yield break;
        }

        TeleopAcceptHelpRequestResponse response;
        try
        {
            response = JsonConvert.DeserializeObject<TeleopAcceptHelpRequestResponse>(json);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[HELP] Accept parse failed: {ex.Message}\n{json}");
            SetStatus("[HELP] Accept parse failed.");
            onDone?.Invoke(null);
            yield break;
        }

        if (response == null || !response.ok || response.session == null || string.IsNullOrWhiteSpace(response.session.id))
        {
            Debug.LogWarning("[HELP] Accept response does not contain session id.");
            SetStatus("[HELP] Accept failed: no session id.");
            onDone?.Invoke(null);
            yield break;
        }

        string sessionId = response.session.id;

        Debug.Log($"[HELP] Request accepted. Session ID = {sessionId}");
        SetStatus($"[HELP] Accepted. Session ID = {sessionId}");

        onDone?.Invoke(sessionId);
    }


    private void SetStatus(string text)
    {
        if (logs != null)
            logs.SetText(text);
    }
}
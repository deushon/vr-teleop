using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class EndSessionManager : MonoBehaviour
{
    [SerializeField]
    private RosbridgeImageSubscriber imageSubscriber;
    [SerializeField]
    private TeleopHelpRequestsManager telemetryManager;
    [SerializeField]
    private DatasetManager datasetManager;
    [SerializeField]
    private TaskManager taskManager;
    [SerializeField]
    private GameObject[] objectsToDisable;
    [SerializeField]
    private QuestRosPoseAndJointsPublisher publisher;
    [SerializeField]
    private GameObject metaDataScreen;
    [SerializeField]
    private GameObject recordingWindow;
    [SerializeField]
    private GameObject taskManagerScreen;
    [SerializeField]
    private Button endSessionButton;

    [SerializeField, Min(1)]
    private int maxUploadAttempts = 3;
    [SerializeField, Min(0f)]
    private float uploadRetryDelaySec = 3f;

    public void TryToEndSession(string reason)
    {
        StartCoroutine(TryToEndSessionCoroutine(reason));
    }

    public void EndGracefully()
    {
        TryToEndSession("graceful_complete");
    }

    public void CancelSession()
    {
        TryToEndSession("operator_cancelled");
    }

    public void AbortForNetworkQuality()
    {
        TryToEndSession("network_quality_abort");
    }

    public void DeclineBeforeConnect()
    {
        TryToEndSession("decline_before_connect");
    }

    private IEnumerator TryToEndSessionCoroutine(string reason)
    {
        if (endSessionButton != null)
            endSessionButton.interactable = false;

        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        if (telemetryManager == null)
            telemetryManager = FindFirstObjectByType<TeleopHelpRequestsManager>();

        if (publisher == null)
            publisher = FindFirstObjectByType<QuestRosPoseAndJointsPublisher>();

        string sessionId = GetActiveSessionId();
        bool declineBeforeConnect = IsDeclineBeforeConnectReason(reason);
        string raidEndReason = NormalizeRaidEndReason(reason);

        if (declineBeforeConnect)
        {
            datasetManager?.MarkBriefRejected();
            yield return SendDeclineBeforeConnect(sessionId);
            FinishLocalSessionUi();
            yield break;
        }

        publisher?.PrepareForDatasetUploadEnd();

        if (datasetManager != null && datasetManager.GetRecordSize() > 0)
        {
            bool uploaded = false;
            int attempts = Mathf.Max(1, maxUploadAttempts);

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                if (datasetManager.LogText != null)
                    datasetManager.LogText.SetText($"Uploading dataset, attempt {attempt}/{attempts}");

                yield return datasetManager.SendAllRecordsCoroutine();

                if (datasetManager.GetSendStatus() == 0)
                {
                    uploaded = true;
                    break;
                }

                if (attempt < attempts)
                {
                    if (datasetManager.LogText != null)
                        datasetManager.LogText.SetText($"Dataset upload failed, retrying {attempt + 1}/{attempts}");

                    yield return new WaitForSeconds(uploadRetryDelaySec);
                }
            }

            if (!uploaded)
            {
                if (datasetManager.LogText != null)
                    datasetManager.LogText.SetText("Dataset upload failed after retries. Closing session.");

                yield return new WaitForSeconds(1);
            }
            else
            {
                datasetManager.ClearAllRecords();
            }
        }

        yield return SendSessionEnd(sessionId, raidEndReason);
        imageSubscriber?.StopConnection();
        FinishLocalSessionUi();
        yield break;
    }

    private IEnumerator SendDeclineBeforeConnect(string sessionId)
    {
        if (telemetryManager == null)
            yield break;

        bool done = false;
        bool ok = false;
        string response = null;

        yield return telemetryManager.DeclineBeforeConnectCoroutine(sessionId, (success, message) =>
        {
            ok = success;
            response = message;
            done = true;
        });

        if (!done)
            yield break;

        Debug.Log(ok
            ? $"[END] Decline before connect sent: {response}"
            : $"[END] Decline before connect failed: {response}");

        if (!ok && !string.IsNullOrWhiteSpace(response) && response.Contains("409"))
        {
            Debug.Log("[END] Decline rejected by RAID, falling back to /end with operator_cancelled.");
            yield return SendSessionEnd(sessionId, "operator_cancelled");
        }
    }

    private IEnumerator SendSessionEnd(string sessionId, string reason)
    {
        if (telemetryManager == null)
            yield break;

        bool done = false;
        bool ok = false;
        string response = null;

        yield return telemetryManager.EndSessionCoroutine(sessionId, reason, (success, message) =>
        {
            ok = success;
            response = message;
            done = true;
        });

        if (!done)
            yield break;

        Debug.Log(ok
            ? $"[END] Session end sent: {reason}. {response}"
            : $"[END] Session end failed: {reason}. {response}");
    }

    private void FinishLocalSessionUi()
    {   
        telemetryManager?.LoadHelpRequests();

        if (metaDataScreen != null)
            metaDataScreen.SetActive(true);

        if (recordingWindow != null)
            recordingWindow.SetActive(false);

        if (taskManagerScreen != null)
            taskManagerScreen.SetActive(true);

        if (objectsToDisable != null)
        {
            foreach (var obj in objectsToDisable)
            {
                if (obj != null)
                    obj.SetActive(false);
            }
        }

        if (endSessionButton != null)
            endSessionButton.interactable = true;
    }

    private string GetActiveSessionId()
    {
        var activeTask = taskManager != null ? taskManager.GetActiveTaskData() : null;
        return activeTask != null ? activeTask.SessionId : string.Empty;
    }

    private static bool IsDeclineBeforeConnectReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        switch (reason.Trim())
        {
            case "decline_before_connect":
            case "brief_declined_before_proxy":
            case "card_declined":
                return true;
            default:
                return false;
        }
    }

    private static string NormalizeRaidEndReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "client_error";

        switch (reason.Trim())
        {
            case "graceful_complete":
            case "successfull_record":
            case "successful_record":
            case "record_complete":
                return "graceful_complete";

            case "operator_cancelled":
            case "user_call_end":
            case "user_exit":
            case "operator_abort":
                return "operator_cancelled";

            case "network_quality_abort":
            case "ping_exceeded":
            case "ping_error":
            case "high_ping":
            case "network":
                return "network_quality_abort";

            case "client_error":
                return "client_error";

            default:
                return "client_error";
        }
    }
}

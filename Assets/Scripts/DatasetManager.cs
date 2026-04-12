using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System.Text;
using Newtonsoft.Json;
using TMPro;
using UnityEngine.Networking;
using System.IO;

public class DatasetManager : MonoBehaviour
{
    [SerializeField] private RectTransform parentForLayout;
    [SerializeField] private GameObject recordUI;
    [SerializeField] private NumberInput keyboardManager;

    //[SerializeField] private Button sendRecordsButton;
    //[SerializeField] private Button clearAllRecordsButton;
    [SerializeField] private TaskManager taskManager;

    [SerializeField] private TMP_Text IpText;
    [SerializeField] private TMP_Text PortText;

    public AutoDestroyTMPText LogText;

    private List<GameObject> currentRecords = new List<GameObject>();

    private long acceptedAtUnixTimeNs;
    private string acceptedAtUtcIso;
    private bool hasAcceptedAt;
    private readonly List<TeleopControlEvent> teleopControlEvents = new();

    private int sendStatus = -1;

    public void AddNewRecord()
    {
        AddNewRecord(null);
    }

    public void AddNewRecord(RecordedSession session)
    {
        if (recordUI == null)
        {
            Debug.LogError("No record prefab");
            return;
        }

        if (parentForLayout == null)
        {
            Debug.LogError("No parentForLayout assigned");
            return;
        }

        GameObject record = Instantiate(recordUI, parentForLayout, false);

        RectTransform recordTransform = record.GetComponent<RectTransform>();
        if (recordTransform == null)
        {
            Debug.LogError("No RectTransform was found");
            return;
        }

        recordTransform.localScale = Vector3.one;
        recordTransform.anchoredPosition = Vector2.zero;

        var recordData = record.GetComponent<RecordData>();
        if (recordData == null)
        {
            Debug.LogError("No RecordData was found");
            return;
        }

        recordData.SetRecordedSession(session);

        var activeTaskData = taskManager.GetActiveTaskData();
        if (activeTaskData != null)
        {
            recordData.SetSelectedTask(activeTaskData);
            keyboardManager.SilentInput(recordData.TextField, activeTaskData.LabelTextField.text);
        }

        recordData.InputTextButton.onClick.AddListener(() =>
        {
            keyboardManager.Activate(recordData.TextField);
        });

        LayoutRebuilder.ForceRebuildLayoutImmediate(parentForLayout);
        currentRecords.Add(record);

        //if (!clearAllRecordsButton.interactable)
        //{
        //    //sendRecordsButton.interactable = true;
        //    clearAllRecordsButton.interactable = true;
        //}
    }
    public void DeleteRecord(GameObject record)
    {
        currentRecords.Remove(record);
        Destroy(record);
        //if (currentRecords.Count == 0)
        //{
        //    //sendRecordsButton.interactable = false;
        //    clearAllRecordsButton.interactable = false;
        //}
    }

    public void ClearAllRecords()
    {
        while (currentRecords.Count > 0)
        {
            Destroy(currentRecords[0]);
            currentRecords.RemoveAt(0);
        }
        //sendRecordsButton.interactable = false;
        //clearAllRecordsButton.interactable = false;
    }

    public void SendAllRecords()
    {
        StartCoroutine(SendAllRecordsCoroutine());
    }

    public int GetRecordSize()
    {
        return currentRecords.Count;
    }

    public int GetSendStatus()
    {
        return sendStatus;
    }

    public IEnumerator SendAllRecordsCoroutine()
    {
        sendStatus = -1;
        if (IpText == null || string.IsNullOrWhiteSpace(IpText.text))
        {
            Debug.LogError("[Dataset] Robot IP is empty");
            sendStatus = 1;
            yield break;
        }

        if (PortText == null || string.IsNullOrWhiteSpace(PortText.text))
        {
            Debug.LogError("[Dataset] Robot port is empty");
            sendStatus = 1;
            yield break;
        }

        if (taskManager == null || taskManager.GetActiveTaskData() == null)
        {
            Debug.LogError("[Dataset] Active task is missing");
            sendStatus = 1;
            yield break;
        }

        if (!TeleopAuthSession.IsAuthorized || string.IsNullOrWhiteSpace(TeleopAuthSession.AccessToken))
        {
            Debug.LogError("[Dataset] Access token is missing. Login first.");
            sendStatus = 1;
            yield break;
        }

        var robotId = taskManager.GetActiveTaskData().RobotId;
        if (string.IsNullOrWhiteSpace(robotId))
        {
            Debug.LogError("[Dataset] RobotId is empty");
            sendStatus = 1;
            yield break;
        }

        string ip = IpText.text.Trim();
        string port = PortText.text.Trim();

        string url = $"http://{ip}:{port}/api/teleop/robots/{robotId}/dataset/upload_dataset";

        var payload = new DatasetUploadRequest
        {
            source = "unity_quest_dataset",
            generatedUtcIso = System.DateTime.UtcNow.ToString("o"),
            contractVersion = 2,
            acceptedAtUtcIso = hasAcceptedAt ? acceptedAtUtcIso : null,
            teleopControl = new TeleopControlEventsBlock(),
        };

        payload.teleopControl.events.AddRange(teleopControlEvents);

        foreach (var recordObj in currentRecords)
        {
            if (recordObj == null)
                continue;

            var recordData = recordObj.GetComponent<RecordData>();
            if (recordData == null)
                continue;

            payload.records.Add(new DatasetUploadRecord
            {
                recordId = recordData.GetRecordId(),
                label = recordData.GetLabel(),
                taskName = recordData.GetSelectedTaskName(),
                data = recordData.GetRecordedSession()
            });
        }

        string json = JsonConvert.SerializeObject(payload, Formatting.Indented);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();

        request.SetRequestHeader("accept", "*/*");
        request.SetRequestHeader("Authorization", $"Bearer {TeleopAuthSession.AccessToken}");
        request.SetRequestHeader("Content-Type", "application/json");

        string startMessage = $"[Dataset] Uploading {payload.records.Count} records to {url}";
        if (LogText != null) LogText.SetText(startMessage);
        Debug.Log(startMessage);

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            string message = $"[Dataset] Upload success: {request.downloadHandler.text}";
            if (LogText != null) LogText.SetText(message);
            Debug.Log(message);

            teleopControlEvents.Clear();
            hasAcceptedAt = false;
            acceptedAtUnixTimeNs = 0L;
            acceptedAtUtcIso = null;

            sendStatus = 0;
        }
        else
        {
            string responseText = request.downloadHandler != null ? request.downloadHandler.text : "";
            string message = $"[Dataset] Upload failed: {request.result}, {request.error}\nResponse: {responseText}";
            if (LogText != null) LogText.SetText(message);
            Debug.LogError(message);
            sendStatus = 1;
        }
    }

    public void MarkAcceptedInWork()
    {
        acceptedAtUnixTimeNs = GetUnixTimeNs();
        acceptedAtUtcIso = System.DateTime.UtcNow.ToString("o");
        hasAcceptedAt = true;
        RegisterControlEvent("brief_accepted");

        Debug.Log($"[Dataset] Accepted in work at {acceptedAtUtcIso} ({acceptedAtUnixTimeNs})");

        if (LogText != null)
            LogText.SetText($"[Dataset] Accepted in work: {acceptedAtUtcIso}");
    }

    public void MarkBriefRejected()
    {
        RegisterControlEvent("brief_rejected");
    }

    public void RegisterControlEvent(bool hasControl)
    {
        RegisterControlEvent(hasControl ? "get_control" : "lost_control");
    }

    public void RegisterControlEvent(string eventType)
    {
        if (string.IsNullOrWhiteSpace(eventType))
            return;

        var evt = new TeleopControlEvent
        {
            eventType = eventType,
            timestampUtcIso = System.DateTime.UtcNow.ToString("o")
        };

        teleopControlEvents.Add(evt);

        Debug.Log($"[Dataset] Control event registered: {evt.eventType} at {evt.timestampUtcIso}");

        if (LogText != null)
            LogText.SetText($"[Dataset] Control event: {evt.eventType}");
    }

    public void RegisterLifecycleEvent(string eventName, string reason = null)
    {
        if (string.IsNullOrWhiteSpace(eventName))
            return;

        switch (eventName)
        {
            case "pause":
            case "resume":
                RegisterControlEvent(eventName);
                break;
            case "disconnect":
                string suffix = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
                RegisterControlEvent($"disconnect_{suffix}");
                break;
        }
    }

    private static long GetUnixTimeNs()
    {
        return (System.DateTime.UtcNow - System.DateTime.UnixEpoch).Ticks * 100L;
    }
}

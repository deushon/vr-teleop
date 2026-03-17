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

    [SerializeField] private Button sendRecordsButton;
    [SerializeField] private Button clearAllRecordsButton;
    [SerializeField] private TaskManager taskManager;

    [SerializeField] private TMP_Text IpText;
    [SerializeField] private TMP_Text PortText;
    [SerializeField] private string uploadDatasetPath = "/upload_dataset";

    [SerializeField] private AutoDestroyTMPText LogText;

    private List<GameObject> currentRecords = new List<GameObject>();

    //private IEnumerator Start()
    //{
    //    yield return new WaitForSeconds(1f);
    //    AddNewRecord();
    //    yield return null;
    //    AddNewRecord();
    //    yield return null;
    //    AddNewRecord();
    //    yield return null;
    //    AddNewRecord();
    //}

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
            keyboardManager.SilentInput(recordData.TextField, activeTaskData.TextField.text);
        }

        recordData.InputTextButton.onClick.AddListener(() =>
        {
            keyboardManager.Activate(recordData.TextField);
        });

        LayoutRebuilder.ForceRebuildLayoutImmediate(parentForLayout);
        currentRecords.Add(record);

        if (!sendRecordsButton.interactable || !clearAllRecordsButton.interactable)
        {
            sendRecordsButton.interactable = true;
            clearAllRecordsButton.interactable = true;
        }
    }
    public void DeleteRecord(GameObject record)
    {
        currentRecords.Remove(record);
        Destroy(record);
        if (currentRecords.Count == 0)
        {
            sendRecordsButton.interactable = false;
            clearAllRecordsButton.interactable = false;
        }
    }

    public void ClearAllRecords()
    {
        while (currentRecords.Count > 0)
        {
            Destroy(currentRecords[0]);
            currentRecords.RemoveAt(0);
        }
        sendRecordsButton.interactable = false;
        clearAllRecordsButton.interactable = false;
    }

    public void SendAllRecords()
    {
        StartCoroutine(SendAllRecordsCoroutine());
    }

    private IEnumerator SendAllRecordsCoroutine()
    {
        if (IpText == null || string.IsNullOrWhiteSpace(IpText.text))
        {
            Debug.LogError("Robot IP is empty");
            yield break;
        }

        string url = $"http://{IpText.text}:{PortText.text}{uploadDatasetPath}";

        var payload = new DatasetUploadRequest
        {
            source = "unity_quest_dataset",
            generatedUtcIso = System.DateTime.UtcNow.ToString("o")
        };

        foreach (var recordObj in currentRecords)
        {
            if (recordObj == null) continue;

            var recordData = recordObj.GetComponent<RecordData>();
            if (recordData == null) continue;

            payload.records.Add(new DatasetUploadRecord
            {
                recordId = recordData.GetRecordId(),
                label = recordData.GetLabel(),
                taskName = recordData.GetSelectedTaskName(),
                data = recordData.GetRecordedSession()
            });
        }

        string json = JsonConvert.SerializeObject(payload, Formatting.Indented);

        //File.WriteAllText("test.json", json);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(body);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        LogText.SetText($"[Dataset] Uploading {payload.records.Count} records to {url}");
        Debug.Log($"[Dataset] Uploading {payload.records.Count} records to {url}");

        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            var message = $"[Dataset] Upload success: {request.downloadHandler.text}";
            LogText.SetText(message);
            Debug.Log(message);
        }
        else
        {
            var message = $"[Dataset] Upload failed: {request.result}, {request.error}\nResponse: {request.downloadHandler.text}";
            LogText.SetText(message);
            Debug.LogError(message);
        }
    }
}
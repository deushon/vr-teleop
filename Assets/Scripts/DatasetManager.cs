using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class DatasetManager : MonoBehaviour
{
    [SerializeField] private RectTransform parentForLayout;
    [SerializeField] private GameObject recordUI;
    [SerializeField] private NumberInput keyboardManager;

    [SerializeField] private Button sendRecordsButton;
    [SerializeField] private Button clearAllRecordsButton;
    [SerializeField] private TaskManager taskManager;

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
}
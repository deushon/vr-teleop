using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class TaskManager : MonoBehaviour
{
    [SerializeField] private RectTransform parentForLayout;
    [SerializeField] private GameObject taskUI;

    [SerializeField] private Button clearAllTasksButton;
    [SerializeField] private RosbridgeImageSubscriber imageSubscriber;
    [SerializeField] private TeleopHelpRequestsManager helpRequestsManager;
    [SerializeField] private GameObject TaskPanel;
    [SerializeField] private TMP_Text TaskIDText;
    [SerializeField] private TMP_Text ErrorContextText;
    [SerializeField] private TMP_Text SituationReportText;

    private List<GameObject> currentTasks = new List<GameObject>();

    private TaskData activeSelection = null;

    public void AddNewTask(TeleopHelpRequestDto data)
    {
        if (taskUI == null)
        {
            Debug.LogError("No task prefab");
            return;
        }

        if (parentForLayout == null)
        {
            Debug.LogError("No parentForLayout assigned");
            return;
        }

        GameObject task = Instantiate(taskUI, parentForLayout, false);

        RectTransform taskTransform = task.GetComponent<RectTransform>();
        if (taskTransform == null)
        {
            Debug.LogError("No RectTransform was found");
            return;
        }

        taskTransform.localScale = Vector3.one;
        taskTransform.anchoredPosition = Vector2.zero;

        var taskData = task.GetComponent<TaskData>();
        if (taskData == null)
        {
            Debug.LogError("No TaskData was found");
            return;
        }

        taskData.SetupFromHelpRequest(data);
        taskData.TextField.text = data.payload.message;

        LayoutRebuilder.ForceRebuildLayoutImmediate(parentForLayout);
        currentTasks.Add(task);
        if (!clearAllTasksButton.interactable)
        {
            clearAllTasksButton.interactable = true;
        }
    }

    public void DeleteTask(GameObject task)
    {
        currentTasks.Remove(task);
        Destroy(task);
        if (currentTasks.Count == 0)
        {
            clearAllTasksButton.interactable = false;
        }
    }

    public void ClearAllTasks()
    {
        activeSelection = null;
        while (currentTasks.Count > 0)
        {
            Destroy(currentTasks[0]);
            currentTasks.RemoveAt(0);
        }
        clearAllTasksButton.interactable = false;
    }

    public void ChangeSelection(TaskData data)
    {
        if (activeSelection != null) 
        {
            if (activeSelection == data)
            {
                data.SetSelectionState(!data.CurrentSelectionState);
                activeSelection = null;
                return;
            }
            else
            {
                activeSelection.SetSelectionState(false);
            }
        }
        data.SetSelectionState(true);
        activeSelection = data;
        TaskPanel.SetActive(false);
        helpRequestsManager.AcceptHelpRequest(data.RequestId, sessionId =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Debug.LogWarning("Unable to get session id");
                return;
            }

            Debug.Log("Session ID: " + sessionId);

            TaskIDText.text = activeSelection.TaskId;
            ErrorContextText.text = activeSelection.ErrorContextJson;
            SituationReportText.text = activeSelection.SituationReport;

            activeSelection.SessionId = sessionId;
            imageSubscriber.InitConnection(sessionId);
        });
        
    }

    public TaskData GetActiveTaskData()
    {
        return activeSelection;
    }
}
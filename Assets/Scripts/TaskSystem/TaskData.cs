using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class TaskData : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text LabelTextField;
    public TMP_Text BodyTextField;
    public GameObject DropdownBody;
    public bool CurrentSelectionState = false;
    public Image DropdownImage;
    public Sprite openedDropdownIcon;
    private Sprite closedDropdownIcon;

    [Header("Help Request Data")]
    public string RequestId;
    public string RobotId;
    public string Status;
    public string Message;
    public string TaskId;
    [TextArea] public string ErrorContextJson;
    [TextArea] public string SituationReport;
    public string CreatedAtRaw;
    public string SessionId;

    private TaskManager taskManager;

    private bool isDropdownOpened = false;

    public DateTime? CreatedAtUtc
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CreatedAtRaw))
                return null;

            if (DateTime.TryParse(
                    CreatedAtRaw,
                    null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal |
                    System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var dt))
            {
                return dt;
            }

            return null;
        }
    }

    private void Start()
    {
        taskManager = FindFirstObjectByType<TaskManager>();

        if (DropdownImage != null)
            closedDropdownIcon = DropdownImage.sprite;
    }

    public void SetupFromHelpRequest(TeleopHelpRequestDto dto)
    {
        if (dto == null)
            return;

        RequestId = dto.id ?? string.Empty;
        RobotId = dto.robotId ?? string.Empty;
        Status = dto.status ?? string.Empty;

        Message = dto.payload?.message ?? string.Empty;
        TaskId = dto.payload?.metadata?.taskId ?? string.Empty;
        ErrorContextJson = dto.payload?.metadata?.errorContext ?? string.Empty;
        SituationReport = dto.payload?.metadata?.situationReport ?? string.Empty;
        CreatedAtRaw = dto.createdAt ?? string.Empty;

        UpdateUiText();
    }

    public void UpdateUiText()
    {
        if (LabelTextField == null)
            return;

        string createdText = CreatedAtUtc.HasValue
            ? CreatedAtUtc.Value.ToString("u")
            : CreatedAtRaw;

        LabelTextField.text =
            $"[{Status}] {Message}\n";
        BodyTextField.text =
            $"Task ID: {TaskId}\n" +
            $"Robot ID: {RobotId}\n" +
            $"Created: {createdText}";
    }

    public void DestroyRecord()
    {
        if (CurrentSelectionState)
        {
            ClearSelection();
        }

        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        taskManager.DeleteTask(gameObject);
    }

    public void ChangeDropdownCard()
    {
        isDropdownOpened = !isDropdownOpened;
        DropdownBody.SetActive(isDropdownOpened);

        if (DropdownImage != null)
            DropdownImage.sprite = isDropdownOpened ? openedDropdownIcon : closedDropdownIcon;

        LayoutRebuilder.ForceRebuildLayoutImmediate(GetComponent<RectTransform>());
        var parentTransform = transform.parent.GetComponent<RectTransform>();
        if (parentTransform)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(parentTransform);
        }
    }

    public void TakeTask()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        taskManager.SetActiveTask(this);
    }

    public void ClearSelection()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        taskManager.ClearSelection();
    }

    public void SetSelectionState(bool state)
    {
        CurrentSelectionState = state;
    }
}
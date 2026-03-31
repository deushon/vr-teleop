using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class TaskData : MonoBehaviour
{
    [Header("UI")]
    public TMP_Text TextField;
    public bool CurrentSelectionState = false;
    public Image SelectionImage;
    public Sprite ActiveSelectionIcon;
    private Sprite disabledSelectionIcon;

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

        if (SelectionImage != null)
            disabledSelectionIcon = SelectionImage.sprite;
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
        if (TextField == null)
            return;

        string createdText = CreatedAtUtc.HasValue
            ? CreatedAtUtc.Value.ToString("u")
            : CreatedAtRaw;

        TextField.text =
            $"[{Status}] {Message}\n" +
            $"Task ID: {TaskId}\n" +
            $"Robot ID: {RobotId}\n" +
            $"Created: {createdText}";
    }

    public void DestroyRecord()
    {
        if (CurrentSelectionState)
        {
            ChangeSelection();
        }

        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        taskManager.DeleteTask(gameObject);
    }

    public void ChangeSelection()
    {
        if (taskManager == null)
            taskManager = FindFirstObjectByType<TaskManager>();

        taskManager.ChangeSelection(this);
    }

    public void SetSelectionState(bool state)
    {
        CurrentSelectionState = state;

        if (SelectionImage != null)
            SelectionImage.sprite = state ? ActiveSelectionIcon : disabledSelectionIcon;
    }
}
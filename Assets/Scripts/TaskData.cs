using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class TaskData : MonoBehaviour
{
    public TMP_Text TextField;
    public bool CurrentSelectionState = false;
    public Image SelectionImage;
    public Sprite ActiveSelectionIcon;
    private Sprite disabledSelectionIcon;

    private TaskManager taskManager;

    private void Start()
    {
        taskManager = FindFirstObjectByType<TaskManager>();
        disabledSelectionIcon = SelectionImage.sprite;
    }

    public void DestroyRecord()
    {
        if (CurrentSelectionState)
        {
            ChangeSelection();
        }
        taskManager.DeleteTask(gameObject);
    }

    public void ChangeSelection()
    {
        taskManager.ChangeSelection(this);
    }

    public void SetSelectionState(bool state)
    {
        CurrentSelectionState = state;
        SelectionImage.sprite = state ? ActiveSelectionIcon : disabledSelectionIcon;
    }
}

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RecordData : MonoBehaviour
{
    public Button InputTextButton;
    public TMP_Text TextField;
    private TaskData selectedTask = null;

    public void DestroyRecord()
    {
        FindFirstObjectByType<DatasetManager>().DeleteRecord(gameObject);
    }

    public void SetSelectedTask(TaskData task)
    {
        selectedTask = task;
    }
}

using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RecordData : MonoBehaviour
{
    public Button InputTextButton;
    public TMP_Text TextField;

    private TaskData selectedTask = null;
    private RecordedSession recordedSession = null;

    public void DestroyRecord()
    {
        FindFirstObjectByType<DatasetManager>().DeleteRecord(gameObject);
    }

    public void SetSelectedTask(TaskData task)
    {
        selectedTask = task;
    }

    public void SetRecordedSession(RecordedSession session)
    {
        recordedSession = session;
    }

    public RecordedSession GetRecordedSession()
    {
        return recordedSession;
    }

    public string GetSelectedTaskName()
    {
        if (selectedTask == null || selectedTask.TextField == null)
            return string.Empty;

        return selectedTask.TextField.text;
    }

    public string GetLabel()
    {
        return TextField != null ? TextField.text : string.Empty;
    }
}
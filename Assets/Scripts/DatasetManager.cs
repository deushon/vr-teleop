using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class DatasetManager : MonoBehaviour
{
    [SerializeField] private RectTransform parentForLayout;
    [SerializeField] private GameObject recordUI;
    [SerializeField] private NumberInput keyboardManager;

    private IEnumerator Start()
    {
        yield return new WaitForSeconds(1f);
        AddNewRecord();
    }

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

        recordData.InputTextButton.onClick.AddListener(() =>
        {
            keyboardManager.Activate(recordData.TextField);
        });

        LayoutRebuilder.ForceRebuildLayoutImmediate(parentForLayout);
    }
}
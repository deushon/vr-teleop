using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class NumberInput : MonoBehaviour
{
    [SerializeField]
    private GameObject[] objectsToHide;

    [SerializeField]
    private GameObject numbersInput;

    private TMP_Text inputText;
    private DefaultTextValue textFields;

    public bool Lock = false;
    public bool NeedToSave = false;

    [SerializeField]
    private Color defaultTextColor = Color.gray;

    public void InputText(string symbol)
    {
        if (symbol != "backspace")
        {
            if (textFields.inputedValue == string.Empty)
            {
                inputText.text = string.Empty;
                inputText.color = Color.white;
            }
            if (inputText.text.Length < 15)
            {
                textFields.inputedValue += symbol;
                inputText.text = textFields.inputedValue;
            }
        }
        else
        {
            if (textFields.inputedValue.Length > 0)
            {
                textFields.inputedValue = textFields.inputedValue[..^1];
                inputText.text = textFields.inputedValue;
            }
            if (textFields.inputedValue == string.Empty)
            {
                inputText.text = textFields.defaultValue;
                inputText.color = defaultTextColor;
            }
        }
    }

    public void Activate(TMP_Text text)
    {
        if (Lock) return;
        inputText = text;
        textFields = inputText.GetComponent<DefaultTextValue>();
        ChangeState(true);
    }

    public void Deactivate()
    {
        inputText = null;
        ChangeState(false);
        if (NeedToSave)
        {
            FindFirstObjectByType<SaveData>().Save();
        }
    }

    private void ChangeState(bool state)
    {
        foreach (var obj in objectsToHide)
        {
            obj.SetActive(!state);
        }
        numbersInput.SetActive(state);
    }
}

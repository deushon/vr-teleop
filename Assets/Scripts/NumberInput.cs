using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class NumberInput : MonoBehaviour
{
    [SerializeField]
    private GameObject[] objectsToHide;

    [SerializeField]
    private GameObject[] objectsToShow;

    [SerializeField]
    private GameObject numbersInput;

    private TMP_Text inputText;
    private DefaultTextValue textFields;

    public bool Lock = false;
    public bool NeedToSave = false;
    public bool NeedToChangeColorInDefault = false;

    public int inputLimit = 15;

    [SerializeField]
    private Color defaultTextColor = Color.gray;
    [SerializeField]
    private Color activeTextColor = Color.white;
    [SerializeField]
    private Color disabledTextColor = Color.white;


    public void InputText(string symbol)
    {
        if (symbol != "backspace")
        {
            if (textFields.inputedValue == string.Empty)
            {
                inputText.text = string.Empty;
                inputText.color = activeTextColor;
            }
            if ((inputLimit > 0 && inputText.text.Length < inputLimit) || inputLimit == 0)
            {
                textFields.inputedValue += symbol;
                if (textFields.isPassword)
                {
                    inputText.text = new string('*', textFields.inputedValue.Length);
                }
                else
                {
                    inputText.text = textFields.inputedValue;
                }
            }
        }
        else
        {
            if (textFields.inputedValue.Length > 0)
            {
                textFields.inputedValue = textFields.inputedValue[..^1];
                if (textFields.isPassword)
                {
                    inputText.text = new string('*', textFields.inputedValue.Length);
                }
                else
                {
                    inputText.text = textFields.inputedValue;
                }
            }
            if (textFields.inputedValue == string.Empty)
            {
                inputText.text = textFields.defaultValue;
                if (!NeedToChangeColorInDefault)
                {
                    inputText.color = defaultTextColor;
                }
            }
        }
    }

    public void Activate(TMP_Text text)
    {
        if (Lock) return;
        if (inputText != null)
        {
            Deactivate();
        }
        inputText = text;
        textFields = inputText.GetComponent<DefaultTextValue>();
        if (NeedToChangeColorInDefault || inputText.text != textFields.defaultValue)
        {
            inputText.color = activeTextColor;
        }
        ChangeState(true);
    }

    public void SilentInput(TMP_Text text, string data)
    {
        if (data.Length == 0) return;
        var textFieldsLocal = text.GetComponent<DefaultTextValue>();
        textFieldsLocal.inputedValue += data;
        text.text = textFieldsLocal.inputedValue;
        text.color = disabledTextColor;
    }

    public void Deactivate()
    {
        if (inputText.text == textFields.defaultValue)
        {
            inputText.color = defaultTextColor;
        }
        else
        {
            inputText.color = disabledTextColor;
        }
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
        if (objectsToShow != null)
        {
            foreach (var obj in objectsToShow)
            {
                if (obj != null) obj.SetActive(state);
            }
        }
        numbersInput.SetActive(state);
    }
}

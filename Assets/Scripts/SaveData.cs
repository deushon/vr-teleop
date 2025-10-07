using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;

[System.Serializable]
public class IPData
{
    public string ip;
    public string port;
}

[System.Serializable]
public class DataForSave
{
    public IPData ipData;
}

public class SaveData : MonoBehaviour
{
    private string jsonPath; 
    [SerializeField]
    private DefaultTextValue IpText;
    [SerializeField]
    private DefaultTextValue PortText;

    private void Awake()
    {
        jsonPath =
            Path.Combine(Application.persistentDataPath, "data.json");
        Load();
    }

    public void Save()
    {
        IPData ipData = new()
        {
            ip = IpText.inputedValue,
            port = PortText.inputedValue
        };
        DataForSave data = new()
        {
            ipData = ipData
        };

        string json = JsonUtility.ToJson(data);
        File.WriteAllText(jsonPath, json);
    }

    public void Load()
    {
        if (!File.Exists(jsonPath)) return;
        DataForSave loadedData = 
            JsonUtility.FromJson<DataForSave>(File.ReadAllText(jsonPath));

        IpText.inputedValue = loadedData.ipData.ip;
        PortText.inputedValue = loadedData.ipData.port;

        if (IpText.inputedValue != "")
        {
            var ipTMP = IpText.GetComponent<TMP_Text>();
            ipTMP.text = IpText.inputedValue;
            ipTMP.color = Color.white;
        }
        if (PortText.inputedValue != "")
        {
            var portTMP = PortText.GetComponent<TMP_Text>();
            portTMP.text = PortText.inputedValue;
            portTMP.color = Color.white;
        }
    }
}

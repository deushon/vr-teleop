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
    public string restApiPort;
}

[System.Serializable]

public class AuthData
{
    public string username; 
    public string password;
}

[System.Serializable]
public class DataForSave
{
    public IPData ipData;
    public AuthData authData;
}

public class SaveData : MonoBehaviour
{
    private string jsonPath; 
    [SerializeField]
    private DefaultTextValue IpText;
    [SerializeField]
    private DefaultTextValue PortText;
    [SerializeField]
    private DefaultTextValue RestApiPortText;
    [SerializeField]
    private DefaultTextValue UsernameText;
    [SerializeField]
    private DefaultTextValue PasswordText;

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
            port = PortText.inputedValue, 
            restApiPort = RestApiPortText.inputedValue
        };
        AuthData authData = new()
        {
            username = UsernameText.inputedValue,
            password = PasswordText.inputedValue
        };
        DataForSave data = new()
        {
            ipData = ipData,
            authData = authData
        };

        string json = JsonUtility.ToJson(data);
        File.WriteAllText(jsonPath, json);
    }

    public void Load()
    {
        if (!File.Exists(jsonPath)) return;

        DataForSave loadedData =
            JsonUtility.FromJson<DataForSave>(File.ReadAllText(jsonPath));

        if (loadedData == null || loadedData.ipData == null || loadedData.authData == null)
            return;

        if (!string.IsNullOrEmpty(loadedData.ipData.ip))
        {
            IpText.inputedValue = loadedData.ipData.ip;
            var ipTMP = IpText.GetComponent<TMP_Text>();
            ipTMP.text = IpText.inputedValue;
            ipTMP.color = Color.white;
        }

        if (!string.IsNullOrEmpty(loadedData.ipData.port))
        {
            PortText.inputedValue = loadedData.ipData.port;
            var portTMP = PortText.GetComponent<TMP_Text>();
            portTMP.text = PortText.inputedValue;
            portTMP.color = Color.white;
        }

        if (!string.IsNullOrEmpty(loadedData.ipData.restApiPort))
        {
            RestApiPortText.inputedValue = loadedData.ipData.restApiPort;
            var portRestTMP = RestApiPortText.GetComponent<TMP_Text>();
            portRestTMP.text = RestApiPortText.inputedValue;
            portRestTMP.color = Color.white;
        }

        //TODO: Fix Save of auth data and change color method

        if (!string.IsNullOrEmpty(loadedData.authData.username))
        {
            UsernameText.inputedValue = loadedData.authData.username;
            var usernameTMP = UsernameText.GetComponent<TMP_Text>();
            usernameTMP.text = UsernameText.inputedValue;
            usernameTMP.color = new Color(48f / 255f, 48f / 255f, 48f / 255f);
        }

        if (!string.IsNullOrEmpty(loadedData.authData.password))
        {
            PasswordText.inputedValue = loadedData.authData.password;
            var passwordTMP = PasswordText.GetComponent<TMP_Text>();
            passwordTMP.text = new string('*', PasswordText.inputedValue.Length);
            passwordTMP.color = new Color(48f / 255f, 48f / 255f, 48f / 255f);
        }
    }
}

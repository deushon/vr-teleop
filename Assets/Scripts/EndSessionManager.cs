using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class EndSessionManager : MonoBehaviour
{
    [SerializeField]
    private RosbridgeImageSubscriber imageSubscriber;
    [SerializeField]
    private TeleopHelpRequestsManager telemetryManager;
    [SerializeField]
    private DatasetManager datasetManager;
    [SerializeField]
    private GameObject metaDataScreen;
    [SerializeField]
    private GameObject recordingWindow;
    [SerializeField]
    private Button endSessionButton;

    private int numOfSendTries = 0;

    public void TryToEndSession()
    {
        StartCoroutine(TryToEndSessionCoroutine());
    }

    private IEnumerator TryToEndSessionCoroutine()
    {
        endSessionButton.interactable = false;
        if (datasetManager.GetRecordSize() > 0)
        {
            yield return datasetManager.SendAllRecordsCoroutine();
            if (datasetManager.GetSendStatus() != 0)
            {
                datasetManager.LogText.SetText("Error while sending dataset, try again");
                numOfSendTries++;
                if (numOfSendTries < 3)
                {
                    endSessionButton.interactable = true;
                    yield break;
                }
                datasetManager.LogText.SetText("Closing connection without sending data");
                yield return new WaitForSeconds(1);
            }
            numOfSendTries = 0;
            datasetManager.ClearAllRecords();
        }
        imageSubscriber.StopConnection();
        telemetryManager.LoadHelpRequests();
        metaDataScreen.SetActive(true);
        recordingWindow.SetActive(false);
        endSessionButton.interactable = true;
        yield break;
    }
}

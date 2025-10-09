using UnityEngine;
using TMPro;
using System.Collections;

[RequireComponent(typeof(TMP_Text))]
public class AutoDestroyTMPText : MonoBehaviour
{
    public float autoDestroyDelay = 3f;

    private TMP_Text tmpText;
    private Coroutine destroyCoroutine;

    void Awake()
    {
        tmpText = GetComponent<TMP_Text>();
    }

    public void SetText(string newText)
    {
        if (tmpText == null)
            tmpText = GetComponent<TMP_Text>();

        tmpText.text += "\n" + newText;
        //RestartTimer();
    }

    private void RestartTimer()
    {
        if (destroyCoroutine != null)
        {
            StopCoroutine(destroyCoroutine);
            destroyCoroutine = null;
        }

        destroyCoroutine = StartCoroutine(DestroyAfterDelay());
    }

    private IEnumerator DestroyAfterDelay()
    {
        yield return new WaitForSeconds(autoDestroyDelay);

        tmpText.text = "";
    }
}

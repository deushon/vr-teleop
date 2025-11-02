using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI; // если используешь RawImage для UI
using WebSocketSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;

public class RosbridgeImageSubscriber : MonoBehaviour
{
    [Header("ROSBridge")]
    public string wsUrl = "ws://192.168.1.100:9090"; // замени на IP робота
    public string imageTopic = "/camera/image/compressed"; // часто так называется

    [Header("Target")]
    public RawImage targetUI;          // либо
    public Renderer targetRenderer;    // что-то одно из них

    [Header("Perf")]
    [Tooltip("Ставит throttle_rate (мс) в subscribe. 0 = без дросселя.")]
    public int subscribeThrottleMs = 0;
    [Tooltip("Пропуск кадров, если не успеваем декодировать (0 = не пропускать).")]
    public int maxQueueFrames = 1;

    private WebSocket ws;
    private Texture2D texture;
    private readonly ConcurrentQueue<byte[]> frameQueue = new();
    private readonly object textureLock = new();
    private Thread decodeThread;
    private volatile bool running;
    private byte[] latestDecoded; // JPEG/PNG -> байты исходного файла (не raw)
    private byte[] workingBuffer; // переисп. буфер под base64

    // Опционально: статистика
    private int receivedFrames;
    private int droppedFrames;
    private float lastStatTime;

    private bool currentConnectionState = false;
    [SerializeField]
    private GameObject EnableController;
    [SerializeField]
    private GameObject DisableController;
    [SerializeField]
    private GameObject DisconnectButton;
    [SerializeField]
    private GameObject PanelSettings;

    [SerializeField]
    private TMP_Text IpText;

    [SerializeField]
    private TMP_Text PortText;

    [SerializeField]
    private NumberInput numberInput;

    [SerializeField]
    private QuestRosPoseAndJointsPublisher publisher;
    public void InitConnection()
    {
        wsUrl = $"ws://{IpText.text}:{PortText.text}";
        numberInput.Lock = true;
        IpText.color = Color.gray;
        PortText.color = Color.gray;
        Connect();
        StartDecoderThread();
    }

    public void StopConnection()
    {
        publisher.Disconnect();
        SetState(false);
        numberInput.Lock = false;
        IpText.color = Color.white;
        PortText.color = Color.white;
        running = false;
        try { decodeThread?.Join(200); } catch { /* ignore */ }
        try { ws?.Close(); } catch { /* ignore */ }
        Destroy(texture);
    }

    void OnDestroy()
    {
        StopConnection();
    }

    void Connect()
    {
        ws = new WebSocket(wsUrl);
        // Если rosbridge поддерживает permessage-deflate — включай (снижает трафик)
        ws.Compression = WebSocketSharp.CompressionMethod.Deflate;

        ws.OnOpen += (s, e) =>
        {
            Debug.Log("[ROS] WebSocket opened");
            // Подписка на CompressedImage
            var sub = new
            {
                op = "subscribe",
                topic = imageTopic,
                type = "sensor_msgs/CompressedImage",
                throttle_rate = subscribeThrottleMs // миллисекунды, можно 33 для около 30 FPS
            };
            ws.Send(JsonConvert.SerializeObject(sub));
        };

        ws.OnMessage += (s, e) =>
        {
            try
            {
                // rosbridge шлёт JSON с msg.data (base64)
                var jo = JsonConvert.DeserializeObject<JObject>(e.Data);
                var msg = jo?["msg"];
                if (msg == null) return;
                var dataToken = msg["data"];
                if (dataToken == null) return;

                // data может быть строкой base64 или уже бинарём при BSON — но через ws обычно base64
                string b64 = dataToken.Value<string>();
                if (string.IsNullOrEmpty(b64)) return;

                // Быстрое декодирование base64 -> byte[]
                // Convert.FromBase64String создаёт новый массив; можно переисп. ArrayPool, но для простоты так:
                byte[] jpegBytes = Convert.FromBase64String(b64);

                receivedFrames++;

                // drop old frames если очередь забита
                while (frameQueue.Count >= maxQueueFrames && frameQueue.TryDequeue(out _))
                    droppedFrames++;

                frameQueue.Enqueue(jpegBytes);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ROS] Parse error: " + ex.Message);
            }
        };

        ws.OnError += (s, e) =>
        {
            Debug.LogWarning("[ROS] Error: " + e.Message);
            StopConnection();
        };
        ws.OnClose += (s, e) => Debug.LogWarning("[ROS] Closed: " + e.Reason);

        ws.ConnectAsync();
    }

    void StartDecoderThread()
    {
        running = true;
        decodeThread = new Thread(DecoderLoop) { IsBackground = true };
        decodeThread.Start();
    }

    void DecoderLoop()
    {
        // В этом потоке мы НИЧЕГО не делаем с Unity API.
        // Мы только берём последний JPEG/PNG из очереди и кладём его в latestDecoded.
        while (running)
        {
            if (!frameQueue.TryDequeue(out var encoded))
            {
                Thread.Sleep(1);
                continue;
            }
            // Просто держим последний принятый кадр (переступаем через медленные декоды)
            latestDecoded = encoded;
        }
    }

    void Update()
    {
        // Раз в кадр пробуем, есть ли новый JPEG/PNG для показа
        var toApply = latestDecoded;
        if (toApply == null || toApply.Length == 0) return;

        // Создаём/переиспользуем Texture2D
        if (texture == null)
        {
            texture = new Texture2D(2, 2, TextureFormat.RGB24, false, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            AssignTextureToTarget(texture);
        }

        // Важно: ImageConversion.LoadImage выполняет декод JPEG/PNG на CPU + загружает в Texture2D
        // Он должен вызываться на главном потоке Unity.
        bool ok = ImageConversion.LoadImage(texture, toApply, markNonReadable: true);
        if (ok && !currentConnectionState)
        {
            SetState(true);
        }
        else if (!ok && currentConnectionState) 
        {
            SetState(false);
        }
        if (!ok) return;


        latestDecoded = null;

        // Простейшая статистика (по желанию)
        if (Time.unscaledTime - lastStatTime > 2f)
        {
            Debug.Log($"[ROS] recv={receivedFrames} drop={droppedFrames} tex={texture.width}x{texture.height}");
            receivedFrames = droppedFrames = 0;
            lastStatTime = Time.unscaledTime;
        }
    }

    private void AssignTextureToTarget(Texture2D tex)
    {
        if (targetUI != null) targetUI.texture = tex;
        if (targetRenderer != null) targetRenderer.material.mainTexture = tex;
    }

    private void SetState(bool state)
    {
        currentConnectionState = state;
        targetUI.enabled = state;
        if (EnableController != null)
        {
            EnableController.SetActive(state);
        }
        if (DisconnectButton != null)
        {
            DisconnectButton.SetActive(state);
        }
        if (PanelSettings != null)
        {
            PanelSettings.SetActive(state);
        }
        if (DisableController != null)
        {
            DisableController.SetActive(state);
        }
    }
}

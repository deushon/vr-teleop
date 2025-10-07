using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;

public class RosbridgeImageSubscriber : MonoBehaviour
{
    [Header("ROSBridge")]
    public string wsUrl = "ws://192.168.1.100:9090"; 
    public string imageTopic = "/camera/image/compressed";

    [Header("Target")]
    public RawImage targetUI;
    public Renderer targetRenderer;

    [Header("Perf")]
    [Tooltip("Placed throttle_rate (ms) in subscribe. 0 = without drossel.")]
    public int subscribeThrottleMs = 0;
    [Tooltip("Number of skippable frames, if we don't have time to decode (0 = don't skip).")]
    public int maxQueueFrames = 1;

    private WebSocket ws;
    private Texture2D texture;
    private readonly ConcurrentQueue<byte[]> frameQueue = new();
    private readonly object textureLock = new();
    private Thread decodeThread;
    private volatile bool running;
    private byte[] latestDecoded;
    private byte[] workingBuffer;

    // Optional: stats
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
        ws.Compression = WebSocketSharp.CompressionMethod.Deflate;

        ws.OnOpen += (s, e) =>
        {
            Debug.Log("[ROS] WebSocket opened");
            var sub = new
            {
                op = "subscribe",
                topic = imageTopic,
                type = "sensor_msgs/CompressedImage",
                throttle_rate = subscribeThrottleMs
            };
            ws.Send(JsonConvert.SerializeObject(sub));
        };

        ws.OnMessage += (s, e) =>
        {
            try
            {
                var jo = JsonConvert.DeserializeObject<JObject>(e.Data);
                var msg = jo?["msg"];
                if (msg == null) return;
                var dataToken = msg["data"];
                if (dataToken == null) return;

                string b64 = dataToken.Value<string>();
                if (string.IsNullOrEmpty(b64)) return;

                byte[] jpegBytes = Convert.FromBase64String(b64);

                receivedFrames++;

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
        while (running)
        {
            if (!frameQueue.TryDequeue(out var encoded))
            {
                Thread.Sleep(1);
                continue;
            }
            latestDecoded = encoded;
        }
    }

    void Update()
    {
        var toApply = latestDecoded;
        if (toApply == null || toApply.Length == 0) return;

        if (texture == null)
        {
            texture = new Texture2D(2, 2, TextureFormat.RGB24, false, false);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Bilinear;
            AssignTextureToTarget(texture);
        }

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

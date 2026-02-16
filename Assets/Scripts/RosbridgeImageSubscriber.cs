using System;
using System.Collections;
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
    [Tooltip("throttle_rate (ms) in subscribe. 0 = without drossel.")]
    public int subscribeThrottleMs = 0;
    public int maxQueueFrames = 1;

    [Header("Resilience")]
    [Tooltip("Try to reconnect if loosing connection.")]
    public bool autoReconnect = true;
    [Tooltip("Pause before reconnection (sec).")]
    public float reconnectDelaySec = 2f;
    public float connectTimeoutSec = 10f;
    public float pingIntervalSec = 5f;

    private WebSocket ws;
    private Texture2D texture;
    private readonly ConcurrentQueue<byte[]> frameQueue = new();
    private Thread decodeThread;
    private volatile bool running;
    private volatile bool isConnecting;
    private volatile bool isConnected;
    private volatile bool isStopping;
    private volatile bool wantConnection; // (InitConnection -> true, StopConnection -> false)

    private byte[] latestDecoded;

    // Optional: stats
    private int receivedFrames;
    private int droppedFrames;
    private float lastStatTime;

    private bool currentConnectionState = false;

    [SerializeField] private GameObject EnableController;
    [SerializeField] private GameObject DisableController;
    [SerializeField] private GameObject DisconnectButton;
    [SerializeField] private GameObject PanelSettings;

    [SerializeField] private TMP_Text IpText;
    [SerializeField] private TMP_Text PortText;
    [SerializeField] private NumberInput numberInput;
    [SerializeField] private QuestRosPoseAndJointsPublisher publisher;

    [SerializeField]
    private AutoDestroyTMPText LogText;
    private float lastPingTime;

    // ======================== PUBLIC API ========================

    public void InitConnection()
    {
        try
        {
            LogText.SetText("[ROS] Starting Connection");
            wsUrl = $"ws://{IpText?.text}:{PortText?.text}";
            numberInput.Lock = true;
            if (IpText) IpText.color = Color.gray;
            if (PortText) PortText.color = Color.gray;

            wantConnection = true;
            EnsureDecoderThread();
            SafeConnect();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] InitConnection exception: {ex}");
            LogText.SetText($"[ROS] InitConnection exception: {ex}");
        }
    }

    public void StopConnection()
    {
        try
        {
            wantConnection = false;
            isStopping = true;

            try { publisher?.Disconnect(); } catch (Exception ex) 
            {
                Debug.LogWarning($"[ROS] publisher.Disconnect exception: {ex.Message}");
                LogText.SetText($"[ROS] publisher.Disconnect exception: {ex.Message}");
            }

            SetState(false);

            numberInput.Lock = false;
            if (IpText) IpText.color = Color.white;
            if (PortText) PortText.color = Color.white;

            running = false;

            try
            {
                if (decodeThread != null && decodeThread.IsAlive)
                {
                    if (!decodeThread.Join(300))
                        decodeThread.Interrupt(); 
                }
            }
            catch (Exception ex) 
            {
                Debug.LogWarning($"[ROS] decodeThread stop exception: {ex.Message}");
                LogText.SetText($"[ROS] decodeThread stop exception: {ex.Message}");
            }
            finally { decodeThread = null; }

            try
            {
                if (ws != null)
                {
                    UnsubscribeWsHandlers(ws);
                    ws.CloseAsync();
                    ws = null;
                }
            }
            catch (Exception ex) 
            {
                Debug.LogWarning($"[ROS] ws.Close exception: {ex.Message}");
                LogText.SetText($"[ROS] ws.Close exception: {ex.Message}");
            }

            try
            {
                while (frameQueue.TryDequeue(out _)) { }
                latestDecoded = null;
            }
            catch (Exception ex) 
            {
                Debug.LogWarning($"[ROS] queue clear exception: {ex.Message}");
                LogText.SetText($"[ROS] queue clear exception: {ex.Message}");
            }

            SafeDestroyTexture();

            LogText.SetText($"[ROS] Connection stopped");
            isConnected = false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] StopConnection exception: {ex}");
            LogText.SetText($"[ROS] StopConnection exception: {ex}");
        }
        finally
        {
            isStopping = false;
        }
    }

    // ======================== UNITY LIFECYCLE ========================

    private void OnDestroy()
    {
        StopConnection();
    }

    private void Update()
    {
        try
        {
            if (pingIntervalSec > 0f && isConnected && ws != null)
            {
                if (Time.unscaledTime - lastPingTime > pingIntervalSec)
                {
                    try
                    {
                        ws.Ping();
                        lastPingTime = Time.unscaledTime;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ROS] StopConnection exception: {ex}");
                        LogText.SetText($"[ROS] StopConnection exception: {ex}");
                    }
                }
            }

            var toApply = latestDecoded;
            if (toApply == null || toApply.Length == 0) return;

            if (texture == null)
            {
                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGB24, false, false)
                    {
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear
                    };
                    AssignTextureToTarget(texture);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ROS] Texture init failed: {ex.Message}");
                    LogText.SetText($"[ROS] Texture init failed: {ex.Message}");
                    latestDecoded = null;
                    return;
                }
            }

            bool ok = false;
            try
            {
                ok = ImageConversion.LoadImage(texture, toApply, markNonReadable: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] LoadImage exception: {ex.Message}");
                LogText.SetText($"[ROS] LoadImage exception: {ex.Message}");
                ok = false;
            }

            if (ok && !currentConnectionState)
                SetState(true);
            else if (!ok && currentConnectionState)
                SetState(false);

            if (!ok)
            {
                latestDecoded = null;
                return;
            }

            latestDecoded = null;

            if (Time.unscaledTime - lastStatTime > 2f)
            {
                Debug.Log($"[ROS] recv={receivedFrames} drop={droppedFrames} tex={texture.width}x{texture.height}");
                receivedFrames = droppedFrames = 0;
                lastStatTime = Time.unscaledTime;
            }

            if (autoReconnect && wantConnection && !isConnected && !isConnecting && !isStopping)
            {
                StartCoroutine(ReconnectAfterDelay(reconnectDelaySec));
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] Update exception: {ex}");
            LogText.SetText($"[ROS] Update exception: {ex}");
        }
    }

    // ======================== INTERNALS ========================

    private void EnsureDecoderThread()
    {
        try
        {
            if (decodeThread != null && decodeThread.IsAlive)
                return;

            running = true;
            decodeThread = new Thread(DecoderLoop) { IsBackground = true, Name = "ROS JPEG Decoder" };
            decodeThread.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] EnsureDecoderThread exception: {ex}");
            LogText.SetText($"[ROS] EnsureDecoderThread exception: {ex}");
        }
    }

    private void DecoderLoop()
    {
        try
        {
            while (running)
            {
                try
                {
                    if (!frameQueue.TryDequeue(out var encoded))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    latestDecoded = encoded;
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ROS] DecoderLoop iteration exception: {ex.Message}");
                    LogText.SetText($"[ROS] DecoderLoop iteration exception: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] DecoderLoop fatal exception: {ex}");
            LogText.SetText($"[ROS] DecoderLoop fatal exception: {ex}");
        }
    }

    private void SafeConnect()
    {
        if (isConnecting || isConnected || !wantConnection) return;

        try
        {
            ValidateWsUrl();

            try
            {
                if (ws != null)
                {
                    UnsubscribeWsHandlers(ws);
                    ws.CloseAsync();
                }
            }
            catch (Exception ex) 
            {
                Debug.LogWarning($"[ROS] Pre-close previous ws exception: {ex.Message}");
                LogText.SetText($"[ROS] Pre-close previous ws exception: {ex.Message}");
            }

            ws = new WebSocket(wsUrl)
            {
                Compression = CompressionMethod.Deflate,
                EmitOnPing = true
            };

            SubscribeWsHandlers(ws);

            isConnecting = true;
            lastPingTime = Time.unscaledTime;

            if (connectTimeoutSec > 0f)
                StartCoroutine(ConnectWithTimeout(connectTimeoutSec));
            else
                ws.ConnectAsync();
        }
        catch (Exception ex)
        {
            isConnecting = false;
            Debug.LogError($"[ROS] SafeConnect exception: {ex}");
            LogText.SetText($"[ROS] SafeConnect exception: {ex}");
            if (autoReconnect && wantConnection)
                StartCoroutine(ReconnectAfterDelay(reconnectDelaySec));
        }
    }

    private IEnumerator ConnectWithTimeout(float timeoutSec)
    {
        bool timedOut = false;
        float start = Time.unscaledTime;

        // Connection attempt ? extracted from try/catch with yield
        bool connectOk = true;
        try
        {
            ws.ConnectAsync();
        }
        catch (Exception ex)
        {
            isConnecting = false;
            connectOk = false;
            Debug.LogError($"[ROS] ConnectAsync threw: {ex}");
            LogText.SetText($"[ROS] ConnectAsync threw: {ex}");
        }

        if (!connectOk)
        {
            if (autoReconnect && wantConnection)
                yield return ReconnectAfterDelay(reconnectDelaySec);
            yield break;
        }

        while (isConnecting && !isConnected)
        {
            if (Time.unscaledTime - start > timeoutSec)
            {
                timedOut = true;
                break;
            }
            yield return null;
        }

        if (timedOut && ws != null)
        {
            Debug.LogWarning("[ROS] Connection timeout; closing and scheduling reconnect.");
            LogText.SetText("[ROS] Connection timeout; closing and scheduling reconnect.");

            try
            {
                ws.CloseAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] Close after timeout exception: {ex.Message}");
                LogText.SetText($"[ROS] Close after timeout exception: {ex.Message}");
            }

            isConnecting = false;
            isConnected = false;

            if (autoReconnect && wantConnection)
                yield return ReconnectAfterDelay(reconnectDelaySec);
        }
    }

    private IEnumerator ReconnectAfterDelay(float delay)
    {
        if (isConnecting || isConnected || !wantConnection) yield break;

        Debug.Log($"[ROS] Reconnecting in {delay:0.##} s...");
        LogText.SetText($"[ROS] Reconnecting in {delay:0.##} s...");
        yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, delay));

        if (!wantConnection) yield break;
        SafeConnect();
    }

    private void SubscribeWsHandlers(WebSocket socket)
    {
        socket.OnOpen += OnWsOpen;
        socket.OnMessage += OnWsMessage;
        socket.OnError += OnWsError;
        socket.OnClose += OnWsClose;
    }

    private void UnsubscribeWsHandlers(WebSocket socket)
    {
        socket.OnOpen -= OnWsOpen;
        socket.OnMessage -= OnWsMessage;
        socket.OnError -= OnWsError;
        socket.OnClose -= OnWsClose;
    }

    // ======================== WS HANDLERS ========================

    private void OnWsOpen(object sender, EventArgs e)
    {
        try
        {
            isConnected = true;
            isConnecting = false;
            Debug.Log("[ROS] WebSocket opened");
            var sub = new
            {
                op = "subscribe",
                topic = imageTopic,
                type = "sensor_msgs/CompressedImage",
                throttle_rate = Mathf.Max(0, subscribeThrottleMs)
            };

            string payload = JsonConvert.SerializeObject(sub);
            ws?.Send(payload);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnOpen exception: {ex}");
            LogText.SetText($"[ROS] OnOpen exception: {ex}");
        }
    }

    private void OnWsMessage(object sender, MessageEventArgs e)
    {
        try
        {
            if (!e.IsText)
            {
                Debug.LogWarning("[ROS] Non-text message received; ignoring.");
                LogText.SetText("[ROS] Non-text message received; ignoring.");
                return;
            }

            JObject jo = null;
            try
            {
                jo = JsonConvert.DeserializeObject<JObject>(e.Data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] JSON parse error: {ex.Message}");
                LogText.SetText($"[ROS] JSON parse error: {ex.Message}");
                return;
            }

            var msg = jo?["msg"];
            if (msg == null) return;

            var dataToken = msg["data"];
            if (dataToken == null) return;

            string b64 = dataToken.Value<string>();
            if (string.IsNullOrEmpty(b64)) return;

            byte[] jpegBytes = null;
            try
            {
                jpegBytes = Convert.FromBase64String(b64);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] Base64 decode error: {ex.Message}");
                LogText.SetText($"[ROS] Base64 decode error: {ex.Message}");
                return;
            }

            receivedFrames++;

            if (maxQueueFrames > 0)
            {
                while (frameQueue.Count >= maxQueueFrames && frameQueue.TryDequeue(out _))
                    droppedFrames++;
            }

            frameQueue.Enqueue(jpegBytes);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnMessage exception: {ex}");
            LogText.SetText($"[ROS] OnMessage exception: {ex}");
        }
    }

    private void OnWsError(object sender, ErrorEventArgs e)
    {
        try
        {
            Debug.LogWarning($"[ROS] WS Error: {e.Message}");
            LogText.SetText($"[ROS] WS Error: {e.Message}");
            isConnected = false;
            isConnecting = false;

            if (!isStopping)
                SetState(false);

            if (autoReconnect && wantConnection && !isStopping)
                StartCoroutine(ReconnectAfterDelay(reconnectDelaySec));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnError exception: {ex}");
            LogText.SetText($"[ROS] OnError exception: {ex}");
        }
    }

    private void OnWsClose(object sender, CloseEventArgs e)
    {
        try
        {
            Debug.LogWarning($"[ROS] WS Closed: code={e.Code}, reason={e.Reason}, clean={e.WasClean}");
            LogText.SetText($"[ROS] WS Closed: code={e.Code}, reason={e.Reason}, clean={e.WasClean}");
            isConnected = false;
            isConnecting = false;

            if (!isStopping)
                SetState(false);

            if (autoReconnect && wantConnection && !isStopping)
                StartCoroutine(ReconnectAfterDelay(reconnectDelaySec));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnClose exception: {ex}");
            LogText.SetText($"[ROS] OnClose exception: {ex}");
        }
    }

    // ======================== HELPERS ========================

    private void ValidateWsUrl()
    {
        if (string.IsNullOrWhiteSpace(wsUrl))
            throw new ArgumentException("wsUrl is empty.");
        if (!wsUrl.StartsWith("ws://") && !wsUrl.StartsWith("wss://"))
            throw new ArgumentException($"wsUrl must start with ws:// or wss://, got: {wsUrl}");

        try
        {
            var uri = new Uri(wsUrl);
            if (uri.Port <= 0 || uri.Port > 65535)
            {
                LogText.SetText($"Invalid port in wsUrl: {uri.Port}");
                throw new ArgumentException($"Invalid port in wsUrl: {uri.Port}");
            }

        }
        catch (UriFormatException ex)
        {
            LogText.SetText($"Invalid wsUrl format: {wsUrl}. {ex.Message}");
            throw new ArgumentException($"Invalid wsUrl format: {wsUrl}. {ex.Message}");
        }
    }

    private void AssignTextureToTarget(Texture2D tex)
    {
        try
        {
            if (targetUI != null) targetUI.texture = tex;
            if (targetRenderer != null)
            {
                var mr = targetRenderer.material;
                if (mr != null)
                    mr.mainTexture = tex;
            }
        }
        catch (Exception ex)
        {
            LogText.SetText($"[ROS] AssignTextureToTarget exception: {ex.Message}");
            Debug.LogWarning($"[ROS] AssignTextureToTarget exception: {ex.Message}");
        }
    }

    private void SafeDestroyTexture()
    {
        try
        {
            if (texture != null)
            {
                if (Application.isPlaying)
                    Destroy(texture);
                else
                    DestroyImmediate(texture);
                texture = null;
            }
        }
        catch (Exception ex)
        {
            LogText.SetText($"[ROS] SafeDestroyTexture exception: {ex.Message}");
            Debug.LogWarning($"[ROS] SafeDestroyTexture exception: {ex.Message}");
        }
    }

    private void SetState(bool state)
    {
        try
        {
            currentConnectionState = state;

            if (targetUI) targetUI.enabled = state;

            if (EnableController) EnableController.SetActive(state);
            if (DisconnectButton) DisconnectButton.SetActive(state);
            if (PanelSettings) PanelSettings.SetActive(state);
            if (DisableController) DisableController.SetActive(state);
        }
        catch (Exception ex)
        {
            LogText.SetText($"[ROS] SetState exception: {ex.Message}");
            Debug.LogWarning($"[ROS] SetState exception: {ex.Message}");
        }
    }
}

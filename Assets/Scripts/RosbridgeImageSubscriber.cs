using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using WebSocketSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using System.Threading.Tasks;

public class RosbridgeImageSubscriber : MonoBehaviour
{
    [Header("ROSBridge")]
    public string wsUrl = "ws://192.168.1.100:9090";
    public string imageTopic = "/camera/image/compressed";

    [Header("Battery")]
    public string batteryTopic = "/ros_robot_controller/battery";
    public TMP_Text batteryText;
    public Image batteryIcon;

    //[Header("Task")]
    //public string taskTopic = "/task";
    //[SerializeField] private TaskManager taskManager;

    [Header("Target")]
    public RawImage targetUI;
    public Renderer targetRenderer;

    [Header("Video")]
    [Tooltip("Maximum rate of applying decoded JPEG frames to Unity texture.")]
    public float maxApplyFps = 15f;

    [Header("Perf")]
    [Tooltip("throttle_rate (ms) in ROS subscribe for image topic. 0 = no throttle.")]
    public int subscribeThrottleMs = 0;

    [Header("Resilience")]
    [Tooltip("Try to reconnect if connection is lost.")]
    public bool autoReconnect = true;
    [Tooltip("Pause before reconnection (sec).")]
    public float reconnectDelaySec = 2f;
    public float connectTimeoutSec = 10f;
    public float pingIntervalSec = 5f;


    [Header("RTT Monitor")]
    public bool enableRttMonitor = true;

    [Tooltip("How often to measure rosbridge RTT.")]
    public float rttMeasureIntervalSec = 0.5f;

    [Tooltip("Threshold used for UI state and control precheck.")]
    public int rttBlockThresholdMs = 200;

    [Tooltip("Artificial RTT offset for testing.")]
    public int debugArtificialRttOffsetMs = 0;

    private volatile bool rttProbeInFlight;
    private volatile int lastRttProbeToken;

    [SerializeField] private TMP_Text pingText;

    [Header("Image Stream Watchdog")]
    [Tooltip("How long to wait for the first image frame after subscribe.")]
    public float firstImageTimeoutSec = 3f;

    [Tooltip("How long stream may stay silent after frames had already arrived.")]
    public float imageSilenceTimeoutSec = 5f;

    [Tooltip("How many times to retry image topic subscription before reconnecting whole socket.")]
    public int maxImageResubscribeAttempts = 2;

    [Tooltip("Try to recover image stream automatically.")]
    public bool autoRecoverImageStream = true;

    //[SerializeField] private GameObject ShowDashboard;
    [SerializeField] private GameObject CameraPanelSettings;
    [SerializeField] private GameObject RecordPanel;
    [SerializeField] private GameObject TaskPanel;
    [SerializeField] private SoftHeadFollower DashboardHeadFollower;
    [SerializeField] private GameObject LogoutButton;
    //[SerializeField] private GameObject DashboardPanelSettings;
    //[SerializeField] private GameObject DisableControlButton;
    //[SerializeField] private GameObject PanelSettings;

    [SerializeField] private TMP_Text IpText;
    [SerializeField] private TMP_Text PortText;
    [SerializeField] private NumberInput numberInput;
    [SerializeField] private QuestRosPoseAndJointsPublisher publisher;
    [SerializeField] private AutoDestroyTMPText LogText;

    private WebSocket ws;
    private Texture2D texture;

    private readonly ConcurrentQueue<Action> mainThreadActions = new();
    private readonly Dictionary<string, TopicSubscription> subscriptions = new();

    private readonly Dictionary<string, List<Action<JToken>>> externalTopicHandlers = new();

    private volatile bool isConnecting;
    private volatile bool isConnected;
    private volatile bool isStopping;
    private volatile bool wantConnection;
    private volatile bool isDestroyedOrQuitting;

    private volatile ushort latestBatteryValue;
    private volatile bool hasBatteryValue;

    private byte[] latestEncodedFrame;
    private readonly object latestFrameLock = new();

    private int receivedFrames;
    private int droppedFrames;
    private int replacedFrames;
    private float lastStatTime;
    private float lastPingTime;
    private float lastFrameApplyTime;

    private bool currentConnectionState = false;
    private bool reconnectCoroutineScheduled = false;

    private volatile bool hasReceivedAnyImageFrame;
    private volatile bool waitingForFirstImage;
    private volatile float lastImageMessageTime;

    private Coroutine imageStartupWatchdogCoroutine;
    private Coroutine imageSilenceWatchdogCoroutine;

    private int imageResubscribeAttempts;

    private Coroutine rttMonitorCoroutine;

    private volatile int lastMeasuredRttMs = -1;
    private volatile bool hasValidRttSample;
    private volatile bool rttAboveThreshold;
    private volatile float lastRttMeasureTime;

    public bool TryGetCurrentRttMs(out int rttMs)
    {
        if (hasValidRttSample)
        {
            rttMs = lastMeasuredRttMs;
            return true;
        }

        rttMs = -1;
        return false;
    }

    public int CurrentRttMs => lastMeasuredRttMs;
    public bool HasValidRttSample => hasValidRttSample;
    public bool IsRttAboveThreshold => rttAboveThreshold;
    public bool IsRosbridgeConnected => isConnected;

    public void SetArtificialRttOffsetMs(int value)
    {
        debugArtificialRttOffsetMs = Mathf.Max(0, value);
    }

    private sealed class TopicSubscription
    {
        public string Topic;
        public string RosType;
        public Action<JToken> Handler;
        public int ThrottleRateMs;
    }

    // ======================== UNITY LIFECYCLE ========================

    private void Awake()
    {
        RegisterSubscriptions();
    }

    private void OnApplicationQuit()
    {
        isDestroyedOrQuitting = true;
    }

    private void OnDestroy()
    {
        isDestroyedOrQuitting = true;
        StopConnection();
    }

    private void Update()
    {
        FlushMainThreadActions();

        try
        {
            if (isDestroyedOrQuitting) return;

            UpdateBatteryUi();
            ApplyLatestFrameIfNeeded();
            PrintStatsIfNeeded();
            ScheduleReconnectIfNeeded();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] Update exception: {ex}");
            SafeLog($"[ROS] Update exception: {ex.Message}");
        }
    }

    // ======================== PUBLIC API ========================

    public void InitConnection(string sessionId)
    {
        try
        {
            if (isDestroyedOrQuitting) return;

            RegisterSubscriptions();

            SafeLog("[ROS] Starting connection");

            string ip = IpText ? IpText.text : "";
            string port = PortText ? PortText.text : "";
            wsUrl = $"ws://{ip}:{port}/ws/teleop/session/{sessionId}?token={TeleopAuthSession.AccessToken}";

            if (numberInput) numberInput.Lock = true;
            if (IpText) IpText.color = Color.gray;
            if (PortText) PortText.color = Color.gray;

            wantConnection = true;
            SafeConnect();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] InitConnection exception: {ex}");
            SafeLog($"[ROS] InitConnection exception: {ex.Message}");
        }
    }

    public void StopConnection()
    {
        try
        {
            if (rttMonitorCoroutine != null)
            {
                StopCoroutine(rttMonitorCoroutine);
                rttMonitorCoroutine = null;
            }

            hasValidRttSample = false;
            rttAboveThreshold = false;
            lastMeasuredRttMs = -1;
            lastRttMeasureTime = 0f;

            UpdatePingUi();

            StopImageWatchdogs();
            waitingForFirstImage = false;
            hasReceivedAnyImageFrame = false;
            imageResubscribeAttempts = 0;

            wantConnection = false;
            isStopping = true;
            reconnectCoroutineScheduled = false;

            try
            {
                publisher?.Disconnect();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] publisher.Disconnect exception: {ex.Message}");
                SafeLog($"[ROS] publisher.Disconnect exception: {ex.Message}");
            }

            if (!isDestroyedOrQuitting)
            {
                SetState(false);

                if (numberInput) numberInput.Lock = false;
                if (IpText) IpText.color = Color.white;
                if (PortText) PortText.color = Color.white;
            }

            isConnected = false;
            isConnecting = false;

            try
            {
                var socket = ws;
                ws = null;

                if (socket != null)
                {
                    UnsubscribeWsHandlers(socket);
                    socket.CloseAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] ws.Close exception: {ex.Message}");
                SafeLog($"[ROS] ws.Close exception: {ex.Message}");
            }

            ClearLatestFrame();
            SafeDestroyTexture();

            if (!isDestroyedOrQuitting)
                SafeLog("[ROS] Connection stopped");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] StopConnection exception: {ex}");
            SafeLog($"[ROS] StopConnection exception: {ex.Message}");
        }
        finally
        {
            isStopping = false;
        }
    }

    public bool TryGetBatteryValue(out ushort value)
    {
        if (hasBatteryValue)
        {
            value = latestBatteryValue;
            return true;
        }

        value = 0;
        return false;
    }

    public ushort BatteryValue => latestBatteryValue;
    public bool HasBatteryValue => hasBatteryValue;

    // ======================== SUBSCRIPTIONS ========================

    private void RegisterSubscriptions()
    {
        subscriptions.Clear();

        RegisterSubscription(
            imageTopic,
            "sensor_msgs/CompressedImage",
            HandleImageMessage,
            subscribeThrottleMs < 0 ? 0 : subscribeThrottleMs
        );

        RegisterSubscription(
            batteryTopic,
            "std_msgs/UInt16",
            HandleBatteryMessage
        );
        //RegisterSubscription(
        //    taskTopic,
        //    "std_msgs/String",
        //    HandleTaskMessage
        //);
    }

    private void RegisterSubscription(string topic, string rosType, Action<JToken> handler, int throttleRateMs = 0)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            Debug.LogWarning("[ROS] Tried to register empty topic.");
            return;
        }

        if (handler == null)
        {
            Debug.LogWarning($"[ROS] Tried to register topic '{topic}' with null handler.");
            return;
        }

        subscriptions[topic] = new TopicSubscription
        {
            Topic = topic,
            RosType = rosType,
            Handler = handler,
            ThrottleRateMs = Mathf.Max(0, throttleRateMs)
        };
    }

    private void SendSubscribe(TopicSubscription sub)
    {
        if (ws == null || sub == null) return;

        object payload;
        if (sub.ThrottleRateMs > 0)
        {
            payload = new
            {
                op = "subscribe",
                topic = sub.Topic,
                type = sub.RosType,
                throttle_rate = sub.ThrottleRateMs
            };
        }
        else
        {
            payload = new
            {
                op = "subscribe",
                topic = sub.Topic,
                type = sub.RosType
            };
        }

        string json = JsonConvert.SerializeObject(payload);
        ws.Send(json);

        Debug.Log($"[ROS] Subscribed to topic: {sub.Topic} ({sub.RosType})");
        SafeLog($"[ROS] Subscribed to topic: {sub.Topic}");
    }

    private void SendUnsubscribe(string topic)
    {
        if (ws == null || string.IsNullOrWhiteSpace(topic)) return;

        var payload = new
        {
            op = "unsubscribe",
            topic = topic
        };

        string json = JsonConvert.SerializeObject(payload);
        ws.Send(json);

        Debug.Log($"[ROS] Unsubscribed from topic: {topic}");
        SafeLog($"[ROS] Unsubscribed from topic: {topic}");
    }

    // ======================== MAIN THREAD DISPATCH ========================

    private void RunOnMainThread(Action action)
    {
        if (action == null || isDestroyedOrQuitting) return;
        mainThreadActions.Enqueue(action);
    }

    private void FlushMainThreadActions()
    {
        while (mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ROS] MainThread action exception: {ex}");
            }
        }
    }

    private void SafeLog(string message)
    {
        if (isDestroyedOrQuitting) return;

        RunOnMainThread(() =>
        {
            try
            {
                if (isDestroyedOrQuitting) return;
                if (!this) return;
                if (!LogText) return;

                LogText.SetText(message);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] SafeLog exception: {ex.Message}");
            }
        });
    }

    private void ScheduleReconnectFromAnyThread()
    {
        if (!autoReconnect || !wantConnection || isStopping || isDestroyedOrQuitting)
            return;

        RunOnMainThread(() =>
        {
            if (!this || isDestroyedOrQuitting) return;
            if (!autoReconnect || !wantConnection || isStopping || isConnected || isConnecting) return;
            if (reconnectCoroutineScheduled) return;

            reconnectCoroutineScheduled = true;
            StartCoroutine(ReconnectAfterDelay(reconnectDelaySec));
        });
    }

    private void SetDisconnectedStateFromAnyThread()
    {
        RunOnMainThread(() =>
        {
            if (!this || isDestroyedOrQuitting) return;
            if (!isStopping)
                SetState(false);
        });
    }

    // ======================== CONNECTION ========================

    private void SafeConnect()
    {
        if (isConnecting || isConnected || !wantConnection || isDestroyedOrQuitting) return;

        try
        {
            ValidateWsUrl();

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
                Debug.LogWarning($"[ROS] Pre-close previous ws exception: {ex.Message}");
                SafeLog($"[ROS] Pre-close previous ws exception: {ex.Message}");
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
            SafeLog($"[ROS] SafeConnect exception: {ex.Message}");
            ScheduleReconnectFromAnyThread();
        }
    }

    private IEnumerator ConnectWithTimeout(float timeoutSec)
    {
        bool connectOk = true;
        float start = Time.unscaledTime;

        try
        {
            ws?.ConnectAsync();
        }
        catch (Exception ex)
        {
            isConnecting = false;
            connectOk = false;
            Debug.LogError($"[ROS] ConnectAsync threw: {ex}");
            SafeLog($"[ROS] ConnectAsync threw: {ex.Message}");
        }

        if (!connectOk)
        {
            reconnectCoroutineScheduled = false;
            if (autoReconnect && wantConnection && !isDestroyedOrQuitting)
            {
                reconnectCoroutineScheduled = true;
                yield return ReconnectAfterDelay(reconnectDelaySec);
            }
            yield break;
        }

        while (isConnecting && !isConnected && !isDestroyedOrQuitting)
        {
            if (Time.unscaledTime - start > timeoutSec)
                break;

            yield return null;
        }

        if (isDestroyedOrQuitting)
        {
            reconnectCoroutineScheduled = false;
            yield break;
        }

        if (isConnecting && !isConnected && ws != null)
        {
            Debug.LogWarning("[ROS] Connection timeout; closing and scheduling reconnect.");
            SafeLog("[ROS] Connection timeout; closing and scheduling reconnect.");

            try
            {
                ws.CloseAsync();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] Close after timeout exception: {ex.Message}");
                SafeLog($"[ROS] Close after timeout exception: {ex.Message}");
            }

            isConnecting = false;
            isConnected = false;

            reconnectCoroutineScheduled = false;

            if (autoReconnect && wantConnection)
            {
                reconnectCoroutineScheduled = true;
                yield return ReconnectAfterDelay(reconnectDelaySec);
            }
        }
        else
        {
            reconnectCoroutineScheduled = false;
        }
    }

    private IEnumerator ReconnectAfterDelay(float delay)
    {
        if (isDestroyedOrQuitting)
        {
            reconnectCoroutineScheduled = false;
            yield break;
        }

        if (isConnecting || isConnected || !wantConnection)
        {
            reconnectCoroutineScheduled = false;
            yield break;
        }

        Debug.Log($"[ROS] Reconnecting in {delay:0.##} s...");
        SafeLog($"[ROS] Reconnecting in {delay:0.##} s...");

        yield return new WaitForSecondsRealtime(Mathf.Max(0.05f, delay));

        reconnectCoroutineScheduled = false;

        if (isDestroyedOrQuitting || !wantConnection || isConnecting || isConnected)
            yield break;

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
            SafeLog("[ROS] WebSocket opened");

            foreach (var pair in subscriptions)
            {
                var sub = pair.Value;
                SendSubscribe(sub);
            }

            RunOnMainThread(() =>
            {
                if (!this || isDestroyedOrQuitting) return;

                hasReceivedAnyImageFrame = false;
                waitingForFirstImage = true;
                imageResubscribeAttempts = 0;
                lastImageMessageTime = Time.unscaledTime;

                if (rttMonitorCoroutine != null)
                {
                    StopCoroutine(rttMonitorCoroutine);
                    rttMonitorCoroutine = null;
                }

                hasValidRttSample = false;
                rttAboveThreshold = false;
                lastMeasuredRttMs = -1;
                lastRttMeasureTime = 0f;

                if (enableRttMonitor)
                    rttMonitorCoroutine = StartCoroutine(RttMonitorLoop());

                RestartImageWatchdogs();
            });
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnOpen exception: {ex}");
            SafeLog($"[ROS] OnOpen exception: {ex.Message}");
        }
    }

    private void OnWsMessage(object sender, MessageEventArgs e)
    {
        try
        {
            if (!e.IsText)
            {
                Debug.LogWarning("[ROS] Non-text message received; ignoring.");
                SafeLog("[ROS] Non-text message received; ignoring.");
                return;
            }

            JObject jo;
            try
            {
                jo = JsonConvert.DeserializeObject<JObject>(e.Data);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] JSON parse error: {ex.Message}");
                SafeLog($"[ROS] JSON parse error: {ex.Message}");
                return;
            }

            if (jo == null)
                return;

            string topic = jo["topic"]?.Value<string>();
            var msg = jo["msg"];

            if (msg == null || string.IsNullOrWhiteSpace(topic))
                return;

            if (subscriptions.TryGetValue(topic, out var subscription))
            {
                try
                {
                    subscription.Handler?.Invoke(msg);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ROS] Handler exception for topic '{topic}': {ex.Message}");
                    SafeLog($"[ROS] Handler exception for topic '{topic}': {ex.Message}");
                }
            }

            if (externalTopicHandlers.TryGetValue(topic, out var handlers))
            {
                var handlersCopy = new List<Action<JToken>>(handlers);
                var msgCopy = msg?.DeepClone();

                RunOnMainThread(() =>
                {
                    for (int i = 0; i < handlersCopy.Count; i++)
                    {
                        try
                        {
                            handlersCopy[i]?.Invoke(msgCopy);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[ROS] External handler exception for topic '{topic}': {ex.Message}");
                        }
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnMessage exception: {ex}");
            SafeLog($"[ROS] OnMessage exception: {ex.Message}");
        }
    }

    private void OnWsError(object sender, ErrorEventArgs e)
    {
        try
        {
            Debug.LogWarning($"[ROS] WS Error: {e.Message}");
            SafeLog($"[ROS] WS Error: {e.Message}");

            isConnected = false;
            isConnecting = false;

            SetDisconnectedStateFromAnyThread();
            ScheduleReconnectFromAnyThread();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnError exception: {ex}");
            SafeLog($"[ROS] OnError exception: {ex.Message}");
        }
    }

    private void OnWsClose(object sender, CloseEventArgs e)
    {
        try
        {
            Debug.LogWarning($"[ROS] WS Closed: code={e.Code}, reason={e.Reason}, clean={e.WasClean}");
            SafeLog($"[ROS] WS Closed: code={e.Code}, reason={e.Reason}, clean={e.WasClean}");

            isConnected = false;
            isConnecting = false;

            SetDisconnectedStateFromAnyThread();
            ScheduleReconnectFromAnyThread();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ROS] OnClose exception: {ex}");
            SafeLog($"[ROS] OnClose exception: {ex.Message}");
        }
    }

    // ======================== MESSAGE HANDLERS ========================

    private void HandleImageMessage(JToken msg)
    {
        var dataToken = msg["data"];
        if (dataToken == null) return;

        string b64 = dataToken.Value<string>();
        if (string.IsNullOrEmpty(b64)) return;

        byte[] jpegBytes;
        try
        {
            jpegBytes = Convert.FromBase64String(b64);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ROS] Base64 decode error: {ex.Message}");
            SafeLog($"[ROS] Base64 decode error: {ex.Message}");
            return;
        }

        Interlocked.Increment(ref receivedFrames);

        lock (latestFrameLock)
        {
            if (latestEncodedFrame != null)
                Interlocked.Increment(ref replacedFrames);

            latestEncodedFrame = jpegBytes;
        }
    }

    private void HandleBatteryMessage(JToken msg)
    {
        var dataToken = msg["data"];
        if (dataToken == null) return;

        try
        {
            ushort value = dataToken.Value<ushort>();
            latestBatteryValue = value;
            hasBatteryValue = true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ROS] Battery parse error: {ex.Message}");
            SafeLog($"[ROS] Battery parse error: {ex.Message}");
        }
    }

    //private void HandleTaskMessage(JToken msg)
    //{
    //    var dataToken = msg["data"];
    //    if (dataToken == null) return;

    //    string taskText;
    //    try
    //    {
    //        taskText = dataToken.Value<string>();
    //    }
    //    catch (Exception ex)
    //    {
    //        Debug.LogWarning($"[ROS] Task parse error: {ex.Message}");
    //        SafeLog($"[ROS] Task parse error: {ex.Message}");
    //        return;
    //    }

    //    if (string.IsNullOrWhiteSpace(taskText))
    //        return;

    //    RunOnMainThread(() =>
    //    {
    //        try
    //        {
    //            if (isDestroyedOrQuitting) return;
    //            if (!this) return;

    //            if (taskManager == null)
    //            {
    //                Debug.LogWarning("[ROS] TaskManager is not assigned.");
    //                SafeLog("[ROS] TaskManager is not assigned.");
    //                return;
    //            }
    //            SafeLog($"[ROS] New task received: {taskText}");
    //            taskManager.AddNewTask(taskText);
    //        }
    //        catch (Exception ex)
    //        {
    //            Debug.LogWarning($"[ROS] TaskManager.AddNewTask exception: {ex.Message}");
    //            SafeLog($"[ROS] TaskManager.AddNewTask exception: {ex.Message}");
    //        }
    //    });
    //}

    // ======================== UPDATE STEPS ========================

    private void UpdateBatteryUi()
    {
        if (hasBatteryValue && batteryText)
        {
            float batteryVolt = latestBatteryValue / 1000f;
            batteryText.text = $"{batteryVolt:F2}V";
            if (batteryVolt >= 12f)
            {
                batteryText.color = new Color(0, 104f / 255f, 0);
                batteryIcon.color = new Color(0, 104f / 255f, 0);
            }
            else if (batteryVolt >= 11f)
            {
                batteryText.color = Color.yellow;
                batteryIcon.color = Color.yellow;
            }
            else
            {
                batteryText.color = Color.red;
                batteryIcon.color = Color.red;
            }
        }
    }

    //private void UpdatePing()
    //{
    //    if (pingIntervalSec <= 0f || !isConnected || ws == null)
    //        return;

    //    if (Time.unscaledTime - lastPingTime <= pingIntervalSec)
    //        return;

    //    try
    //    {
    //        ws.Ping();
    //        lastPingTime = Time.unscaledTime;
    //    }
    //    catch (Exception ex)
    //    {
    //        Debug.LogWarning($"[ROS] Ping exception: {ex.Message}");
    //        SafeLog($"[ROS] Ping exception: {ex.Message}");
    //    }
    //}

    private void ApplyLatestFrameIfNeeded()
    {
        if (maxApplyFps > 0f)
        {
            float interval = 1f / Mathf.Max(1f, maxApplyFps);
            if (Time.unscaledTime - lastFrameApplyTime < interval)
                return;
        }

        byte[] frameToApply = null;

        lock (latestFrameLock)
        {
            if (latestEncodedFrame != null)
            {
                frameToApply = latestEncodedFrame;
                latestEncodedFrame = null;
            }
        }

        if (frameToApply == null || frameToApply.Length == 0)
            return;

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
                SafeLog($"[ROS] Texture init failed: {ex.Message}");
                return;
            }
        }

        bool ok = false;
        try
        {
            ok = ImageConversion.LoadImage(texture, frameToApply, markNonReadable: true);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ROS] LoadImage exception: {ex.Message}");
            SafeLog($"[ROS] LoadImage exception: {ex.Message}");
        }

        lastFrameApplyTime = Time.unscaledTime;

        if (ok)
        {
            hasReceivedAnyImageFrame = true;
            waitingForFirstImage = false;
            imageResubscribeAttempts = 0;
            lastImageMessageTime = Time.unscaledTime;
        }

        if (ok && !currentConnectionState)
            SetState(true);
        else if (!ok && currentConnectionState)
            SetState(false);

        if (!ok)
            Interlocked.Increment(ref droppedFrames);
    }

    private void PrintStatsIfNeeded()
    {
        if (Time.unscaledTime - lastStatTime <= 2f)
            return;

        int recv = Interlocked.Exchange(ref receivedFrames, 0);
        int drop = Interlocked.Exchange(ref droppedFrames, 0);
        int repl = Interlocked.Exchange(ref replacedFrames, 0);

        if (texture != null)
        {
            Debug.Log(
                $"[ROS] recv={recv} replaced={repl} drop={drop} tex={texture.width}x{texture.height} " +
                $"battery={(hasBatteryValue ? latestBatteryValue.ToString() : "n/a")}");
        }
        else if (recv > 0 || drop > 0 || repl > 0)
        {
            Debug.Log(
                $"[ROS] recv={recv} replaced={repl} drop={drop} " +
                $"battery={(hasBatteryValue ? latestBatteryValue.ToString() : "n/a")}");
        }

        lastStatTime = Time.unscaledTime;
    }

    private void ScheduleReconnectIfNeeded()
    {
        if (autoReconnect && wantConnection && !isConnected && !isConnecting && !isStopping && !reconnectCoroutineScheduled)
        {
            reconnectCoroutineScheduled = true;
            StartCoroutine(ReconnectAfterDelay(reconnectDelaySec));
        }
    }

    // ======================== HELPERS ========================

    private void ClearLatestFrame()
    {
        lock (latestFrameLock)
        {
            latestEncodedFrame = null;
        }
    }

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
                throw new ArgumentException($"Invalid port in wsUrl: {uri.Port}");
        }
        catch (UriFormatException ex)
        {
            throw new ArgumentException($"Invalid wsUrl format: {wsUrl}. {ex.Message}");
        }
    }

    private void AssignTextureToTarget(Texture2D tex)
    {
        try
        {
            if (targetUI != null)
                targetUI.texture = tex;

            if (targetRenderer != null)
            {
                var mr = targetRenderer.material;
                if (mr != null)
                    mr.mainTexture = tex;
            }
        }
        catch (Exception ex)
        {
            SafeLog($"[ROS] AssignTextureToTarget exception: {ex.Message}");
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
            SafeLog($"[ROS] SafeDestroyTexture exception: {ex.Message}");
            Debug.LogWarning($"[ROS] SafeDestroyTexture exception: {ex.Message}");
        }
    }

    private void SetState(bool state)
    {
        try
        {
            currentConnectionState = state;

            if (targetUI) targetUI.enabled = state;
            //if (ShowDashboard) ShowDashboard.SetActive(state);
            if (CameraPanelSettings) CameraPanelSettings.SetActive(state);
            if (RecordPanel) RecordPanel.SetActive(state);
            if (TaskPanel) TaskPanel.SetActive(!state);
            if (DashboardHeadFollower) DashboardHeadFollower.verticalOffset = state ? -0.83f : 0;
            if (LogoutButton) LogoutButton.SetActive(!state);
            //if (DashboardPanelSettings) DashboardPanelSettings.SetActive(state);
            //if (DisableControlButton) DisableControlButton.SetActive(state);
            //if (PanelSettings) PanelSettings.SetActive(state);
        }
        catch (Exception ex)
        {
            SafeLog($"[ROS] SetState exception: {ex.Message}");
            Debug.LogWarning($"[ROS] SetState exception: {ex.Message}");
        }
    }

    private void RestartImageWatchdogs()
    {
        StopImageWatchdogs();

        if (firstImageTimeoutSec > 0f)
            imageStartupWatchdogCoroutine = StartCoroutine(ImageStartupWatchdog());

        if (imageSilenceTimeoutSec > 0f)
            imageSilenceWatchdogCoroutine = StartCoroutine(ImageSilenceWatchdog());
    }

    private void StopImageWatchdogs()
    {
        if (imageStartupWatchdogCoroutine != null)
        {
            StopCoroutine(imageStartupWatchdogCoroutine);
            imageStartupWatchdogCoroutine = null;
        }

        if (imageSilenceWatchdogCoroutine != null)
        {
            StopCoroutine(imageSilenceWatchdogCoroutine);
            imageSilenceWatchdogCoroutine = null;
        }
    }

    private IEnumerator ImageStartupWatchdog()
    {
        float startTime = Time.unscaledTime;

        SafeLog($"[ROS] Waiting for first image from topic: {imageTopic}");

        while (!isDestroyedOrQuitting && isConnected && waitingForFirstImage)
        {
            if (Time.unscaledTime - startTime >= firstImageTimeoutSec)
            {
                Debug.LogWarning($"[ROS] No image frames received from topic '{imageTopic}' within {firstImageTimeoutSec:0.##} sec.");
                SafeLog($"[ROS] No image frames from '{imageTopic}' within {firstImageTimeoutSec:0.##} sec. Camera may be disabled.");

                waitingForFirstImage = false;

                if (autoRecoverImageStream)
                    TryRecoverImageStream();

                yield break;
            }

            yield return null;
        }
    }

    private IEnumerator ImageSilenceWatchdog()
    {
        while (!isDestroyedOrQuitting)
        {
            if (!isConnected)
            {
                yield return null;
                continue;
            }

            if (hasReceivedAnyImageFrame)
            {
                float silence = Time.unscaledTime - lastImageMessageTime;
                if (silence >= imageSilenceTimeoutSec)
                {
                    Debug.LogWarning($"[ROS] Image stream stalled for {silence:0.##} sec on topic '{imageTopic}'.");
                    SafeLog($"[ROS] Image stream stalled on '{imageTopic}' for {silence:0.##} sec.");

                    hasReceivedAnyImageFrame = false;
                    waitingForFirstImage = true;

                    if (autoRecoverImageStream)
                        TryRecoverImageStream();

                    yield break;
                }
            }

            yield return null;
        }
    }

    private void TryRecoverImageStream()
    {
        if (!autoRecoverImageStream || !isConnected || ws == null)
            return;

        if (!subscriptions.TryGetValue(imageTopic, out var imageSub))
        {
            SafeLog($"[ROS] Cannot recover image stream: topic '{imageTopic}' is not registered.");
            return;
        }

        if (imageResubscribeAttempts < maxImageResubscribeAttempts)
        {
            imageResubscribeAttempts++;

            SafeLog($"[ROS] Recovering image stream: resubscribe attempt {imageResubscribeAttempts}/{maxImageResubscribeAttempts}");

            try
            {
                SendUnsubscribe(imageTopic);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] Unsubscribe image topic exception: {ex.Message}");
                SafeLog($"[ROS] Unsubscribe image topic exception: {ex.Message}");
            }

            try
            {
                SendSubscribe(imageSub);
                waitingForFirstImage = true;
                hasReceivedAnyImageFrame = false;
                lastImageMessageTime = Time.unscaledTime;

                RestartImageWatchdogs();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] Resubscribe image topic exception: {ex.Message}");
                SafeLog($"[ROS] Resubscribe image topic exception: {ex.Message}");
            }
        }
        else
        {
            SafeLog("[ROS] Image topic recovery failed. Reconnecting WebSocket...");
            Debug.LogWarning("[ROS] Image topic recovery failed. Reconnecting WebSocket...");

            StopConnection();

            if (!isDestroyedOrQuitting)
            {
                wantConnection = true;
                SafeConnect();
            }
        }
    }

    //private bool TryMeasureCurrentRttMs(out int rttMs)
    //{
    //    rttMs = -1;

    //    if (ws == null || ws.ReadyState != WebSocketState.Open)
    //        return false;

    //    try
    //    {
    //        var sw = System.Diagnostics.Stopwatch.StartNew();
    //        bool ok = ws.Ping();
    //        sw.Stop();

    //        if (!ok)
    //            return false;

    //        int measuredMs = Mathf.RoundToInt((float)sw.Elapsed.TotalMilliseconds);
    //        measuredMs += Mathf.Max(0, debugArtificialRttOffsetMs);

    //        rttMs = measuredMs;
    //        return true;
    //    }
    //    catch (Exception ex)
    //    {
    //        Debug.LogWarning($"[ROS] RTT measure failed: {ex.Message}");
    //        return false;
    //    }
    //}

    private void BeginAsyncRttProbe()
    {
        if (rttProbeInFlight)
            return;

        if (ws == null || ws.ReadyState != WebSocketState.Open)
            return;

        rttProbeInFlight = true;
        int probeToken = ++lastRttProbeToken;
        var socketRef = ws;

        Task.Run(() =>
        {
            int measuredRtt = -1;
            bool ok = false;

            try
            {
                if (socketRef != null && socketRef.ReadyState == WebSocketState.Open)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ok = socketRef.Ping();
                    sw.Stop();

                    if (ok)
                    {
                        measuredRtt = Mathf.RoundToInt((float)sw.Elapsed.TotalMilliseconds);
                        measuredRtt += Mathf.Max(0, debugArtificialRttOffsetMs);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ROS] Async RTT measure failed: {ex.Message}");
                ok = false;
            }

            RunOnMainThread(() =>
            {
                // игнорируем устаревший результат
                if (probeToken != lastRttProbeToken)
                {
                    rttProbeInFlight = false;
                    return;
                }

                if (ok && measuredRtt >= 0)
                {
                    lastMeasuredRttMs = measuredRtt;
                    hasValidRttSample = true;
                    rttAboveThreshold = measuredRtt >= rttBlockThresholdMs;
                    lastRttMeasureTime = Time.unscaledTime;
                }
                else
                {
                    hasValidRttSample = false;
                    rttAboveThreshold = false;
                    lastMeasuredRttMs = -1;
                }

                rttProbeInFlight = false;
                UpdatePingUi();
            });
        });
    }

    //private IEnumerator RttMonitorLoop()
    //{
    //    while (!isDestroyedOrQuitting)
    //    {
    //        if (!enableRttMonitor || !isConnected || ws == null || ws.ReadyState != WebSocketState.Open)
    //        {
    //            yield return null;
    //            continue;
    //        }

    //        if (TryMeasureCurrentRttMs(out int rttMs))
    //        {
    //            lastMeasuredRttMs = rttMs;
    //            hasValidRttSample = true;
    //            rttAboveThreshold = rttMs >= rttBlockThresholdMs;
    //            lastRttMeasureTime = Time.unscaledTime;
    //        }
    //        else
    //        {
    //            hasValidRttSample = false;
    //            rttAboveThreshold = false;
    //            lastMeasuredRttMs = -1;
    //        }

    //        UpdatePingUi();

    //        yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, rttMeasureIntervalSec));
    //    }
    //}

    private IEnumerator RttMonitorLoop()
    {
        while (!isDestroyedOrQuitting)
        {
            if (!enableRttMonitor || !isConnected || ws == null || ws.ReadyState != WebSocketState.Open)
            {
                yield return null;
                continue;
            }

            if (!rttProbeInFlight)
            {
                BeginAsyncRttProbe();
            }

            yield return new WaitForSecondsRealtime(Mathf.Max(0.1f, rttMeasureIntervalSec));
        }
    }

    private void UpdatePingUi()
    {
        RunOnMainThread(() =>
        {
            if (!this || isDestroyedOrQuitting)
                return;

            if (pingText)
            {
                pingText.text = hasValidRttSample
                    ? $"Ping: {lastMeasuredRttMs} ms"
                    : "Ping: --- ms";

                if (hasValidRttSample)
                {
                    if (lastMeasuredRttMs <= 100f)
                    {
                        pingText.color = new Color(0, 104f / 255f, 0);
                    }
                    else if (lastMeasuredRttMs < 200f)
                    {
                        pingText.color = Color.yellow;
                    }
                    else
                    {
                        pingText.color = Color.red;
                    }
                }
                else
                {
                    pingText.color = Color.white;
                }
            }
        });
    }
    public void RegisterExternalTopicHandler(string topic, Action<JToken> handler, string rosType = "std_msgs/String")
    {
        if (string.IsNullOrWhiteSpace(topic) || handler == null)
            return;

        if (!externalTopicHandlers.TryGetValue(topic, out var handlers))
        {
            handlers = new List<Action<JToken>>();
            externalTopicHandlers[topic] = handlers;
        }

        if (!handlers.Contains(handler))
            handlers.Add(handler);

        // topic нужен в subscriptions только как описание подписки
        if (!subscriptions.ContainsKey(topic))
        {
            RegisterSubscription(topic, rosType, _ => { });
        }

        if (ws != null && ws.ReadyState == WebSocketState.Open && subscriptions.TryGetValue(topic, out var sub))
        {
            SendSubscribe(sub);
        }
    }

    public void UnregisterExternalTopicHandler(string topic, Action<JToken> handler)
    {
        if (string.IsNullOrWhiteSpace(topic) || handler == null)
            return;

        if (!externalTopicHandlers.TryGetValue(topic, out var handlers))
            return;

        handlers.Remove(handler);

        if (handlers.Count == 0)
        {
            externalTopicHandlers.Remove(topic);

            if (subscriptions.ContainsKey(topic))
            {
                try
                {
                    SendUnsubscribe(topic);
                }
                catch { }

                subscriptions.Remove(topic);
            }
        }
    }

    public bool TryPublish(string topic, JObject msg)
    {
        if (ws == null || ws.ReadyState != WebSocketState.Open)
            return false;

        try
        {
            var envelope = new JObject
            {
                ["op"] = "publish",
                ["topic"] = topic,
                ["msg"] = msg
            };

            ws.Send(envelope.ToString(Formatting.None));
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ROS] TryPublish failed for topic '{topic}': {ex.Message}");
            return false;
        }
    }

    public bool IsSocketOpen => ws != null && ws.ReadyState == WebSocketState.Open;

    public bool TryMeasureSharedSocketRttMs(out int rttMs)
    {
        return TryGetCurrentRttMs(out rttMs);
    }
}
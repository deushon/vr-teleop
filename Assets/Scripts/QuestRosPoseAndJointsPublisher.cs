using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.XR;
using WebSocketSharp;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

public class QuestRosPoseAndJointsPublisher : MonoBehaviour
{
    [Header("ROSBridge")]
    public string wsUrl = "ws://192.168.1.100:9090";
    public string poseArrayTopic = "/quest/poses";   // geometry_msgs/PoseArray
    public string jointStateTopic = "/quest/joints"; // sensor_msgs/JointState
    public string poseFrameId = "unity_world";       // header.frame_id for PoseArray
    public string headFrameId = "head";              // logic head id

    [Header("ROS Time Sync")]
    public bool enableRosTimeSync = true;
    public string timeSyncRequestTopic = "/quest/time_sync/request";
    public string timeSyncResponseTopic = "/quest/time_sync/response";
    public float timeSyncIntervalSec = 5f;
    public bool debugTimeSync = true;

    [Header("Dataset Recording")]
    [SerializeField] private DatasetManager datasetManager;

    [Header("Record Session Events")]
    public string recordSessionTopic = "/record_sessions";
    public bool sendRecordSessionEvents = true;

    [Header("XR")]
    public Camera xrCamera;

    [Header("Rate")]
    [Range(1, 120)] public float sendHz = 10f;

    [Header("Debug")]
    public bool debugPrint = true;
    [Tooltip("Pause after loosing controller tracking, before switching to hands")]
    public float handsGraceSeconds = 0.25f;

    [Header("Relative pose mode")]
    [Tooltip("If true, hand/controller position is sent as (handWorld - headWorld), without rotating into head local frame.")]
    public bool positionRelativeToHeadButIgnoreHeadRotation = true;

    [Tooltip("If true, hand/controller orientation is sent relative to head rotation. If false, absolute orientation is sent.")]
    public bool orientationRelativeToHead = true;

    [Header("External Time Sync (NTP)")]
    public bool enableNtpTimeSync = true;
    public bool debugNtpTimeSync = true;
    public int ntpTimeoutMs = 3000;

    [Tooltip("NTP servers are queried in order until one responds.")]
    public string[] ntpServers = new[]
{
        "ru.pool.ntp.org",
        "0.ru.pool.ntp.org",
        "1.ru.pool.ntp.org",
        "2.ru.pool.ntp.org",
        "3.ru.pool.ntp.org",

        "europe.pool.ntp.org",
        "0.europe.pool.ntp.org",
        "1.europe.pool.ntp.org",
        "2.europe.pool.ntp.org",
        "3.europe.pool.ntp.org",

        "pool.ntp.org",
        "0.pool.ntp.org",
        "1.pool.ntp.org",
        "2.pool.ntp.org",
        "3.pool.ntp.org",

        "time.cloudflare.com",


        "time.google.com"
    };
    private Coroutine sendLoopCoroutine;

    private WebSocket ws;
    private WaitForSeconds wait;
    private float lastDebug;

    private readonly List<InputDevice> tmp = new();

    private static readonly InputFeatureUsage<float>[] HandFloatCandidates = new[]{
        new InputFeatureUsage<float>("pinch_strength"),
        new InputFeatureUsage<float>("pinch_strength_index"),
        new InputFeatureUsage<float>("trigger"),
        new InputFeatureUsage<float>("grip"),
    };

    private static readonly InputFeatureUsage<float> kPinchIndex = new("pinch_strength_index");
    private static readonly InputFeatureUsage<float> kPinchMiddle = new("pinch_strength_middle");
    private static readonly InputFeatureUsage<float> kPinchRing = new("pinch_strength_ring");
    private static readonly InputFeatureUsage<float> kPinchLittle = new("pinch_strength_little");

    [SerializeField] private TMP_Text IpText;
    [SerializeField] private TMP_Text PortText;

    private bool controllersActivePrev;
    private float controllersLostAt = -999f;
    private bool dumpedHandFeaturesOnce;

    private Coroutine timeSyncCoroutine;

    private volatile bool rosTimeSynchronized;
    private double rosClockOffsetSec; // ros_time - local_utc_time
    private double lastSyncRttSec;

    private string lastTimeSyncId;
    private long lastTimeSyncLocalSendNs;

    private bool isRecording;
    private RecordedSession currentRecording;

    private string currentRecordId;
    private string sessionInstanceId;

    private bool ntpTimeSynchronized;
    private double ntpClockOffsetSec;   // ntp_utc - local_utc
    private double ntpLastRttSec;
    private Coroutine ntpSyncCoroutine;

    void Awake()
    {
        if (!xrCamera) xrCamera = Camera.main;
        wait = new WaitForSeconds(1f / Mathf.Max(1f, sendHz));

        sessionInstanceId = Guid.NewGuid().ToString("N");

        if (enableNtpTimeSync)
            ntpSyncCoroutine = StartCoroutine(InitializeNtpTime());
    }

    public void InitConnection()
    {
        wsUrl = $"ws://{IpText.text}:{PortText.text}";

        Disconnect();
        Connect();

        if (sendLoopCoroutine == null)
            sendLoopCoroutine = StartCoroutine(SendLoop());

        if (enableRosTimeSync && timeSyncCoroutine == null)
            timeSyncCoroutine = StartCoroutine(TimeSyncLoop());
    }

    public void Disconnect()
    {
        if (sendLoopCoroutine != null)
        {
            StopCoroutine(sendLoopCoroutine);
            sendLoopCoroutine = null;
        }

        if (timeSyncCoroutine != null)
        {
            StopCoroutine(timeSyncCoroutine);
            timeSyncCoroutine = null;
        }

        rosTimeSynchronized = false;
        rosClockOffsetSec = 0.0;
        lastSyncRttSec = 0.0;
        lastTimeSyncId = null;
        lastTimeSyncLocalSendNs = 0L;

        try
        {
            if (ws != null && ws.ReadyState == WebSocketState.Open)
            {
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = poseArrayTopic }));
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = jointStateTopic }));
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = recordSessionTopic }));
            }
        }
        catch { }

        try
        {
            ws?.Close();
        }
        catch { }

        ws = null;
    }

    void OnDisable()
    {
        Disconnect();
    }

    void Connect()
    {
        ws = new WebSocket(wsUrl);
        ws.Compression = WebSocketSharp.CompressionMethod.Deflate;

        ws.OnOpen += (_, __) =>
        {
            Debug.Log("[ROS TX] WS opened");

            ws.Send(JsonConvert.SerializeObject(new
            {
                op = "advertise",
                topic = poseArrayTopic,
                type = "geometry_msgs/PoseArray",
                latch = false
            }));

            ws.Send(JsonConvert.SerializeObject(new
            {
                op = "advertise",
                topic = jointStateTopic,
                type = "sensor_msgs/JointState",
                latch = false
            }));

            if (enableRosTimeSync)
            {
                ws.Send(JsonConvert.SerializeObject(new
                {
                    op = "advertise",
                    topic = timeSyncRequestTopic,
                    type = "std_msgs/String",
                    latch = false
                }));

                ws.Send(JsonConvert.SerializeObject(new
                {
                    op = "subscribe",
                    topic = timeSyncResponseTopic,
                    type = "std_msgs/String"
                }));
            }

            if (sendRecordSessionEvents)
            {
                ws.Send(JsonConvert.SerializeObject(new
                {
                    op = "advertise",
                    topic = recordSessionTopic,
                    type = "std_msgs/String",
                    latch = true
                }));
            }
        };

        ws.OnMessage += (_, e) =>
        {
            if (!e.IsText) return;

            try
            {
                var jo = JsonConvert.DeserializeObject<JObject>(e.Data);
                if (jo == null) return;

                string topic = jo["topic"]?.Value<string>();
                if (topic == timeSyncResponseTopic)
                {
                    HandleTimeSyncResponse(jo["msg"]);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[ROS TX] OnMessage parse error: " + ex.Message);
            }
        };

        ws.OnError += (_, e) => Debug.LogWarning("[ROS TX] WS error: " + e.Message);
        ws.OnClose += (_, e) => Debug.LogWarning("[ROS TX] WS closed: " + e.Reason);

        ws.ConnectAsync();
    }

    IEnumerator SendLoop()
    {
        yield return new WaitForSeconds(0.5f);
        while (true)
        {
            TrySendOnce();
            yield return wait;
        }
    }

    void TrySendOnce()
    {
        if (ws == null || ws.ReadyState != WebSocketState.Open || xrCamera == null)
            return;

        var headPosW = xrCamera.transform.position;
        var headRotW = xrCamera.transform.rotation;

        var leftCtrl = GetDevice(true, controller: true);
        var rightCtrl = GetDevice(false, controller: true);
        bool controllersActive = IsTracked(leftCtrl) || IsTracked(rightCtrl);

        if (controllersActivePrev && !controllersActive) controllersLostAt = Time.time;
        controllersActivePrev = controllersActive;

        bool leftHandTracked = TryGetNodePose(XRNode.LeftHand, out var leftHandPosW, out var leftHandRotW);
        bool rightHandTracked = TryGetNodePose(XRNode.RightHand, out var rightHandPosW, out var rightHandRotW);

        bool withinGrace = (Time.time - controllersLostAt) < handsGraceSeconds;
        bool handsCandidates = leftHandTracked || rightHandTracked;
        bool useControllers = controllersActive;
        bool useHands = !controllersActive && !withinGrace && handsCandidates;

        // PoseArray: [head(abs), L(rel head), R(rel head)]
        var poses = new JArray();
        poses.Add(PoseJson(headPosW, headRotW));

        if (useControllers)
        {
            poses.Add(RelToHeadFromDevice(leftCtrl, headPosW, headRotW));
            poses.Add(RelToHeadFromDevice(rightCtrl, headPosW, headRotW));
        }
        else if (useHands)
        {
            poses.Add(RelToHeadFromWorld(leftHandTracked, leftHandPosW, leftHandRotW, headPosW, headRotW));
            poses.Add(RelToHeadFromWorld(rightHandTracked, rightHandPosW, rightHandRotW, headPosW, headRotW));
        }
        else
        {
            poses.Add(PoseJson(Vector3.zero, Quaternion.identity));
            poses.Add(PoseJson(Vector3.zero, Quaternion.identity));
        }

        var header = RosHeader(poseFrameId);
        var poseArrayMsg = new JObject { ["header"] = header, ["poses"] = poses };
        Publish(poseArrayTopic, "geometry_msgs/PoseArray", poseArrayMsg);

        // JointState
        var names = new List<string>();
        var vals = new List<float>();

        // --- Controllers: analogs (grip, index) ---
        Action<string, string, InputDevice> AddCtrlAnalog = (side, key, dev) =>
        {
            float v = 0f;
            if (dev.isValid)
            {
                if (key == "grip") dev.TryGetFeatureValue(CommonUsages.grip, out v);
                if (key == "index") dev.TryGetFeatureValue(CommonUsages.trigger, out v);
            }
            names.Add($"{side}_{key}"); vals.Add(v);
        };

        // --- Controllers: buttons A/B/X/Y ---
        Action<string, InputDevice, bool> AddCtrlButtons = (side, dev, isLeft) =>
        {
            float v;
            v = ReadBoolAsFloat(dev, CommonUsages.primaryButton);   // X (L) / A (R)
            names.Add($"{side}_{(isLeft ? "X" : "A")}"); vals.Add(v);

            v = ReadBoolAsFloat(dev, CommonUsages.secondaryButton); // Y (L) / B (R)
            names.Add($"{side}_{(isLeft ? "Y" : "B")}"); vals.Add(v);
        };

        // --- Controllers: sticks (axes + click/touch) ---
        Action<string, InputDevice> AddCtrlStick = (side, dev) =>
        {
            Vector2 axis = Read2DAxis(dev); // primary2DAxis, fallback secondary2DAxis
            names.Add($"{side}_stick_x"); vals.Add(axis.x);
            names.Add($"{side}_stick_y"); vals.Add(axis.y);

            // click/touch если доступны
            names.Add($"{side}_stick_click"); vals.Add(ReadBoolAsFloat(dev, CommonUsages.primary2DAxisClick));
            names.Add($"{side}_stick_touch"); vals.Add(ReadBoolAsFloat(dev, CommonUsages.primary2DAxisTouch));
        };

        // --- Hands: try vendor/usages for squeeze ---
        bool justEnteredHands = useHands && !dumpedHandFeaturesOnce;
        Action<string, InputDevice> AddHand = (side, dev) =>
        {
            float bestVal = 0f; string used = null;
            TryReadFirstAvailable(dev, out bestVal, out used);

            float idx = 0, mid = 0, ring = 0, lit = 0, grip = 0, trig = 0;
            if (dev.isValid)
            {
                dev.TryGetFeatureValue(kPinchIndex, out idx);
                dev.TryGetFeatureValue(kPinchMiddle, out mid);
                dev.TryGetFeatureValue(kPinchRing, out ring);
                dev.TryGetFeatureValue(kPinchLittle, out lit);
                dev.TryGetFeatureValue(CommonUsages.grip, out grip);
                dev.TryGetFeatureValue(CommonUsages.trigger, out trig);
            }

            names.Add($"{side}_grip"); vals.Add(grip != 0 ? grip : bestVal);
            names.Add($"{side}_index"); vals.Add(trig != 0 ? trig : bestVal);

            names.Add($"{side}_pinch_index"); vals.Add(idx);
            names.Add($"{side}_pinch_middle"); vals.Add(mid);
            names.Add($"{side}_pinch_ring"); vals.Add(ring);
            names.Add($"{side}_pinch_little"); vals.Add(lit);

            if (debugPrint && used != null)
                Debug.Log($"[XR] {side} hand squeeze via '{used}' = {bestVal:F2}");
        };

        if (useControllers)
        {
            // analog buttons
            AddCtrlAnalog("L", "grip", leftCtrl);
            AddCtrlAnalog("L", "index", leftCtrl);
            AddCtrlAnalog("R", "grip", rightCtrl);
            AddCtrlAnalog("R", "index", rightCtrl);

            // buttons
            AddCtrlButtons("L", leftCtrl, true);
            AddCtrlButtons("R", rightCtrl, false);

            // sticks
            AddCtrlStick("L", leftCtrl);
            AddCtrlStick("R", rightCtrl);
        }
        else if (useHands)
        {
            var leftHandDev = GetDevice(true, controller: false);
            var rightHandDev = GetDevice(false, controller: false);

            if (justEnteredHands)
            {
                DumpFeatures(leftHandDev, "LeftHand");
                DumpFeatures(rightHandDev, "RightHand");
                dumpedHandFeaturesOnce = true;
            }

            AddHand("L", leftHandDev);
            AddHand("R", rightHandDev);
        }

        var jointHeader = RosHeader(headFrameId);
        var jointMsg = new JObject
        {
            ["header"] = jointHeader,
            ["name"] = new JArray(names),
            ["position"] = new JArray(vals),
        };
        Publish(jointStateTopic, "sensor_msgs/JointState", jointMsg);

        var modeStr = useControllers ? "controllers" : useHands ? "hands" : "none";

        if (isRecording && currentRecording != null)
        {
            currentRecording.frames.Add(new RecordedFrame
            {
                localUnixTimeNs = GetUnixTimeNs(),
                localMonotonicSec = Time.realtimeSinceStartupAsDouble,

                estimatedExternalUnixTimeNs = GetEstimatedExternalUnixTimeNs(),
                estimatedRosUnixTimeNs = GetEstimatedRosUnixTimeNs(),

                ntpTimeSynchronized = ntpTimeSynchronized,
                ntpClockOffsetSec = ntpClockOffsetSec,
                ntpSyncRttSec = ntpLastRttSec,

                rosClockOffsetSec = rosClockOffsetSec,
                syncRttSec = lastSyncRttSec,
                rosTimeSynchronized = rosTimeSynchronized,

                inputMode = modeStr,

                head = PoseFromToken(poses[0]),
                left = PoseFromToken(poses[1]),
                right = PoseFromToken(poses[2]),

                joints = BuildJointValues(names, vals)
            });
        }

        if (debugPrint && Time.unscaledTime - lastDebug > 1f)
        {
            lastDebug = Time.unscaledTime;
            
            float l0 = vals.Count > 0 ? vals[0] : 0f;
            float l1 = vals.Count > 1 ? vals[1] : 0f;
            Debug.Log($"[ROS TX] mode={modeStr} head=({headPosW.x:F2},{headPosW.y:F2},{headPosW.z:F2}) L0={l0:F2} L1={l1:F2} url={wsUrl} state={ws?.ReadyState}");
        }
    }

    // --------------- Helpers ---------------

    InputDevice GetDevice(bool left, bool controller)
    {
        tmp.Clear();
        var ch = InputDeviceCharacteristics.None;
        ch |= left ? InputDeviceCharacteristics.Left : InputDeviceCharacteristics.Right;
        if (controller)
            ch |= InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.HeldInHand;
        else
            ch |= InputDeviceCharacteristics.HandTracking;

        InputDevices.GetDevicesWithCharacteristics(ch, tmp);
        return tmp.Count > 0 ? tmp[0] : default;
    }

    bool IsTracked(InputDevice dev)
    {
        if (!dev.isValid) return false;
        if (dev.TryGetFeatureValue(CommonUsages.isTracked, out bool t)) return t;
        return dev.TryGetFeatureValue(CommonUsages.devicePosition, out _);
    }

    static bool TryGetNodePose(XRNode node, out Vector3 pos, out Quaternion rot)
    {
        pos = default; rot = default;
        var states = new List<XRNodeState>();
        InputTracking.GetNodeStates(states);
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            if (s.nodeType != node) continue;
            bool okP = s.TryGetPosition(out pos);
            bool okR = s.TryGetRotation(out rot);
            return okP || okR;
        }
        return false;
    }

    JObject RelToHeadFromDevice(InputDevice dev, Vector3 headPosW, Quaternion headRotW)
    {
        if (!dev.isValid) return PoseJson(Vector3.zero, Quaternion.identity);
        if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pW)) return PoseJson(Vector3.zero, Quaternion.identity);
        if (!dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rW)) return PoseJson(Vector3.zero, Quaternion.identity);

        return RelativePose(
            pW, rW,
            headPosW, headRotW,
            positionRelativeToHeadButIgnoreHeadRotation,
            orientationRelativeToHead
        );
    }

    JObject RelToHeadFromWorld(bool tracked, Vector3 pW, Quaternion rW, Vector3 headPosW, Quaternion headRotW)
    {
        if (!tracked) return PoseJson(Vector3.zero, Quaternion.identity);

        return RelativePose(
            pW, rW,
            headPosW, headRotW,
            positionRelativeToHeadButIgnoreHeadRotation,
            orientationRelativeToHead
        );
    }

    JObject RelativePose(Vector3 objPosW, Quaternion objRotW, Vector3 headPosW, Quaternion headRotW, bool ignoreHeadRotationForPosition, bool orientationRelativeToHead)
    {
        Vector3 pRel = ignoreHeadRotationForPosition
            ? (objPosW - headPosW)
            : (Quaternion.Inverse(headRotW) * (objPosW - headPosW));

        Quaternion rRel = orientationRelativeToHead
            ? (Quaternion.Inverse(headRotW) * objRotW)
            : objRotW;

        return PoseJson(pRel, rRel);
    }

    static bool TryReadFirstAvailable(InputDevice dev, out float value, out string nameUsed)
    {
        value = 0f; nameUsed = null;
        if (!dev.isValid) return false;
        foreach (var u in HandFloatCandidates)
        {
            if (dev.TryGetFeatureValue(u, out value))
            {
                nameUsed = u.name;
                return true;
            }
        }
        return false;
    }

    static void DumpFeatures(InputDevice dev, string label)
    {
        if (!dev.isValid) { Debug.Log($"[XR] {label} features: <invalid device>"); return; }
        var usages = new List<InputFeatureUsage>();
        if (dev.TryGetFeatureUsages(usages))
        {
            var sb = new StringBuilder();
            foreach (var u in usages) sb.Append(u.name).Append(", ");
            Debug.Log($"[XR] {label} features: {sb}");
        }
        else
        {
            Debug.Log($"[XR] {label} features: <none>");
        }
    }

    static float ReadBoolAsFloat(InputDevice dev, InputFeatureUsage<bool> usage)
    {
        if (dev.isValid && dev.TryGetFeatureValue(usage, out bool b)) return b ? 1f : 0f;
        return 0f;
    }

    // Read stick axis: primary2DAxis with reserve secondary2DAxis
    static Vector2 Read2DAxis(InputDevice dev)
    {
        Vector2 v = Vector2.zero;
        if (!dev.isValid) return v;
        if (dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 a)) v = a;
        if (v == Vector2.zero) dev.TryGetFeatureValue(CommonUsages.secondary2DAxis, out v);
        return v;
    }

    static JObject PoseJson(Vector3 p, Quaternion q)
    {
        return new JObject
        {
            ["position"] = new JObject { ["x"] = p.x, ["y"] = p.y, ["z"] = p.z },
            ["orientation"] = new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w }
        };
    }

    JObject RosHeader(string frameId)
    {
        long nanos = GetEstimatedRosUnixTimeNs();
        int secs = (int)(nanos / 1_000_000_000L);
        int nsecs = (int)(nanos % 1_000_000_000L);

        return new JObject
        {
            ["stamp"] = new JObject { ["secs"] = secs, ["nsecs"] = nsecs },
            ["frame_id"] = frameId
        };
    }

    void Publish(string topic, string rosType, JObject msg)
    {
        var envelope = new JObject
        {
            ["op"] = "publish",
            ["topic"] = topic,
            ["msg"] = msg
        };
        ws.Send(envelope.ToString(Formatting.None));
    }

    static long GetUnixTimeNs()
    {
        return (DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100L; // 1 tick = 100 ns
    }

    long GetEstimatedRosUnixTimeNs()
    {
        long baseNs = GetEstimatedExternalUnixTimeNs();

        if (!rosTimeSynchronized)
            return baseNs;

        return baseNs + (long)(rosClockOffsetSec * 1_000_000_000.0);
    }

    void SendTimeSyncPing()
    {
        if (!enableRosTimeSync || ws == null || ws.ReadyState != WebSocketState.Open)
            return;

        lastTimeSyncId = Guid.NewGuid().ToString("N");
        lastTimeSyncLocalSendNs = GetEstimatedExternalUnixTimeNs();

        var payload = new JObject
        {
            ["ping_id"] = lastTimeSyncId,
            ["client_send_unix_ns"] = lastTimeSyncLocalSendNs
        };

        var envelope = new JObject
        {
            ["op"] = "publish",
            ["topic"] = timeSyncRequestTopic,
            ["msg"] = new JObject
            {
                ["data"] = payload.ToString(Formatting.None)
            }
        };

        ws.Send(envelope.ToString(Formatting.None));
    }

    void HandleTimeSyncResponse(JToken msg)
    {
        try
        {
            string json = msg?["data"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(json))
                return;

            var jo = JsonConvert.DeserializeObject<JObject>(json);
            if (jo == null)
                return;

            string pingId = jo["ping_id"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(pingId))
                return;

            long clientSendNs = jo["client_send_unix_ns"]?.Value<long>() ?? 0L;
            long robotRosUnixNs = jo["robot_ros_unix_ns"]?.Value<long>() ?? 0L;
            if (clientSendNs == 0L || robotRosUnixNs == 0L)
                return;

            long clientRecvNs = GetEstimatedExternalUnixTimeNs();
            long midpointNs = clientSendNs + (clientRecvNs - clientSendNs) / 2L;

            double measuredOffsetSec = (robotRosUnixNs - midpointNs) / 1_000_000_000.0;
            double measuredRttSec = (clientRecvNs - clientSendNs) / 1_000_000_000.0;

            if (!rosTimeSynchronized)
            {
                rosClockOffsetSec = measuredOffsetSec;
            }
            else
            {
                const double alpha = 0.15; // сглаживание
                rosClockOffsetSec = rosClockOffsetSec * (1.0 - alpha) + measuredOffsetSec * alpha;
            }

            lastSyncRttSec = measuredRttSec;
            rosTimeSynchronized = true;

            if (debugTimeSync)
            {
                Debug.Log($"[ROS TX] Time sync OK: offset={rosClockOffsetSec:F6}s rtt={lastSyncRttSec:F4}s");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[ROS TX] HandleTimeSyncResponse error: " + ex.Message);
        }
    }

    IEnumerator TimeSyncLoop()
    {
        yield return new WaitForSecondsRealtime(0.25f);

        while (true)
        {
            if (enableRosTimeSync)
                SendTimeSyncPing();

            yield return new WaitForSecondsRealtime(Mathf.Max(0.5f, timeSyncIntervalSec));
        }
    }

    public void StartRecording()
    {
        currentRecordId = Guid.NewGuid().ToString("N");

        currentRecording = new RecordedSession
        {
            recordId = currentRecordId,

            startedLocalUnixTimeNs = GetUnixTimeNs(),
            endedLocalUnixTimeNs = 0L,

            startedEstimatedRosUnixTimeNs = GetEstimatedRosUnixTimeNs(),
            endedEstimatedRosUnixTimeNs = 0L,

            startedEstimatedExternalUnixTimeNs = GetEstimatedExternalUnixTimeNs(),
            endedEstimatedExternalUnixTimeNs = 0L,

            rosTimeWasSynchronizedAtStart = rosTimeSynchronized,
            ntpTimeWasSynchronizedAtStart = ntpTimeSynchronized,

            sourceWsUrl = wsUrl,
            sourceSendHz = sendHz
        };

        isRecording = true;

        Debug.Log($"[ROS TX] About to publish START record event, id={currentRecordId}, wsState={ws?.ReadyState}");
        PublishRecordSessionEvent("start", currentRecordId);
        Debug.Log($"[ROS TX] Recording started, id={currentRecordId}");
    }

    public void StopRecordingAndSave()
    {
        if (!isRecording || currentRecording == null)
            return;

        currentRecording.endedLocalUnixTimeNs = GetUnixTimeNs();
        currentRecording.endedEstimatedRosUnixTimeNs = GetEstimatedRosUnixTimeNs();
        currentRecording.endedEstimatedExternalUnixTimeNs = GetEstimatedExternalUnixTimeNs();

        currentRecording.rosTimeWasSynchronizedAtEnd = rosTimeSynchronized;
        currentRecording.ntpTimeWasSynchronizedAtEnd = ntpTimeSynchronized;

        Debug.Log($"[ROS TX] About to publish STOP record event, id={currentRecordId}, wsState={ws?.ReadyState}");
        PublishRecordSessionEvent("stop", currentRecordId);

        var completed = currentRecording;

        currentRecording = null;
        isRecording = false;

        if (datasetManager != null)
        {
            datasetManager.AddNewRecord(completed);
        }
        else
        {
            Debug.LogWarning("[ROS TX] DatasetManager is not assigned, recorded session is lost.");
        }

        Debug.Log($"[ROS TX] Recording stopped, id={currentRecordId}");
        currentRecordId = null;
    }

    private RecordedPose PoseFromToken(JToken poseToken)
    {
        if (poseToken == null)
        {
            return new RecordedPose
            {
                position = new JsonVec3 { x = 0f, y = 0f, z = 0f },
                orientation = new JsonQuat { x = 0f, y = 0f, z = 0f, w = 1f }
            };
        }

        return new RecordedPose
        {
            position = new JsonVec3
            {
                x = poseToken["position"]?["x"]?.Value<float>() ?? 0f,
                y = poseToken["position"]?["y"]?.Value<float>() ?? 0f,
                z = poseToken["position"]?["z"]?.Value<float>() ?? 0f
            },
            orientation = new JsonQuat
            {
                x = poseToken["orientation"]?["x"]?.Value<float>() ?? 0f,
                y = poseToken["orientation"]?["y"]?.Value<float>() ?? 0f,
                z = poseToken["orientation"]?["z"]?.Value<float>() ?? 0f,
                w = poseToken["orientation"]?["w"]?.Value<float>() ?? 1f
            }
        };
    }

    List<RecordedJointValue> BuildJointValues(List<string> names, List<float> vals)
    {
        var result = new List<RecordedJointValue>(Mathf.Min(names.Count, vals.Count));
        int count = Mathf.Min(names.Count, vals.Count);

        for (int i = 0; i < count; i++)
        {
            result.Add(new RecordedJointValue
            {
                name = names[i],
                value = vals[i]
            });
        }

        return result;
    }

    private JsonVec3 ToJsonVec3(Vector3 v)
    {
        return new JsonVec3
        {
            x = v.x,
            y = v.y,
            z = v.z
        };
    }

    private JsonQuat ToJsonQuat(Quaternion q)
    {
        return new JsonQuat
        {
            x = q.x,
            y = q.y,
            z = q.z,
            w = q.w
        };
    }

    IEnumerator InitializeNtpTime()
    {
        yield return null;

        bool success = false;

        foreach (var server in ntpServers)
        {
            if (string.IsNullOrWhiteSpace(server))
                continue;

            bool done = false;
            bool ok = false;
            DateTime ntpUtc = default;
            double rttSec = 0.0;
            string error = null;

            Task.Run(() =>
            {
                try
                {
                    ok = TryGetNetworkTime(server, ntpTimeoutMs, out ntpUtc, out rttSec, out error);
                }
                catch (Exception ex)
                {
                    ok = false;
                    error = ex.Message;
                }
                finally
                {
                    done = true;
                }
            });

            while (!done)
                yield return null;

            if (!ok)
            {
                if (debugNtpTimeSync)
                    Debug.LogWarning($"[NTP] Failed for {server}: {error}");
                continue;
            }

            DateTime localUtc = DateTime.UtcNow;
            ntpClockOffsetSec = (ntpUtc - localUtc).TotalSeconds;
            ntpLastRttSec = rttSec;
            ntpTimeSynchronized = true;
            success = true;

            if (debugNtpTimeSync)
            {
                Debug.Log($"[NTP] Sync OK via {server}: offset={ntpClockOffsetSec:F6}s rtt={ntpLastRttSec:F4}s utc={ntpUtc:O}");
            }

            break;
        }

        if (!success)
        {
            ntpTimeSynchronized = false;
            ntpClockOffsetSec = 0.0;
            ntpLastRttSec = 0.0;

            Debug.LogWarning("[NTP] All NTP servers failed. Falling back to local device UTC.");
        }
    }

    bool TryGetNetworkTime(string hostname, int timeoutMs, out DateTime networkUtc, out double rttSec, out string error)
    {
        networkUtc = default;
        rttSec = 0.0;
        error = null;

        const int ntpPort = 123;
        byte[] ntpData = new byte[48];
        ntpData[0] = 0x1B; // LI, Version, Mode

        IPAddress[] addresses = Dns.GetHostAddresses(hostname);
        if (addresses == null || addresses.Length == 0)
        {
            error = "DNS resolved no addresses";
            return false;
        }

        var endpoint = new IPEndPoint(addresses[0], ntpPort);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = timeoutMs;
        socket.SendTimeout = timeoutMs;

        DateTime sendUtc = DateTime.UtcNow;
        long sendTicks = sendUtc.Ticks;

        socket.Connect(endpoint);
        socket.Send(ntpData);

        int received = socket.Receive(ntpData);
        DateTime recvUtc = DateTime.UtcNow;
        long recvTicks = recvUtc.Ticks;

        if (received < 48)
        {
            error = $"Short NTP response: {received} bytes";
            return false;
        }

        ulong intPart = ((ulong)ntpData[40] << 24) | ((ulong)ntpData[41] << 16) | ((ulong)ntpData[42] << 8) | ntpData[43];
        ulong fractPart = ((ulong)ntpData[44] << 24) | ((ulong)ntpData[45] << 16) | ((ulong)ntpData[46] << 8) | ntpData[47];

        const ulong seventyYears = 2208988800UL;
        ulong unixSeconds = intPart - seventyYears;
        double fraction = fractPart / 4294967296.0;

        DateTime serverUtc = DateTime.UnixEpoch.AddSeconds(unixSeconds + fraction);

        rttSec = TimeSpan.FromTicks(recvTicks - sendTicks).TotalSeconds;

        // приближение: считаем, что ответ соответствует середине RTT
        networkUtc = serverUtc.AddSeconds(rttSec * 0.5);
        return true;
    }

    long GetEstimatedExternalUnixTimeNs()
    {
        long localNs = GetUnixTimeNs();

        if (ntpTimeSynchronized)
            return localNs + (long)(ntpClockOffsetSec * 1_000_000_000.0);

        return localNs;
    }

    void PublishRecordSessionEvent(string eventType, string recordId)
    {
        if (!sendRecordSessionEvents || ws == null || ws.ReadyState != WebSocketState.Open)
            return;

        var msg = new JObject
        {
            ["record_id"] = recordId,
            ["event_type"] = eventType, // "start" / "stop"
            ["app_session_id"] = sessionInstanceId,
            ["timestamp_unix_ns"] = GetEstimatedExternalUnixTimeNs(),
            ["timestamp_ros_unix_ns"] = GetEstimatedRosUnixTimeNs(),
            ["ntp_time_synchronized"] = ntpTimeSynchronized,
            ["ros_time_synchronized"] = rosTimeSynchronized,
            ["pose_topic"] = poseArrayTopic,
            ["joint_topic"] = jointStateTopic,
            ["send_hz"] = sendHz
        };

        var envelope = new JObject
        {
            ["op"] = "publish",
            ["topic"] = recordSessionTopic,
            ["msg"] = new JObject
            {
                ["data"] = msg.ToString(Formatting.None)
            }
        };

        ws.Send(envelope.ToString(Formatting.None));
        //Debug.Log("SUCCESSFUL SENDING");
    }
}

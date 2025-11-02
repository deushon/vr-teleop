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

public class QuestRosPoseAndJointsPublisher : MonoBehaviour
{
    [Header("ROSBridge")]
    public string wsUrl = "ws://192.168.1.100:9090";
    public string poseArrayTopic = "/quest/poses";   // geometry_msgs/PoseArray
    public string jointStateTopic = "/quest/joints"; // sensor_msgs/JointState
    public string poseFrameId = "unity_world";       // header.frame_id для PoseArray
    public string headFrameId = "head";              // логический ид головы (для читаемости в отладке)

    [Header("XR")]
    public Camera xrCamera; // укажите Main Camera из XR Origin

    [Header("Rate")]
    [Range(1, 120)] public float sendHz = 10f;

    [Header("Debug")]
    public bool debugPrint = true;
    [Tooltip("Пауза после потери контроллеров, прежде чем переключаться на руки")]
    public float handsGraceSeconds = 0.25f;

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

    void Awake()
    {
        if (!xrCamera) xrCamera = Camera.main;
        wait = new WaitForSeconds(1f / Mathf.Max(1f, sendHz));
    }

    public void InitConnection()
    {
        wsUrl = $"ws://{IpText.text}:{PortText.text}";
        Connect();
        StartCoroutine(SendLoop());
    }

    public void Disconnect()
    {
        try
        {
            if (ws != null && ws.ReadyState == WebSocketState.Open)
            {
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = poseArrayTopic }));
                ws.Send(JsonConvert.SerializeObject(new { op = "unadvertise", topic = jointStateTopic }));
            }
            ws?.Close();
        }
        catch { }
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
            ws.Send(JsonConvert.SerializeObject(new { op = "advertise", topic = poseArrayTopic, type = "geometry_msgs/PoseArray", latch = false }));
            ws.Send(JsonConvert.SerializeObject(new { op = "advertise", topic = jointStateTopic, type = "sensor_msgs/JointState", latch = false }));
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

        // --- Controllers: grip/index + A/B/X/Y ---
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

        Action<string, InputDevice, bool> AddCtrlButtons = (side, dev, isLeft) =>
        {
            float v;
            v = ReadBoolAsFloat(dev, CommonUsages.primaryButton);   // X (L) / A (R)
            names.Add($"{side}_{(isLeft ? "X" : "A")}"); vals.Add(v);

            v = ReadBoolAsFloat(dev, CommonUsages.secondaryButton); // Y (L) / B (R)
            names.Add($"{side}_{(isLeft ? "Y" : "B")}"); vals.Add(v);
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
            AddCtrlAnalog("L", "grip", leftCtrl);
            AddCtrlAnalog("L", "index", leftCtrl);
            AddCtrlAnalog("R", "grip", rightCtrl);
            AddCtrlAnalog("R", "index", rightCtrl);

            AddCtrlButtons("L", leftCtrl, true);
            AddCtrlButtons("R", rightCtrl, false);
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

        if (debugPrint && Time.unscaledTime - lastDebug > 1f)
        {
            lastDebug = Time.unscaledTime;
            var modeStr = useControllers ? "controllers" : useHands ? "hands" : "none";
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

    static JObject RelToHeadFromDevice(InputDevice dev, Vector3 headPosW, Quaternion headRotW)
    {
        if (!dev.isValid) return PoseJson(Vector3.zero, Quaternion.identity);
        if (!dev.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pW)) return PoseJson(Vector3.zero, Quaternion.identity);
        if (!dev.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rW)) return PoseJson(Vector3.zero, Quaternion.identity);

        var pRel = Quaternion.Inverse(headRotW) * (pW - headPosW);
        var rRel = Quaternion.Inverse(headRotW) * rW;
        return PoseJson(pRel, rRel);
    }

    static JObject RelToHeadFromWorld(bool tracked, Vector3 pW, Quaternion rW, Vector3 headPosW, Quaternion headRotW)
    {
        if (!tracked) return PoseJson(Vector3.zero, Quaternion.identity);
        var pRel = Quaternion.Inverse(headRotW) * (pW - headPosW);
        var rRel = Quaternion.Inverse(headRotW) * rW;
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

    static JObject PoseJson(Vector3 p, Quaternion q)
    {
        return new JObject
        {
            ["position"] = new JObject { ["x"] = p.x, ["y"] = p.y, ["z"] = p.z },
            ["orientation"] = new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w }
        };
    }

    static JObject RosHeader(string frameId)
    {
        var now = DateTimeOffset.UtcNow;
        long nanos = now.ToUnixTimeMilliseconds() * 1_000_000;
        int secs = (int)(nanos / 1_000_000_000);
        int nsecs = (int)(nanos % 1_000_000_000);

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
}

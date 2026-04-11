using System;
using System.Collections.Generic;

[Serializable]
public class JsonVec3
{
    public float x;
    public float y;
    public float z;
}

[Serializable]
public class JsonQuat
{
    public float x;
    public float y;
    public float z;
    public float w;
}

[Serializable]
public class RecordedPose
{
    public JsonVec3 position;
    public JsonQuat orientation;
}

[Serializable]
public class RecordedJointValue
{
    public string name;
    public float value;
}

[Serializable]
public class RecordedFrame
{
    public long localUnixTimeNs;
    public double localMonotonicSec;

    public long estimatedRosUnixTimeNs;
    public double rosClockOffsetSec;
    public double syncRttSec;
    public bool rosTimeSynchronized;

    public string inputMode;

    public RecordedPose head;
    public RecordedPose left;
    public RecordedPose right;

    public List<RecordedJointValue> joints = new();

    public long estimatedExternalUnixTimeNs;
    public bool ntpTimeSynchronized;
    public double ntpClockOffsetSec;
    public double ntpSyncRttSec;
}

[Serializable]
public class RecordedSession
{
    public long startedLocalUnixTimeNs;
    public long endedLocalUnixTimeNs;

    public long startedEstimatedRosUnixTimeNs;
    public long endedEstimatedRosUnixTimeNs;

    public bool rosTimeWasSynchronizedAtStart;
    public bool rosTimeWasSynchronizedAtEnd;

    public string sourceWsUrl;
    public float sourceSendHz;

    public List<RecordedFrame> frames = new();

    public string recordId;
    public long startedEstimatedExternalUnixTimeNs;
    public long endedEstimatedExternalUnixTimeNs;
    public bool ntpTimeWasSynchronizedAtStart;
    public bool ntpTimeWasSynchronizedAtEnd;
}

[Serializable]
public class DatasetUploadRecord
{
    public string label;
    public string taskName;
    public RecordedSession data;
    public string recordId;
}

[Serializable]
public class DatasetUploadRequest
{
    public string source;
    public string generatedUtcIso;
    public int contractVersion = 2;
    public string acceptedAtUtcIso;
    public List<DatasetUploadRecord> records = new();

    public TeleopControlEventsBlock teleopControl = new();
}

[Serializable]
public class TeleopControlEvent
{
    public string eventType;          // "get_control" or "lost_control"
    public string timestampUtcIso;
}

[Serializable]
public class TeleopControlEventsBlock
{
    public List<TeleopControlEvent> events = new();
}

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
}

[Serializable]
public class DatasetUploadRecord
{
    public string label;
    public string taskName;
    public RecordedSession data;
}

[Serializable]
public class DatasetUploadRequest
{
    public string source;
    public string generatedUtcIso;
    public List<DatasetUploadRecord> records = new();
}
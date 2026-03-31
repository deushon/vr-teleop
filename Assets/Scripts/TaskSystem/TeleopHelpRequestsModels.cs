using System;
using System.Collections.Generic;
using Newtonsoft.Json;

[Serializable]
public class TeleopHelpRequestsResponse
{
    [JsonProperty("helpRequests")]
    public List<TeleopHelpRequestDto> helpRequests;
}

[Serializable]
public class TeleopHelpRequestDto
{
    [JsonProperty("id")]
    public string id;

    [JsonProperty("robotId")]
    public string robotId;

    [JsonProperty("status")]
    public string status;

    [JsonProperty("payload")]
    public TeleopHelpRequestPayloadDto payload;

    [JsonProperty("createdAt")]
    public string createdAt;
}

[Serializable]
public class TeleopHelpRequestPayloadDto
{
    [JsonProperty("message")]
    public string message;

    [JsonProperty("metadata")]
    public TeleopHelpRequestMetadataDto metadata;
}

[Serializable]
public class TeleopHelpRequestMetadataDto
{
    [JsonProperty("task_id")]
    public string taskId;

    [JsonProperty("error_context")]
    public string errorContext;

    [JsonProperty("situation_report")]
    public string situationReport;
}

[Serializable]
public class TeleopAcceptHelpRequestResponse
{
    [JsonProperty("ok")]
    public bool ok;

    [JsonProperty("helpRequest")]
    public TeleopAcceptedHelpRequestDto helpRequest;

    [JsonProperty("session")]
    public TeleopSessionDto session;
}

[Serializable]
public class TeleopAcceptedHelpRequestDto
{
    [JsonProperty("id")]
    public string id;

    [JsonProperty("robotId")]
    public string robotId;

    [JsonProperty("status")]
    public string status;
}

[Serializable]
public class TeleopSessionDto
{
    [JsonProperty("id")]
    public string id;

    [JsonProperty("robotId")]
    public string robotId;

    [JsonProperty("createdAt")]
    public string createdAt;
}
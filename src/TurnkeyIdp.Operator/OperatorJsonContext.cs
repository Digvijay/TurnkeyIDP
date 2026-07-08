using System.Text.Json.Serialization;
using TurnkeyIdp.Operator.Entities;

using k8s;

namespace TurnkeyIdp.Operator;

public class DiagnosticLogsResponse
{
    public string PodName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Logs { get; set; } = string.Empty;
}

[JsonSerializable(typeof(IdpDeployment))]
[JsonSerializable(typeof(IdpDeploymentSpec))]
[JsonSerializable(typeof(IdpDeploymentStatus))]
[JsonSerializable(typeof(DiagnosticLogsResponse))]
[JsonSerializable(typeof(BackstageConfig))]
[JsonSerializable(typeof(ClusterConfig))]
[JsonSerializable(typeof(GitOpsConfig))]
[JsonSerializable(typeof(CiConfig))]
[JsonSerializable(typeof(DeliveryConfig))]
[JsonSerializable(typeof(ObservabilityConfig))]
[JsonSerializable(typeof(ObservabilityStack))]
[JsonSerializable(typeof(ComponentStatus))]
[JsonSerializable(typeof(List<ComponentStatus>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(k8s.Watcher<k8s.KubernetesObject>.WatchEvent))]
[JsonSerializable(typeof(k8s.Watcher<IdpDeployment>.WatchEvent))]
[JsonSerializable(typeof(object))] // Root object fallback
internal partial class OperatorJsonContext : JsonSerializerContext
{
}

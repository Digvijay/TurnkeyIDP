using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace TurnkeyIdp.Operator.Entities;

[KubernetesEntity(
    Group = "turnkey.idp.io", 
    ApiVersion = "v1alpha1", 
    Kind = "IdpClusterClaim", 
    PluralName = "idpclusterclaims")]
public class IdpClusterClaim : CustomKubernetesEntity<IdpClusterClaimSpec, IdpClusterClaimStatus>
{
}

public class IdpClusterClaimSpec
{
    public string NodeSize { get; set; } = string.Empty;
    public int MinNodes { get; set; }
    public int MaxNodes { get; set; }
    public string Region { get; set; } = string.Empty;
}

public class IdpClusterClaimStatus
{
    public string State { get; set; } = string.Empty;
}

[KubernetesEntity(
    Group = "opentelemetry.io", 
    ApiVersion = "v1alpha1", 
    Kind = "OpenTelemetryCollector", 
    PluralName = "opentelemetrycollectors")]
public class OpenTelemetryCollector : CustomKubernetesEntity<OpenTelemetryCollectorSpec, object>
{
}

public class OpenTelemetryCollectorSpec
{
    public string Mode { get; set; } = "deployment";
    public string Config { get; set; } = string.Empty;
}

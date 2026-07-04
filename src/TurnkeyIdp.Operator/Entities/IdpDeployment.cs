using k8s.Models;
using KubeOps.Abstractions.Entities;

namespace TurnkeyIdp.Operator.Entities;

[KubernetesEntity(
    Group = "turnkey.idp.io", 
    ApiVersion = "v1alpha1", 
    Kind = "IdpDeployment", 
    PluralName = "idpdeployments")]
public class IdpDeployment : CustomKubernetesEntity<IdpDeploymentSpec, IdpDeploymentStatus>
{
}

public class IdpDeploymentSpec
{
    public string CloudProvider { get; set; } = "Azure";
    /// <summary>
    /// The root domain for all IDP service URLs.
    /// For Kind: auto-set to "127.0.0.1.nip.io" (yields argocd.127.0.0.1.nip.io etc.)
    /// For cloud: set to your domain e.g. "idp.company.com"
    /// </summary>
    public string Domain { get; set; } = "127.0.0.1.nip.io";
    public ClusterConfig ClusterConfig { get; set; } = new();
    public GitOpsConfig GitOps { get; set; } = new();
    public CiConfig Ci { get; set; } = new();
    public DeliveryConfig Delivery { get; set; } = new();
    public ObservabilityConfig Observability { get; set; } = new();
    public BackstageConfig Backstage { get; set; } = new();
}

public class BackstageConfig
{
    public bool Enabled { get; set; } = false;
    public string CatalogRepoUrl { get; set; } = string.Empty;
}

public class ClusterConfig
{
    public string NodeSize { get; set; } = "Standard_D4s_v3";
    public int MinNodes { get; set; } = 3;
    public int MaxNodes { get; set; } = 10;
    public string Region { get; set; } = "eastus";
}

public class GitOpsConfig
{
    public string Engine { get; set; } = "ArgoCD"; // ArgoCD, FluxCD
    public string RepoUrl { get; set; } = string.Empty;
    public bool EnableAppOfApps { get; set; } = false;
}

public class CiConfig
{
    public string Engine { get; set; } = "ArgoWorkflows"; // Tekton, ArgoWorkflows
}

public class DeliveryConfig
{
    public bool EnableProgressiveDelivery { get; set; } = true;
    /// <summary>GatewayClass name: istio | gke | azure | aws | nginx-gateway</summary>
    public string GatewayClass { get; set; } = "istio";
}

public class ObservabilityConfig
{
    public bool Enabled { get; set; } = true;
    public ObservabilityStack Stack { get; set; } = new();
    public int MetricsDays { get; set; } = 15;
    public int TracesDays { get; set; } = 7;
    public string StorageClass { get; set; } = "default";
}

public class ObservabilityStack
{
    public string Metrics { get; set; } = "Prometheus";
    public string Visualization { get; set; } = "Grafana";
    public string Tracing { get; set; } = "JaegerV2";
}

public class IdpDeploymentStatus
{
    public string Phase { get; set; } = "Pending";
    public string Message { get; set; } = string.Empty;
    public List<ComponentStatus> Components { get; set; } = new();
}

public class ComponentStatus
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending"; // Pending, Reconciling, Healthy, Degraded, Error
    public string Message { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

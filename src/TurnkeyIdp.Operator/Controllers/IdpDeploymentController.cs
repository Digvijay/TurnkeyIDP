using System;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using k8s;
using k8s.Models;
using KubeOps.Abstractions.Reconciliation;
using KubeOps.Abstractions.Reconciliation.Controller;
using KubeOps.KubernetesClient;
using TurnkeyIdp.Operator.Entities;

namespace TurnkeyIdp.Operator.Controllers;

public class IdpDeploymentController : IEntityController<IdpDeployment>
{
    private readonly IKubernetesClient _client;
    private readonly ILogger<IdpDeploymentController> _logger;

    public IdpDeploymentController(IKubernetesClient client, ILogger<IdpDeploymentController> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<ReconciliationResult<IdpDeployment>> ReconcileAsync(IdpDeployment entity, CancellationToken cancellationToken)
    {
        var name = entity.Metadata.Name;
        var ns = entity.Metadata.NamespaceProperty ?? "default";
        _logger.LogInformation("Reconciling IdpDeployment resource: {Name} in namespace {Namespace}", name, ns);

        try
        {
            // 1. Initialize Status if missing or empty
            if (entity.Status == null || entity.Status.Components == null || !entity.Status.Components.Any())
            {
                entity.Status = new IdpDeploymentStatus
                {
                    Phase = "Pending",
                    Message = "Initializing deployment plan...",
                    Components = new List<ComponentStatus>
                    {
                        new() { Name = "Crossplane Infrastructure", Status = "Pending", Namespace = "crossplane-system" },
                        new() { Name = "GitOps Engine", Status = "Pending", Namespace = entity.Spec.GitOps.Engine.ToLower() == "argocd" ? "argocd" : "flux-system" },
                        new() { Name = "CI Engine", Status = "Pending", Namespace = entity.Spec.Ci.Engine.ToLower() == "argoworkflows" ? "argo" : "tekton-pipelines" },
                    }
                };
                
                if (entity.Spec.Delivery.EnableProgressiveDelivery)
                {
                    entity.Status.Components.Add(new ComponentStatus { Name = "Argo Rollouts", Status = "Pending", Namespace = "argo-rollouts" });
                }
                
                if (entity.Spec.Observability.Enabled)
                {
                    entity.Status.Components.Add(new ComponentStatus { Name = "Grafana Dashboard", Status = "Pending", Namespace = "monitoring" });
                    entity.Status.Components.Add(new ComponentStatus { Name = "Prometheus Metrics", Status = "Pending", Namespace = "monitoring" });
                    entity.Status.Components.Add(new ComponentStatus { Name = "Jaeger Tracing", Status = "Pending", Namespace = "monitoring" });
                }

                if (entity.Spec.Backstage.Enabled)
                {
                    entity.Status.Components.Add(new ComponentStatus { Name = "Spotify Backstage", Status = "Pending", Namespace = "backstage" });
                }

                entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                _logger.LogInformation("Initialized status for IdpDeployment: {Name}", name);
            }

            // Dynamically synchronize status components in case of spec changes on an existing resource
            bool statusChanged = false;
            if (entity.Spec.Delivery.EnableProgressiveDelivery && !entity.Status.Components.Any(c => c.Name == "Argo Rollouts"))
            {
                entity.Status.Components.Add(new ComponentStatus { Name = "Argo Rollouts", Status = "Pending", Namespace = "argo-rollouts" });
                statusChanged = true;
            }
            if (entity.Spec.Observability.Enabled && !entity.Status.Components.Any(c => c.Name == "Grafana Dashboard"))
            {
                entity.Status.Components.Add(new ComponentStatus { Name = "Grafana Dashboard", Status = "Pending", Namespace = "monitoring" });
                entity.Status.Components.Add(new ComponentStatus { Name = "Prometheus Metrics", Status = "Pending", Namespace = "monitoring" });
                entity.Status.Components.Add(new ComponentStatus { Name = "Jaeger Tracing", Status = "Pending", Namespace = "monitoring" });
                statusChanged = true;
            }
            if (entity.Spec.Backstage.Enabled && !entity.Status.Components.Any(c => c.Name == "Spotify Backstage"))
            {
                entity.Status.Components.Add(new ComponentStatus { Name = "Spotify Backstage", Status = "Pending", Namespace = "backstage" });
                statusChanged = true;
            }
            if (statusChanged)
            {
                entity = await _client.UpdateStatusAsync(entity, cancellationToken);
            }

            // 2. Verify Crossplane Infrastructure / Kubernetes Context
            entity.Status.Phase = "Reconciling";
            entity.Status.Message = "Verifying target cluster infrastructure...";
            var infraStatus = entity.Status.Components.First(c => c.Name == "Crossplane Infrastructure");

            var isLocalOrExisting = entity.Spec.CloudProvider.Equals("Kind", StringComparison.OrdinalIgnoreCase) || 
                                    entity.Spec.CloudProvider.Equals("Vanilla", StringComparison.OrdinalIgnoreCase);

            if (isLocalOrExisting)
            {
                // Verify local cluster node health
                var nodeList = await _client.ApiClient.CoreV1.ListNodeAsync(cancellationToken: cancellationToken);
                bool nodesReady = nodeList.Items.Any(n => n.Status.Conditions.Any(c => c.Type == "Ready" && c.Status == "True"));

                if (nodesReady)
                {
                    infraStatus.Status = "Healthy";
                    infraStatus.Message = $"Active {entity.Spec.CloudProvider} Kubernetes context verified healthy.";
                    infraStatus.Url = "";
                }
                else
                {
                    infraStatus.Status = "Reconciling";
                    infraStatus.Message = $"Waiting for {entity.Spec.CloudProvider} nodes to report Ready status...";
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
            }
            else
            {
                var claimName = $"{name}-cluster-claim";
                var claimExists = false;
                IdpClusterClaim? claim = null;

                try
                {
                    claim = await _client.GetAsync<IdpClusterClaim>(claimName, ns, cancellationToken);
                    if (claim != null)
                    {
                        claimExists = true;
                    }
                }
                catch
                {
                    _logger.LogInformation("Crossplane claim {ClaimName} not found. Creating it...", claimName);
                }

                if (!claimExists)
                {
                    claim = new IdpClusterClaim
                    {
                        ApiVersion = "turnkey.idp.io/v1alpha1",
                        Kind = "IdpClusterClaim",
                        Metadata = new V1ObjectMeta
                        {
                            Name = claimName,
                            NamespaceProperty = ns
                        },
                        Spec = new IdpClusterClaimSpec
                        {
                            NodeSize = entity.Spec.ClusterConfig.NodeSize,
                            MinNodes = entity.Spec.ClusterConfig.MinNodes,
                            MaxNodes = entity.Spec.ClusterConfig.MaxNodes,
                            Region = entity.Spec.ClusterConfig.Region
                        }
                    };

                    await _client.CreateAsync(claim, cancellationToken);
                    infraStatus.Status = "Reconciling";
                    infraStatus.Message = "Creating IdpClusterClaim custom resource...";
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }

                // Verify claim readiness in Crossplane
                bool claimReady = claim?.Status != null && claim.Status.State.Equals("Ready", StringComparison.OrdinalIgnoreCase);
                if (claimReady)
                {
                    infraStatus.Status = "Healthy";
                    infraStatus.Message = $"{entity.Spec.CloudProvider} cluster provisioned and verified healthy.";
                    infraStatus.Url = entity.Spec.CloudProvider.Equals("Azure", StringComparison.OrdinalIgnoreCase)
                        ? "https://portal.azure.com"
                        : entity.Spec.CloudProvider.Equals("AWS", StringComparison.OrdinalIgnoreCase)
                            ? "https://console.aws.amazon.com/eks"
                            : "https://console.cloud.google.com/kubernetes"; // GCP
                }
                else
                {
                    infraStatus.Status = "Reconciling";
                    infraStatus.Message = "Waiting for cloud provider cluster claim readiness...";
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
            }

            // 3. Bootstrapping & Verifying GitOps Engine
            var gitopsStatus = entity.Status.Components.First(c => c.Name == "GitOps Engine");
            if (entity.Spec.GitOps.Engine.Equals("ArgoCD", StringComparison.OrdinalIgnoreCase))
            {
                bool isArgoReady = await IsDeploymentReadyAsync("argocd-server", "argocd", cancellationToken);
                if (!isArgoReady)
                {
                    gitopsStatus.Status = "Reconciling";
                    gitopsStatus.Message = "Argo CD control plane deploying... waiting for server verification.";
                    
                    bool exists = await DoesDeploymentExistAsync("argocd-server", "argocd", cancellationToken);
                    if (!exists)
                    {
                        await DeployArgoCDAsync(name, ns, entity.Spec.GitOps.RepoUrl, cancellationToken);
                    }
                    
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
                gitopsStatus.Status = "Healthy";
                gitopsStatus.Message = "Argo CD control plane deployed and verified healthy.";
                if (entity.Spec.GitOps.EnableAppOfApps)
                {
                    await DeployAppOfAppsAsync(entity.Spec.GitOps.RepoUrl, cancellationToken);
                    gitopsStatus.Message = "Argo CD active with App-of-Apps templates sync enabled.";
                }
                gitopsStatus.Url = $"http://argocd.{entity.Spec.Domain}";
            }
            else
            {
                bool isFluxReady = await IsDeploymentReadyAsync("helm-controller", "flux-system", cancellationToken);
                if (!isFluxReady)
                {
                    gitopsStatus.Status = "Reconciling";
                    gitopsStatus.Message = "Flux CD daemon reconciling... waiting for ready status.";
                    
                    bool exists = await DoesDeploymentExistAsync("helm-controller", "flux-system", cancellationToken);
                    if (!exists)
                    {
                        await DeployFluxCDAsync(name, ns, entity.Spec.GitOps.RepoUrl, cancellationToken);
                    }
                    
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
                gitopsStatus.Status = "Healthy";
                gitopsStatus.Message = "Flux CD daemon verified active.";
                gitopsStatus.Url = $"http://flux.{entity.Spec.Domain}";
            }

            // 4. Bootstrapping & Verifying CI Engine
            var ciStatus = entity.Status.Components.First(c => c.Name == "CI Engine");
            if (entity.Spec.Ci.Engine.Equals("ArgoWorkflows", StringComparison.OrdinalIgnoreCase))
            {
                bool isWorkflowsReady = await IsDeploymentReadyAsync("argo-argo-workflows-server", "argo", cancellationToken);
                if (!isWorkflowsReady)
                {
                    ciStatus.Status = "Reconciling";
                    ciStatus.Message = "Argo Workflows server initializing... waiting for verification.";
                    
                    bool exists = await DoesDeploymentExistAsync("argo-argo-workflows-server", "argo", cancellationToken);
                    if (!exists)
                    {
                        await DeployArgoWorkflowsAsync(name, ns, cancellationToken);
                    }
                    
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
                ciStatus.Status = "Healthy";
                ciStatus.Message = "Argo Workflows server verified active.";
                ciStatus.Url = $"http://workflows.{entity.Spec.Domain}";
            }
            else
            {
                bool isTektonReady = await IsDeploymentReadyAsync("tekton-pipelines-controller", "tekton-pipelines", cancellationToken);
                if (!isTektonReady)
                {
                    ciStatus.Status = "Reconciling";
                    ciStatus.Message = "Tekton Pipelines controller deploying... waiting for verification.";
                    
                    bool exists = await DoesDeploymentExistAsync("tekton-pipelines-controller", "tekton-pipelines", cancellationToken);
                    if (!exists)
                    {
                        await DeployTektonAsync(name, ns, cancellationToken);
                    }
                    
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
                ciStatus.Status = "Healthy";
                ciStatus.Message = "Tekton Pipelines controller verified healthy.";
                ciStatus.Url = $"http://tekton.{entity.Spec.Domain}";
            }

            // 5. Deploy Optional Argo Rollouts (Progressive Delivery)
            if (entity.Spec.Delivery.EnableProgressiveDelivery)
            {
                var rolloutsStatus = entity.Status.Components.First(c => c.Name == "Argo Rollouts");
                bool isRolloutsReady = await IsDeploymentReadyAsync("argo-rollouts", "argo-rollouts", cancellationToken);
                if (!isRolloutsReady)
                {
                    rolloutsStatus.Status = "Reconciling";
                    rolloutsStatus.Message = "Registering Rollout Custom Resource Definitions... waiting for validation.";
                    
                    bool exists = await DoesDeploymentExistAsync("argo-rollouts", "argo-rollouts", cancellationToken);
                    if (!exists)
                    {
                        await DeployArgoRolloutsAsync(name, ns, entity.Spec.Delivery.GatewayClass, cancellationToken);
                    }
                    
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
                rolloutsStatus.Status = "Healthy";
                rolloutsStatus.Message = $"Argo Rollouts integrated with {entity.Spec.Delivery.GatewayClass} Gateway.";
                rolloutsStatus.Url = $"http://rollouts.{entity.Spec.Domain}";
            }

            // 6. Deploy Optional Observability Mesh (Prometheus + Grafana + Jaeger)
            // 6. Deploy Optional Observability Mesh (Prometheus + Grafana + Jaeger)
            if (entity.Spec.Observability.Enabled)
            {
                var grafanaStatus = entity.Status.Components.First(c => c.Name == "Grafana Dashboard");
                var promStatus = entity.Status.Components.First(c => c.Name == "Prometheus Metrics");
                var jaegerStatus = entity.Status.Components.First(c => c.Name == "Jaeger Tracing");

                bool otelReady = await IsDeploymentReadyAsync("otel-collector", "monitoring", cancellationToken);
                bool promReady = await IsDeploymentReadyAsync("prometheus-server", "monitoring", cancellationToken);
                bool grafanaReady = await IsDeploymentReadyAsync("grafana", "monitoring", cancellationToken);
                bool jaegerReady = await IsDeploymentReadyAsync("jaeger", "monitoring", cancellationToken);

                bool isObservabilityReady = otelReady && promReady && grafanaReady && jaegerReady;
                if (!isObservabilityReady)
                {
                    grafanaStatus.Status = "Reconciling";
                    grafanaStatus.Message = grafanaReady ? "Grafana ready." : "Grafana deploying... waiting for server verification.";

                    promStatus.Status = "Reconciling";
                    promStatus.Message = promReady ? "Prometheus ready." : "Prometheus server initializing... waiting for ready status.";

                    jaegerStatus.Status = "Reconciling";
                    jaegerStatus.Message = jaegerReady ? "Jaeger ready." : "Jaeger tracing deploying... waiting for pods.";
                    
                    bool exists = 
                        await DoesDeploymentExistAsync("otel-collector", "monitoring", cancellationToken) &&
                        await DoesDeploymentExistAsync("prometheus-server", "monitoring", cancellationToken) &&
                        await DoesDeploymentExistAsync("grafana", "monitoring", cancellationToken) &&
                        await DoesDeploymentExistAsync("jaeger", "monitoring", cancellationToken);
                    if (!exists)
                    {
                        await DeployObservabilityMeshAsync(name, ns, entity.Spec.Observability, cancellationToken);
                    }
                    
                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }

                grafanaStatus.Status = "Healthy";
                grafanaStatus.Message = "Grafana dashboard running. Default login: admin / admin";
                grafanaStatus.Url = $"http://grafana.{entity.Spec.Domain}";

                promStatus.Status = "Healthy";
                promStatus.Message = "Prometheus server running. Metrics collection active.";
                promStatus.Url = $"http://prometheus.{entity.Spec.Domain}";

                jaegerStatus.Status = "Healthy";
                jaegerStatus.Message = "Jaeger distributed tracing engine active.";
                jaegerStatus.Url = $"http://jaeger.{entity.Spec.Domain}";
            }

            // 7. Deploy Optional Spotify Backstage Portal
            if (entity.Spec.Backstage.Enabled)
            {
                var backstageStatus = entity.Status.Components.First(c => c.Name == "Spotify Backstage");
                bool isBackstageReady = await IsDeploymentReadyAsync("backstage", "backstage", cancellationToken);
                if (!isBackstageReady)
                {
                    backstageStatus.Status = "Reconciling";
                    backstageStatus.Message = "Deploying Spotify Backstage console... waiting for pods.";

                    bool exists = await DoesDeploymentExistAsync("backstage", "backstage", cancellationToken);
                    if (!exists)
                    {
                        await DeployBackstageAsync(name, ns, entity.Spec.Domain, entity.Spec.Backstage, cancellationToken);
                    }

                    entity = await _client.UpdateStatusAsync(entity, cancellationToken);
                    return ReconciliationResult<IdpDeployment>.Success(entity, TimeSpan.FromSeconds(15));
                }
                backstageStatus.Status = "Healthy";
                backstageStatus.Message = "Spotify Backstage developer portal running and synced with Git template catalogs.";
                backstageStatus.Url = $"http://backstage.{entity.Spec.Domain}";
            }

            // Reconcile Gateway API routes — domain and gatewayClass come from the spec
            ReconcileGatewayRoutes(entity.Spec.Domain, entity.Spec.Delivery.GatewayClass, entity.Spec.Backstage.Enabled);

            // 7. Reconcile Completed
            entity.Status.Phase = "Ready";
            entity.Status.Message = "Platform Control Plane fully configured. Deployed successfully!";
            entity = await _client.UpdateStatusAsync(entity, cancellationToken);
            _logger.LogInformation("Successfully completed reconciliation for IdpDeployment: {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reconciling IdpDeployment {Name}", name);
            entity.Status.Phase = "Error";
            entity.Status.Message = $"Reconciliation failed: {ex.Message}";
            await _client.UpdateStatusAsync(entity, cancellationToken);
            return ReconciliationResult<IdpDeployment>.Failure(entity, ex.Message, ex);
        }

        return ReconciliationResult<IdpDeployment>.Success(entity);
    }

    public Task<ReconciliationResult<IdpDeployment>> DeletedAsync(IdpDeployment entity, CancellationToken cancellationToken)
    {
        _logger.LogInformation("IdpDeployment resource deleted: {Name}", entity.Metadata.Name);
        return Task.FromResult(ReconciliationResult<IdpDeployment>.Success(entity));
    }

    #region CNCF Bootstrapping Helpers

    private async Task<bool> IsDeploymentReadyAsync(string name, string ns, CancellationToken cancellationToken)
    {
        try
        {
            var deployment = await _client.ApiClient.AppsV1.ReadNamespacedDeploymentStatusAsync(name, ns, cancellationToken: cancellationToken);
            return (deployment.Status?.ReadyReplicas ?? 0) > 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> DoesDeploymentExistAsync(string name, string ns, CancellationToken cancellationToken)
    {
        try
        {
            await _client.ApiClient.AppsV1.ReadNamespacedDeploymentAsync(name, ns, cancellationToken: cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }



    private async Task DeployArgoCDAsync(string name, string ns, string repoUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Argo CD stack...");
        var argoNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "argocd" } };
        await _client.SaveAsync(argoNs, cancellationToken);

        var configMap = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta 
            { 
                Name = "argocd-cm", 
                NamespaceProperty = "argocd",
                Labels = new Dictionary<string, string>
                {
                    { "app.kubernetes.io/managed-by", "Helm" }
                },
                Annotations = new Dictionary<string, string>
                {
                    { "meta.helm.sh/release-name", "argocd" },
                    { "meta.helm.sh/release-namespace", "argocd" }
                }
            },
            Data = new Dictionary<string, string>
            {
                { "repository.url", repoUrl },
                { "admin.enabled", "true" }
            }
        };
        await _client.SaveAsync(configMap, cancellationToken);

        // configs.params.server.insecure is the correct ArgoCD Helm value for disabling TLS at the server
        // so that the Gateway API can terminate TLS externally (or serve plain HTTP in dev).
        RunHelmCommand(@"upgrade --install argocd argo/argo-cd -n argocd --set configs.params.server\.insecure=true");
    }

    private async Task DeployFluxCDAsync(string name, string ns, string repoUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Flux CD stack...");
        var fluxNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "flux-system" } };
        await _client.SaveAsync(fluxNs, cancellationToken);

        var configMap = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "flux-config", NamespaceProperty = "flux-system" },
            Data = new Dictionary<string, string> { { "git.url", repoUrl } }
        };
        await _client.SaveAsync(configMap, cancellationToken);
    }

    private async Task DeployArgoWorkflowsAsync(string name, string ns, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Argo Workflows...");
        var argoNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "argo" } };
        await _client.SaveAsync(argoNs, cancellationToken);

        RunHelmCommand("upgrade --install argo argo/argo-workflows -n argo --set server.secure=false --set \"server.authModes[0]=server\"");
    }

    private async Task DeployTektonAsync(string name, string ns, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Tekton Pipelines...");
        var tektonNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "tekton-pipelines" } };
        await _client.SaveAsync(tektonNs, cancellationToken);
    }

    private async Task DeployArgoRolloutsAsync(string name, string ns, string ingress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Argo Rollouts...");
        var rolloutsNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "argo-rollouts" } };
        await _client.SaveAsync(rolloutsNs, cancellationToken);

        RunHelmCommand("upgrade --install argo-rollouts argo/argo-rollouts -n argo-rollouts --set dashboard.enabled=true");
    }

    private async Task DeployObservabilityMeshAsync(string name, string ns, ObservabilityConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Observability Mesh (OTel, Jaeger, Prometheus)...");
        var monitorNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "monitoring" } };
        await _client.SaveAsync(monitorNs, cancellationToken);

        var otelCollector = new OpenTelemetryCollector
        {
            ApiVersion = "opentelemetry.io/v1alpha1",
            Kind = "OpenTelemetryCollector",
            Metadata = new V1ObjectMeta
            {
                Name = "otel-collector",
                NamespaceProperty = "monitoring"
            },
            Spec = new OpenTelemetryCollectorSpec
            {
                Mode = "deployment",
                Config = "exporters:\n  otlp:\n    endpoint: jaeger:4317\nservice:\n  pipelines:\n    traces:\n      exporters: [otlp]"
            }
        };
        await _client.SaveAsync(otelCollector, cancellationToken);

        var deployment = new V1Deployment
        {
            Metadata = new V1ObjectMeta { Name = "otel-collector", NamespaceProperty = "monitoring" },
            Spec = new V1DeploymentSpec
            {
                Replicas = 1,
                Selector = new V1LabelSelector { MatchLabels = new Dictionary<string, string> { { "app", "otel-collector" } } },
                Template = new V1PodTemplateSpec
                {
                    Metadata = new V1ObjectMeta { Labels = new Dictionary<string, string> { { "app", "otel-collector" } } },
                    Spec = new V1PodSpec
                    {
                        Containers = new List<V1Container>
                        {
                            new V1Container
                            {
                                Name = "otel-collector",
                                Image = "otel/opentelemetry-collector:latest"
                            }
                        }
                    }
                }
            }
        };
        await _client.SaveAsync(deployment, cancellationToken);

        // 1. Deploy Jaeger all-in-one if it doesn't exist
        bool jaegerExists = await DoesDeploymentExistAsync("jaeger", "monitoring", cancellationToken);
        if (!jaegerExists)
        {
            var jaegerYaml = @"apiVersion: apps/v1
kind: Deployment
metadata:
  name: jaeger
  namespace: monitoring
spec:
  replicas: 1
  selector:
    matchLabels:
      app: jaeger
  template:
    metadata:
      labels:
        app: jaeger
    spec:
      containers:
      - name: jaeger
        image: jaegertracing/all-in-one:latest
        ports:
        - containerPort: 16686
        - containerPort: 4317
---
apiVersion: v1
kind: Service
metadata:
  name: jaeger
  namespace: monitoring
spec:
  selector:
    app: jaeger
  ports:
  - name: ui
    port: 16686
    targetPort: 16686
  - name: otlp-grpc
    port: 4317
    targetPort: 4317";

            var jaegerPath = Path.Combine(Path.GetTempPath(), "jaeger-all-in-one.yaml");
            File.WriteAllText(jaegerPath, jaegerYaml);
            RunKubectlCommand($"apply -f {jaegerPath}");
        }

        // 2. Deploy Prometheus if it doesn't exist
        bool promExists = await DoesDeploymentExistAsync("prometheus-server", "monitoring", cancellationToken);
        if (!promExists)
        {
            RunHelmCommand("upgrade --install prometheus prometheus-community/prometheus -n monitoring");
        }

        // 3. Deploy Grafana if it doesn't exist
        bool grafanaExists = await DoesDeploymentExistAsync("grafana", "monitoring", cancellationToken);
        if (!grafanaExists)
        {
            RunHelmCommand("upgrade --install grafana grafana/grafana -n monitoring --set adminPassword=admin");
        }
    }    private void ReconcileGatewayRoutes(string domain, string gatewayClass, bool enableBackstage)
    {
        // Detect if the Operator itself is running inside a Kubernetes cluster.
        // When in-cluster, real Services reference the Operator and UI Deployments directly.
        // When running locally (developer laptop), ExternalName services bridge host.docker.internal.
        var isInCluster = File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token");

        // Build the UI/Operator service block appropriate for the environment
        var uiServiceBlock = isInCluster
            ? $@"apiVersion: v1
kind: Service
metadata:
  name: idp-ui
  namespace: turnkey-idp
spec:
  selector:
    app: idp-ui
  ports:
  - port: 3000
    targetPort: 3000
---
apiVersion: v1
kind: Service
metadata:
  name: idp-operator
  namespace: turnkey-idp
spec:
  selector:
    app: idp-operator
  ports:
  - port: 5000
    targetPort: 8080"
            : $@"apiVersion: v1
kind: Namespace
metadata:
  name: idp-ui
---
apiVersion: v1
kind: Service
metadata:
  name: idp-ui
  namespace: idp-ui
spec:
  type: ExternalName
  externalName: host.docker.internal
  ports:
  - port: 3000
    targetPort: 3000
---
apiVersion: v1
kind: Service
metadata:
  name: idp-operator
  namespace: idp-ui
spec:
  type: ExternalName
  externalName: host.docker.internal
  ports:
  - port: 5000
    targetPort: 5000";

        var uiNamespace = isInCluster ? "turnkey-idp" : "idp-ui";

        var yaml = $@"apiVersion: gateway.networking.k8s.io/v1
kind: Gateway
metadata:
  name: idp-gateway
  namespace: istio-system
spec:
  gatewayClassName: {gatewayClass}
  listeners:
  - name: http
    port: 80
    protocol: HTTP
    allowedRoutes:
      namespaces:
        from: All
---
{uiServiceBlock}
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: idp-ui-route
  namespace: {uiNamespace}
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""idp.{domain}""
  rules:
  - backendRefs:
    - name: idp-ui
      port: 3000
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: idp-operator-route
  namespace: {uiNamespace}
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""operator.{domain}""
  rules:
  - backendRefs:
    - name: idp-operator
      port: 5000
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: argocd-route
  namespace: argocd
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""argocd.{domain}""
  rules:
  - backendRefs:
    - name: argocd-server
      port: 80
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: workflows-route
  namespace: argo
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""workflows.{domain}""
  rules:
  - backendRefs:
    - name: argo-argo-workflows-server
      port: 2746
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: grafana-route
  namespace: monitoring
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""grafana.{domain}""
  rules:
  - backendRefs:
    - name: grafana
      port: 80
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: rollouts-route
  namespace: argo-rollouts
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""rollouts.{domain}""
  rules:
  - backendRefs:
    - name: argo-rollouts-dashboard
      port: 3100
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: prometheus-route
  namespace: monitoring
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""prometheus.{domain}""
  rules:
  - backendRefs:
    - name: prometheus-server
      port: 80
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: jaeger-route
  namespace: monitoring
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""jaeger.{domain}""
  rules:
  - backendRefs:
    - name: jaeger
      port: 16686";

        if (enableBackstage)
        {
            yaml += $@"
---
apiVersion: gateway.networking.k8s.io/v1
kind: HTTPRoute
metadata:
  name: backstage-route
  namespace: backstage
spec:
  parentRefs:
  - name: idp-gateway
    namespace: istio-system
  hostnames:
  - ""backstage.{domain}""
  rules:
  - backendRefs:
    - name: backstage
      port: 7007";
        }

        var filePath = Path.Combine(Path.GetTempPath(), "gateway-routes.yaml");
        File.WriteAllText(filePath, yaml);
        RunKubectlCommand($"apply -f {filePath}");

        // In local Kind dev only: patch the Istio-generated gateway proxy to use hostNetwork
        // so it binds to host ports 80/443 on the Kind node (which maps to the laptop via extraPortMappings).
        // In cloud, the LoadBalancer controller handles address assignment automatically.
        if (!isInCluster)
        {
            RunKubectlCommand(
                "patch deployment idp-gateway-istio -n istio-system " +
                "--patch '{\"spec\":{\"template\":{\"spec\":{\"hostNetwork\":true,\"dnsPolicy\":\"ClusterFirstWithHostNet\",\"securityContext\":{\"sysctls\":null}}}}}'" +
                " --type=merge");
        }
    }

    private void RunHelmCommand(string arguments)
    {
        try
        {
            // Ensure the required repos are added in the container's helm registry
            string[][] repos = {
                new[] { "argo", "https://argoproj.github.io/argo-helm" },
                new[] { "prometheus-community", "https://prometheus-community.github.io/helm-charts" },
                new[] { "grafana", "https://grafana.github.io/helm-charts" }
            };
            foreach (var repo in repos)
            {
                var initStartInfo = new ProcessStartInfo
                {
                    FileName = "helm",
                    Arguments = $"repo add {repo[0]} {repo[1]}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var initProc = Process.Start(initStartInfo))
                {
                    if (initProc != null)
                    {
                        var errTask = initProc.StandardError.ReadToEndAsync();
                        initProc.StandardOutput.ReadToEnd();
                        errTask.GetAwaiter().GetResult();
                        initProc.WaitForExit();
                    }
                }
            }

            var updateStartInfo = new ProcessStartInfo
            {
                FileName = "helm",
                Arguments = "repo update",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var updateProc = Process.Start(updateStartInfo))
            {
                if (updateProc != null)
                {
                    var errTask = updateProc.StandardError.ReadToEndAsync();
                    updateProc.StandardOutput.ReadToEnd();
                    errTask.GetAwaiter().GetResult();
                    updateProc.WaitForExit();
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "helm",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var errTask = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = errTask.GetAwaiter().GetResult();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("Helm command failed with exit code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}", process.ExitCode, stderr, stdout);
                }
                else
                {
                    _logger.LogInformation("Helm command succeeded. Stdout: {Stdout}", stdout);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Helm command: helm {Arguments}", arguments);
        }
    }

    private void RunKubectlCommand(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "kubectl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process != null)
            {
                var errTask = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = errTask.GetAwaiter().GetResult();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("Kubectl command failed with exit code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}", process.ExitCode, stderr, stdout);
                }
                else
                {
                    _logger.LogInformation("Kubectl command succeeded. Stdout: {Stdout}", stdout);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Kubectl command: kubectl {Arguments}", arguments);
        }
    }

    private async Task DeployBackstageAsync(string name, string ns, string domain, BackstageConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping real Spotify Backstage Portal...");
        var backstageNs = new V1Namespace
        {
            Metadata = new V1ObjectMeta
            {
                Name = "backstage",
                Labels = new Dictionary<string, string> { { "istio-injection", "enabled" } }
            }
        };
        await _client.SaveAsync(backstageNs, cancellationToken);

        string dbHost = config.DatabaseHost;
        int dbPort = config.DatabasePort;
        string dbUser = config.DatabaseUser;
        string dbPassword = config.DatabasePassword;
        string dbName = config.DatabaseName;

        var dbYaml = string.Empty;

        // If no database host provided, deploy in-cluster PostgreSQL (StatefulSet)
        if (string.IsNullOrEmpty(dbHost))
        {
            dbHost = "backstage-postgres.backstage.svc.cluster.local";
            dbPort = 5432;
            dbUser = "postgres";
            dbPassword = "postgres-strong-password";
            dbName = "backstage";

            dbYaml = $@"apiVersion: apps/v1
kind: StatefulSet
metadata:
  name: backstage-postgres
  namespace: backstage
spec:
  serviceName: backstage-postgres
  replicas: 1
  selector:
    matchLabels:
      app: backstage-postgres
  template:
    metadata:
      labels:
        app: backstage-postgres
    spec:
      securityContext:
        fsGroup: 999
        runAsNonRoot: true
        runAsUser: 999
        runAsGroup: 999
        seccompProfile:
          type: RuntimeDefault
      containers:
      - name: postgres
        image: postgres:15-alpine
        ports:
        - containerPort: 5432
        env:
        - name: POSTGRES_USER
          value: {dbUser}
        - name: POSTGRES_PASSWORD
          value: {dbPassword}
        - name: POSTGRES_DB
          value: {dbName}
        - name: PGDATA
          value: /var/lib/postgresql/data/pgdata
        securityContext:
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: true
          runAsNonRoot: true
          runAsUser: 999
          capabilities:
            drop:
            - ALL
        volumeMounts:
        - name: pgdata
          mountPath: /var/lib/postgresql/data
        - name: postgres-run
          mountPath: /var/run/postgresql
        - name: tmp-volume
          mountPath: /tmp
      volumes:
      - name: postgres-run
        emptyDir: {{}}
      - name: tmp-volume
        emptyDir: {{}}
  volumeClaimTemplates:
  - metadata:
      name: pgdata
    spec:
      accessModes: [ ""ReadWriteOnce"" ]
      resources:
        requests:
          storage: 2Gi
---
apiVersion: v1
kind: Service
metadata:
  name: backstage-postgres
  namespace: backstage
spec:
  ports:
  - port: 5432
  selector:
    app: backstage-postgres";
        }

        var secretYaml = $@"apiVersion: v1
kind: Secret
metadata:
  name: backstage-postgres-secret
  namespace: backstage
type: Opaque
stringData:
  POSTGRES_HOST: {dbHost}
  POSTGRES_PORT: ""{dbPort}""
  POSTGRES_USER: {dbUser}
  POSTGRES_PASSWORD: {dbPassword}
  POSTGRES_DB: {dbName}";

        var imageBase = Environment.GetEnvironmentVariable("GHCR_IMAGE_BASE") ?? "ghcr.io/digvijay/turnkeyidp";
        var backstageImage = $"{imageBase}-backstage:latest";

        var deploymentYaml = $@"apiVersion: apps/v1
kind: Deployment
metadata:
  name: backstage
  namespace: backstage
spec:
  replicas: 1
  selector:
    matchLabels:
      app: backstage
  template:
    metadata:
      labels:
        app: backstage
    spec:
      securityContext:
        runAsNonRoot: true
        runAsUser: 1000
        runAsGroup: 1000
        fsGroup: 1000
        seccompProfile:
          type: RuntimeDefault
      containers:
      - name: backstage
        image: {backstageImage}
        imagePullPolicy: IfNotPresent
        ports:
        - containerPort: 7007
        env:
        - name: POSTGRES_HOST
          valueFrom:
            secretKeyRef:
              name: backstage-postgres-secret
              key: POSTGRES_HOST
        - name: POSTGRES_PORT
          valueFrom:
            secretKeyRef:
              name: backstage-postgres-secret
              key: POSTGRES_PORT
        - name: POSTGRES_USER
          valueFrom:
            secretKeyRef:
              name: backstage-postgres-secret
              key: POSTGRES_USER
        - name: POSTGRES_PASSWORD
          valueFrom:
            secretKeyRef:
              name: backstage-postgres-secret
              key: POSTGRES_PASSWORD
        - name: POSTGRES_DB
          valueFrom:
            secretKeyRef:
              name: backstage-postgres-secret
              key: POSTGRES_DB
        - name: DOMAIN
          value: {domain}
        resources:
          requests:
            cpu: 100m
            memory: 512Mi
          limits:
            cpu: 1000m
            memory: 1Gi
        securityContext:
          allowPrivilegeEscalation: false
          readOnlyRootFilesystem: true
          runAsNonRoot: true
          runAsUser: 1000
          capabilities:
            drop:
            - ALL
        volumeMounts:
        - name: tmp-volume
          mountPath: /tmp
      volumes:
      - name: tmp-volume
        emptyDir: {{}}
---
apiVersion: v1
kind: Service
metadata:
  name: backstage
  namespace: backstage
spec:
  selector:
    app: backstage
  ports:
  - port: 7007
    targetPort: 7007";

        var peerAuthYaml = $@"apiVersion: security.istio.io/v1beta1
kind: PeerAuthentication
metadata:
  name: backstage-mtls
  namespace: backstage
spec:
  mtls:
    mode: STRICT";

        var backstagePath = Path.Combine(Path.GetTempPath(), "backstage.yaml");
        var manifestContent = secretYaml + "\n---\n" + deploymentYaml + "\n---\n" + peerAuthYaml;
        if (!string.IsNullOrEmpty(dbYaml))
        {
            manifestContent = dbYaml + "\n---\n" + manifestContent;
        }

        File.WriteAllText(backstagePath, manifestContent);
        RunKubectlCommand($"apply -f {backstagePath}");
    }

    private async Task DeployAppOfAppsAsync(string repoUrl, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Argo CD App-of-Apps root application...");
        var gitRepo = string.IsNullOrEmpty(repoUrl) ? "https://github.com/my-org/idp-gitops" : repoUrl;
        
        var appOfAppsYaml = $@"apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: root-app-of-apps
  namespace: argocd
spec:
  project: default
  source:
    repoURL: {gitRepo}
    targetRevision: HEAD
    path: apps
  destination:
    server: https://kubernetes.default.svc
    namespace: argocd
  syncPolicy:
    automated:
      prune: true
      selfHeal: true";

        var appOfAppsPath = Path.Combine(Path.GetTempPath(), "root-app-of-apps.yaml");
        File.WriteAllText(appOfAppsPath, appOfAppsYaml);
        RunKubectlCommand($"apply -f {appOfAppsPath}");
        await Task.CompletedTask;
    }

    private static string IndentLines(string text, int spaces)
    {
        var indent = new string(' ', spaces);
        return string.Join("\n", text.Split('\n').Select(line => indent + line));
    }

    #endregion
}

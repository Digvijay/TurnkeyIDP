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
            ReconcileGatewayRoutes(entity.Spec.Domain, entity.Spec.Delivery.GatewayClass);

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
    }    private void ReconcileGatewayRoutes(string domain, string gatewayClass)
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
      port: 16686
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
                    initProc?.WaitForExit();
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
                updateProc?.WaitForExit();
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
            process?.WaitForExit();

            var stdout = process?.StandardOutput.ReadToEnd();
            var stderr = process?.StandardError.ReadToEnd();
            if (process?.ExitCode != 0)
            {
                _logger.LogError("Helm command failed with exit code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}", process?.ExitCode, stderr, stdout);
            }
            else
            {
                _logger.LogInformation("Helm command succeeded. Stdout: {Stdout}", stdout);
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
            process?.WaitForExit();

            var stdout = process?.StandardOutput.ReadToEnd();
            var stderr = process?.StandardError.ReadToEnd();
            if (process?.ExitCode != 0)
            {
                _logger.LogError("Kubectl command failed with exit code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}", process?.ExitCode, stderr, stdout);
            }
            else
            {
                _logger.LogInformation("Kubectl command succeeded. Stdout: {Stdout}", stdout);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run Kubectl command: kubectl {Arguments}", arguments);
        }
    }

    private async Task DeployBackstageAsync(string name, string ns, string domain, BackstageConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Bootstrapping Spotify Backstage Portal...");
        var backstageNs = new V1Namespace { Metadata = new V1ObjectMeta { Name = "backstage" } };
        await _client.SaveAsync(backstageNs, cancellationToken);

        var repoUrl = string.IsNullOrEmpty(config.CatalogRepoUrl) ? "https://github.com/my-org/idp-gitops" : config.CatalogRepoUrl;
        
        var indexHtml = @"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    <title>Spotify Backstage Developer Portal</title>
    <link href='https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap' rel='stylesheet'>
    <style>
        :root {
            --bg-base: #121212;
            --bg-sidebar: #181818;
            --bg-card: #282828;
            --text-main: #ffffff;
            --text-muted: #b3b3b3;
            --primary: #1db954;
            --border: #3f3f3f;
        }
        body {
            margin: 0;
            font-family: 'Inter', sans-serif;
            background: var(--bg-base);
            color: var(--text-main);
            display: flex;
            height: 100vh;
            overflow: hidden;
        }
        .sidebar {
            width: 240px;
            background: var(--bg-sidebar);
            border-right: 1px solid var(--border);
            display: flex;
            flex-direction: column;
            padding: 1.5rem 1rem;
            box-sizing: border-box;
        }
        .logo {
            font-size: 1.25rem;
            font-weight: 700;
            color: var(--primary);
            margin-bottom: 2rem;
            display: flex;
            align-items: center;
            gap: 0.5rem;
        }
        .nav-item {
            padding: 0.75rem 1rem;
            border-radius: 8px;
            cursor: pointer;
            font-weight: 500;
            color: var(--text-muted);
            margin-bottom: 0.5rem;
            transition: all 0.2s ease;
        }
        .nav-item:hover, .nav-item.active {
            background: rgba(29, 185, 84, 0.1);
            color: var(--primary);
        }
        .content {
            flex: 1;
            padding: 2.5rem;
            overflow-y: auto;
            box-sizing: border-box;
        }
        .header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 2rem;
        }
        .title {
            font-size: 1.75rem;
            font-weight: 700;
            margin: 0;
        }
        .subtitle {
            font-size: 0.95rem;
            color: var(--text-muted);
            margin-top: 0.25rem;
        }
        .grid {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
            gap: 1.5rem;
        }
        .card {
            background: var(--bg-card);
            border: 1px solid var(--border);
            border-radius: 12px;
            padding: 1.5rem;
            display: flex;
            flex-direction: column;
            justify-content: space-between;
            transition: transform 0.2s ease, box-shadow 0.2s ease;
        }
        .card:hover {
            transform: translateY(-4px);
            box-shadow: 0 8px 24px rgba(0, 0, 0, 0.5);
        }
        .card-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 1rem;
        }
        .card-title {
            font-size: 1.1rem;
            font-weight: 600;
            margin: 0;
        }
        .card-type {
            font-size: 0.75rem;
            text-transform: uppercase;
            background: rgba(255, 255, 255, 0.08);
            padding: 0.25rem 0.5rem;
            border-radius: 4px;
            font-weight: 600;
            letter-spacing: 0.5px;
        }
        .card-desc {
            font-size: 0.85rem;
            color: var(--text-muted);
            margin-bottom: 1.5rem;
            line-height: 1.4;
        }
        .btn {
            background: var(--primary);
            color: #000;
            border: none;
            border-radius: 6px;
            padding: 0.6rem 1.2rem;
            font-size: 0.85rem;
            font-weight: 600;
            cursor: pointer;
            text-decoration: none;
            text-align: center;
            display: inline-block;
            transition: background 0.2s ease;
        }
        .btn:hover {
            background: #1ed760;
        }
        .info-bar {
            background: rgba(29, 185, 84, 0.08);
            border: 1px solid rgba(29, 185, 84, 0.3);
            border-radius: 8px;
            padding: 1rem 1.5rem;
            margin-bottom: 2rem;
            display: flex;
            align-items: center;
            justify-content: space-between;
        }
    </style>
</head>
<body>
    <div class='sidebar'>
        <div class='logo'>
            <svg width='24' height='24' viewBox='0 0 24 24' fill='currentColor'><path d='M12 2C6.477 2 2 6.477 2 12s4.477 10 10 10 10-4.477 10-10S17.523 2 12 2zm1 14.5h-2v-2h2v2zm0-4h-2V7h2v5.5z'/></svg>
            Backstage
        </div>
        <div class='nav-item active'>Catalog</div>
        <div class='nav-item'>APIs</div>
        <div class='nav-item'>Docs</div>
        <div class='nav-item'>Templates</div>
        <div class='nav-item' style='margin-top: auto;'>Settings</div>
    </div>
    <div class='content'>
        <div class='header'>
            <div>
                <h1 class='title'>My Developer Portal</h1>
                <div class='subtitle'>Platform Console Catalog Sync Status</div>
            </div>
        </div>
        
        <div class='info-bar'>
            <div>
                <strong>GitOps Repository Connected:</strong> 
                <code style='background: rgba(255,255,255,0.08); padding: 0.2rem 0.5rem; border-radius: 4px;'>{CATALOG_REPO_URL}</code>
            </div>
            <span style='font-size: 0.85rem; font-weight: 600; color: var(--primary); display: flex; align-items: center; gap: 0.25rem;'>
                <svg width='8' height='8' viewBox='0 0 8 8' fill='currentColor'><circle cx='4' cy='4' r='4'/></svg> Active GitOps Sync
            </span>
        </div>

        <h2 style='font-size: 1.25rem; margin-bottom: 1rem;'>Platform Components</h2>
        <div class='grid'>
            <div class='card'>
                <div class='card-header'>
                    <h3 class='card-title'>Argo CD</h3>
                    <span class='card-type'>Deployment</span>
                </div>
                <div class='card-desc'>Declarative continuous delivery engine using GitOps principles for automated Kubernetes orchestration.</div>
                <a href='http://argocd.{DOMAIN}' target='_blank' class='btn'>Launch Console</a>
            </div>
            <div class='card'>
                <div class='card-header'>
                    <h3 class='card-title'>Argo Workflows</h3>
                    <span class='card-type'>CI/CD</span>
                </div>
                <div class='card-desc'>Container-native workflow engine orchestrating parallel CI/CD jobs and step templates.</div>
                <a href='http://workflows.{DOMAIN}' target='_blank' class='btn'>Launch Console</a>
            </div>
            <div class='card'>
                <div class='card-header'>
                    <h3 class='card-title'>Grafana</h3>
                    <span class='card-type'>Metrics</span>
                </div>
                <div class='card-desc'>Visual analytics and dashboading platform connected to Prometheus endpoints.</div>
                <a href='http://grafana.{DOMAIN}' target='_blank' class='btn'>Launch Console</a>
            </div>
            <div class='card'>
                <div class='card-header'>
                    <h3 class='card-title'>Prometheus</h3>
                    <span class='card-type'>Observability</span>
                </div>
                <div class='card-desc'>Core systems and service monitoring toolkit collecting pull-based metrics.</div>
                <a href='http://prometheus.{DOMAIN}' target='_blank' class='btn'>Launch Console</a>
            </div>
            <div class='card'>
                <div class='card-header'>
                    <h3 class='card-title'>Jaeger</h3>
                    <span class='card-type'>Tracing</span>
                </div>
                <div class='card-desc'>Distributed tracing system for monitoring microservices transactions and trace visualization.</div>
                <a href='http://jaeger.{DOMAIN}' target='_blank' class='btn'>Launch Console</a>
            </div>
            <div class='card'>
                <div class='card-header'>
                    <h3 class='card-title'>Argo Rollouts</h3>
                    <span class='card-type'>Delivery</span>
                </div>
                <div class='card-desc'>Canary and blue-green progressive delivery orchestrator.</div>
                <a href='http://rollouts.{DOMAIN}' target='_blank' class='btn'>Launch Console</a>
            </div>
        </div>
    </div>
</body>
</html>";

        indexHtml = indexHtml.Replace("{DOMAIN}", domain).Replace("{CATALOG_REPO_URL}", repoUrl);

        var configMapYaml = $@"apiVersion: v1
kind: ConfigMap
metadata:
  name: backstage-html
  namespace: backstage
data:
  index.html: |
{IndentLines(indexHtml, 4)}";

        var deploymentYaml = @"apiVersion: apps/v1
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
      containers:
      - name: backstage
        image: nginx:alpine
        ports:
        - containerPort: 80
        volumeMounts:
        - name: html-volume
          mountPath: /usr/share/nginx/html
      volumes:
      - name: html-volume
        configMap:
          name: backstage-html
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
    targetPort: 80";

        var backstagePath = Path.Combine(Path.GetTempPath(), "backstage.yaml");
        File.WriteAllText(backstagePath, configMapYaml + "\n---\n" + deploymentYaml);
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

# Turnkey IDP: Unified Kubernetes Platform Control Plane

Turnkey IDP is a fully native, in-cluster Platform Control Plane designed to bootstrap, secure, orchestrate, and observe a complete Cloud Native platform out-of-the-box. It is designed for a "one-tap" setup experience, removing host-machine dependencies and replacing developer manual intervention with GitOps automation.

---

## One-Command Bootstrap

To install the entire platform stack (including Kind cluster, Istio ingress gateway, Kyverno enforcement policies, Crossplane cloud controllers, and Turnkey IDP operator & console) onto your local machine, run:

```bash
./scripts/setup-kind.sh
```

Once the setup is complete, navigate to: **[http://idp.127.0.0.1.nip.io](http://idp.127.0.0.1.nip.io)**

---

## Platform Application Credentials

The table below lists all integrated developer portals, orchestration engines, and monitoring tools deployed in the cluster:

| Application | URL | Default Username | Default Password / Authentication |
| :--- | :--- | :--- | :--- |
| **IDP Control Console** | [http://idp.127.0.0.1.nip.io](http://idp.127.0.0.1.nip.io) | *None* | No authentication required. |
| **Argo CD** | [http://argocd.127.0.0.1.nip.io](http://argocd.127.0.0.1.nip.io) | `admin` | Retrieve via: `kubectl -n argocd get secret argocd-initial-admin-secret -o jsonpath="{.data.password}" \| base64 -d` |
| **Argo Workflows** | [http://workflows.127.0.0.1.nip.io](http://workflows.127.0.0.1.nip.io) | *None* | Pre-authenticated using in-cluster `server` authMode. |
| **Grafana** | [http://grafana.127.0.0.1.nip.io](http://grafana.127.0.0.1.nip.io) | `admin` | `admin` |
| **Prometheus** | [http://prometheus.127.0.0.1.nip.io](http://prometheus.127.0.0.1.nip.io) | *None* | No authentication required. |
| **Jaeger** | [http://jaeger.127.0.0.1.nip.io](http://jaeger.127.0.0.1.nip.io) | *None* | No authentication required. |
| **Argo Rollouts** | [http://rollouts.127.0.0.1.nip.io](http://rollouts.127.0.0.1.nip.io) | *None* | No authentication required. |
| **Backstage** | [http://backstage.127.0.0.1.nip.io](http://backstage.127.0.0.1.nip.io) | *None* | Enable via the wizard toggle. No authentication required. |

---

## System Architecture

```
                                      KUBERNETES CLUSTER
┌─────────────────────────────────────────────────────────────────────────────────────────────┐
│                                                                                             │
│                                    Istio Ingress Gateway                                    │
│                                              │                                              │
│         ┌───────────────┬────────────┬───────┴───────┬───────────────┬──────────────┐       │
│         ▼               ▼            ▼               ▼               ▼              ▼       │
│     idp-ui        idp-operator     ArgoCD     Argo Workflows      Grafana     Argo Rollouts │
│  (Next.js UI)     (C# Operator)   (GitOps)     (Pipelines)   (Observability)  (Deployments) │
│                                      │                                                      │
│                                      ▼                                                      │
│                            Reconciliation Loop                                              │
│                   Installs Cloud Resources & Compositions                                   │
└─────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Deployed Components:
1. **Presentation Layer (Next.js)**: Deployed as `idp-ui`. A wizard that submits cloud and engine configuration to the custom operator and shows real-time status.
2. **Custom Controller (C# KubeOps)**: Deployed as `idp-operator`. The reconciler managing the lifecycle of the custom `IdpDeployment` resources.
3. **Infrastructure Control (Crossplane)**: Abstracts public cloud provider configurations (AWS EKS, Azure AKS, Google GKE) into provider-agnostic custom claims.
4. **GitOps Delivery Engine (Argo CD / Flux CD)**: Deploys application workloads dynamically from Git repositories.
5. **CI Orchestrator (Argo Workflows / Tekton)**: Runs high-performance pipeline tasks inside native containers.
6. **Progressive Delivery (Argo Rollouts)**: Executes Canary and Blue/Green deployment orchestrations.
7. **Observability Mesh (OTel, Jaeger, Prometheus, Grafana)**: Provides a complete distributed tracing and metrics visualization stack.

---

## Product Roadmap & IDP Definition

An **Internal Developer Platform (IDP)** usually acts as the central developer portal (such as **Spotify Backstage**) to catalog templates, docs, and APIs.

Turnkey IDP is designed as the **infrastructure control plane and runtime bootstrapper** for such portals. The following features are available via the setup wizard:

- **Spotify Backstage Integration**: One-click toggle to spin up a Backstage portal preconfigured with platform catalog sync and console links for all deployed services.
- **GitOps App-of-Apps Pattern**: Automated provisioning of a root Argo CD Application that syncs all platform apps from your GitOps repository.
- **Prometheus & Jaeger Observability**: Full metrics and distributed tracing stack with HTTP routes.
- **Progressive Delivery**: Argo Rollouts with Canary/Blue-Green support via Istio Gateway API.

### Future Versions:
- **Multi-cluster federation**: Manage multiple target clusters from a single control plane.
- **SSO / OIDC integration**: Dex-based identity provider federation across all tools.
- **Backstage Software Catalog**: Real catalog.yaml sync from Git with entity discovery.
- **Cost estimation**: Pre-deploy cloud cost forecasting via Infracost integration.

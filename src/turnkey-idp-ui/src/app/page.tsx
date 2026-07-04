"use client";

import { useState, useEffect, useCallback } from "react";

// Types matching .NET entities
interface ComponentStatus {
  name: string;
  status: string; // Pending, Reconciling, Healthy, Degraded, Error
  message?: string;
  namespace?: string;
  url?: string;
}

interface IdpDeployment {
  metadata: {
    name: string;
    namespace?: string;
  };
  spec: {
    cloudProvider: string;
    clusterConfig: {
      nodeSize: string;
      minNodes: number;
      maxNodes: number;
      region: string;
    };
    gitOps: {
      engine: string;
      repoUrl: string;
    };
    ci: {
      engine: string;
    };
    delivery: {
      enableProgressiveDelivery: boolean;
      ingressController: string;
    };
    observability: {
      enabled: boolean;
      stack: {
        metrics: string;
        visualization: string;
        tracing: string;
      };
      metricsDays: number;
      tracesDays: number;
      storageClass: string;
    };
  };
  status?: {
    phase: string; // Ready, Reconciling, Degraded, Error
    message?: string;
    components: ComponentStatus[];
  };
}

export default function Home() {
  // Connection states
  const [operatorUrl, setOperatorUrl] = useState("");
  const [isOperatorConnected, setIsOperatorConnected] = useState<boolean | null>(null);
  const [activeDeployment, setActiveDeployment] = useState<IdpDeployment | null>(null);
  const [isLoading, setIsLoading] = useState(true);

  // Wizard form state
  const [wizardStep, setWizardStep] = useState(1);
  const [cloudProvider, setCloudProvider] = useState("Azure");
  const [domain, setDomain] = useState("idp.company.com");
  const [nodeSize, setNodeSize] = useState("Standard_D4s_v3");
  const [minNodes, setMinNodes] = useState(3);
  const [maxNodes, setMaxNodes] = useState(10);
  const [region, setRegion] = useState("eastus");
  const [gitOpsEngine, setGitOpsEngine] = useState("ArgoCD");
  const [gitRepoUrl, setGitRepoUrl] = useState("https://github.com/my-org/idp-gitops-repo");
  const [ciEngine, setCiEngine] = useState("ArgoWorkflows");
  const [enableProgressiveDelivery, setEnableProgressiveDelivery] = useState(true);
  const [gatewayClass, setGatewayClass] = useState("istio");
  const [enableObservability, setEnableObservability] = useState(true);
  const [enableBackstage, setEnableBackstage] = useState(false);
  const [backstageCatalogRepoUrl, setBackstageCatalogRepoUrl] = useState("https://github.com/my-org/idp-gitops");
  const [enableAppOfApps, setEnableAppOfApps] = useState(false);

  // Logs terminal modal state
  const [showLogModal, setShowLogModal] = useState(false);
  const [logModalComponent, setLogModalComponent] = useState<ComponentStatus | null>(null);
  const [terminalLogs, setTerminalLogs] = useState("");
  const [isFetchingLogs, setIsFetchingLogs] = useState(false);
  const [showTeardownConfirm, setShowTeardownConfirm] = useState(false);
  const [teardownMessage, setTeardownMessage] = useState<string | null>(null);

  // Check operator connection & active deployment status
  const checkDeploymentStatus = useCallback(async () => {
    if (!operatorUrl) return;
    try {
      const res = await fetch(`${operatorUrl}/api/deploy`, {
        method: "GET",
        headers: { "Content-Type": "application/json" },
      });
      setIsOperatorConnected(true);
      if (res.ok) {
        const data = await res.json();
        setActiveDeployment(data);
      } else if (res.status === 404) {
        setActiveDeployment(null);
      }
    } catch {
      setIsOperatorConnected(false);
      setActiveDeployment(null);
    } finally {
      setIsLoading(false);
    }
  }, [operatorUrl]);

  // Compute dynamic Operator API URL on load
  useEffect(() => {
    if (typeof window !== "undefined") {
      const hostname = window.location.hostname;
      let url: string;
      if (window.location.port === "3000") {
        // Local dev: Operator runs on same host, port 5000
        url = `${window.location.protocol}//${hostname}:5000`;
      } else if (hostname.startsWith("idp.")) {
        // Accessed via nip.io Gateway: Operator has its own subdomain route
        url = `${window.location.protocol}//${hostname.replace(/^idp\./, "operator.")}`;
      } else {
        // Production / cloud: same origin, path-based routing
        url = window.location.origin;
      }
      setOperatorUrl(url);
    }
  }, []);

  useEffect(() => {
    if (!operatorUrl) return;
    checkDeploymentStatus();
    // Poll status every 8 seconds if operator is connected
    const interval = setInterval(() => {
      checkDeploymentStatus();
    }, 8000);
    return () => clearInterval(interval);
  }, [checkDeploymentStatus, operatorUrl]);

  // Submit installer wizard payload
  const handleDeploy = async () => {
    setIsLoading(true);
    const spec = {
      cloudProvider,
      domain,
      clusterConfig: {
        nodeSize,
        minNodes: Number(minNodes),
        maxNodes: Number(maxNodes),
        region,
      },
      gitOps: {
        engine: gitOpsEngine,
        repoUrl: gitRepoUrl,
        enableAppOfApps,
      },
      ci: {
        engine: ciEngine,
      },
      delivery: {
        enableProgressiveDelivery,
        gatewayClass,
      },
      observability: {
        enabled: enableObservability,
        stack: {
          metrics: "Prometheus",
          visualization: "Grafana",
          tracing: "JaegerV2",
        },
        metricsDays: 15,
        tracesDays: 7,
        storageClass: "default",
      },
      backstage: {
        enabled: enableBackstage,
        catalogRepoUrl: backstageCatalogRepoUrl,
      },
    };

    try {
      const res = await fetch(`${operatorUrl}/api/deploy`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(spec),
      });

      if (res.ok) {
        await checkDeploymentStatus();
        setWizardStep(1); // Reset wizard
      } else {
        const err = await res.json();
        setTeardownMessage(`Failed to start deployment: ${err.Message || "Unknown error"}`);
      }
    } catch (ex: any) {
      setTeardownMessage(`Network error: ${ex.message}`);
    } finally {
      setIsLoading(false);
    }
  };

  // Teardown deployment custom resource
  const handleTeardown = async () => {
    setShowTeardownConfirm(false);
    setIsLoading(true);
    try {
      const res = await fetch(`${operatorUrl}/api/deploy`, {
        method: "DELETE",
      });
      if (res.ok) {
        setActiveDeployment(null);
        setTeardownMessage("Teardown initiated. The cluster resources are being garbage collected.");
      } else {
        setTeardownMessage("Teardown request failed. Check operator logs.");
      }
    } catch (ex: any) {
      setTeardownMessage(`Network error during teardown: ${ex.message}`);
    } finally {
      setIsLoading(false);
    }
  };

  // View logs for failing components
  const handleViewLogs = async (component: ComponentStatus) => {
    setLogModalComponent(component);
    setShowLogModal(true);
    setIsFetchingLogs(true);
    setTerminalLogs("Connecting to cluster node API...");

    try {
      const ns = component.namespace || "default";
      const res = await fetch(`${operatorUrl}/api/logs/${ns}`);
      if (res.ok) {
        const data = await res.json();
        setTerminalLogs(
          `[POD]: ${data.podName}\n[CONTAINER]: ${data.containerName}\n[STATUS]: ${data.status}\n\n[LOG OUTPUT]:\n${data.logs}`
        );
      } else {
        const err = await res.json();
        setTerminalLogs(`Error fetching logs: ${err.detail || "No failing pods found."}`);
      }
    } catch (ex: any) {
      setTerminalLogs(`Error: Failed to reach log API. ${ex.message}`);
    } finally {
      setIsFetchingLogs(false);
    }
  };

  return (
    <main className="container" style={{ flex: 1, display: "flex", flexDirection: "column" }}>
      {/* Header */}
      <header className="flex-between" style={{ paddingBottom: "2rem", borderBottom: "1px solid var(--card-border)", marginBottom: "2rem" }}>
        <div>
          <h1 style={{ fontSize: "1.8rem", fontWeight: "700" }}>
            <span className="text-gradient">Turnkey IDP</span> Control Plane
          </h1>
          <p style={{ color: "var(--muted)", fontSize: "0.9rem", marginTop: "0.25rem" }}>
            Golden Path Platform Bootstrapper & Diagnostics
          </p>
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: "1rem" }}>
          {/* Operator Url input */}
          <div style={{ display: "flex", alignItems: "center", gap: "0.5rem" }}>
            <span style={{ fontSize: "0.8rem", color: "var(--muted)" }}>API:</span>
            <span
              style={{
                background: "rgba(255,255,255,0.05)",
                border: "1px solid var(--card-border)",
                color: "#fff",
                padding: "0.3rem 0.6rem",
                borderRadius: "6px",
                fontSize: "0.8rem",
                fontFamily: "monospace",
              }}
            >
              {operatorUrl}
            </span>
          </div>
          {/* Connection Status Badge */}
          {isOperatorConnected === true ? (
            <span className="badge badge-success">● Connected</span>
          ) : isOperatorConnected === false ? (
            <span className="badge badge-error">● Offline</span>
          ) : (
            <span className="badge badge-warning">● Checking</span>
          )}
        </div>
      </header>

      {isLoading && !activeDeployment && (
        <div style={{ display: "flex", justifyContent: "center", alignItems: "center", flex: 1 }}>
          <div style={{ border: "4px solid rgba(255,255,255,0.1)", borderTop: "4px solid var(--primary)", borderRadius: "50%", width: "40px", height: "40px", animation: "spin 1s linear infinite" }}></div>
        </div>
      )}

      {/* Operator Unreachable state */}
      {!isLoading && isOperatorConnected === false && (
        <div className="glass-card" style={{ padding: "3rem", textAlign: "center", maxWidth: "600px", margin: "4rem auto" }}>
          <h2 style={{ color: "var(--error)", marginBottom: "1rem" }}>Operator Offline</h2>
          <p style={{ color: "var(--muted)", marginBottom: "2rem" }}>
            The UI is unable to communicate with the C# .NET Operator API at <strong>{operatorUrl}</strong>.
            Please verify that the operator project is running locally.
          </p>
          <div className="terminal-block" style={{ textAlign: "left", marginBottom: "2rem", color: "#a5f3fc" }}>
            # Start your .NET KubeOps Operator<br />
            $ cd turnkey-idp-operator<br />
            $ dotnet run --project src/TurnkeyIdp.Operator/
          </div>
          <button className="btn-primary" onClick={checkDeploymentStatus}>
            Retry Connection
          </button>
        </div>
      )}

      {/* Installer Wizard (Day-1 Flow) */}
      {!isLoading && isOperatorConnected && !activeDeployment && (
        <div className="glass-card" style={{ padding: "2.5rem", maxWidth: "800px", margin: "0 auto", width: "100%" }}>
          <div className="steps-container">
            <div className="steps-line"></div>
            <div
              className="steps-line-active"
              style={{ width: `${((wizardStep - 1) / 4) * 100}%` }}
            ></div>
            {[1, 2, 3, 4, 5].map((s) => (
              <div
                key={s}
                className={`step-node ${wizardStep === s ? "active" : ""} ${
                  wizardStep > s ? "completed" : ""
                }`}
              >
                {wizardStep > s ? "✓" : s}
              </div>
            ))}
          </div>

          {/* Wizard Step 1: Cloud Provider */}
          {wizardStep === 1 && (
            <div>
              <h2 style={{ marginBottom: "0.5rem" }}>Select Cloud Infrastructure Provider</h2>
              <p style={{ color: "var(--muted)", marginBottom: "2rem" }}>
                Select the target public cloud resource plane where Crossplane will orchestrate Kubernetes.
              </p>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(130px, 1fr))", gap: "1rem", marginBottom: "2rem" }}>
                <div
                  className={`provider-card ${cloudProvider === "Kind" ? "selected" : ""}`}
                  onClick={() => {
                    setCloudProvider("Kind");
                    setNodeSize("N/A");
                    setRegion("localhost");
                    setDomain("127.0.0.1.nip.io");
                    setGatewayClass("istio");
                  }}
                >
                  <span style={{ fontSize: "2rem", marginBottom: "0.5rem" }}>💻</span>
                  <span style={{ fontWeight: "700" }}>Kind</span>
                  <span style={{ fontSize: "0.75rem", color: "var(--muted)" }}>Local Dev Cluster</span>
                </div>
                <div
                  className={`provider-card ${cloudProvider === "Azure" ? "selected" : ""}`}
                  onClick={() => {
                    setCloudProvider("Azure");
                    setNodeSize("Standard_D4s_v3");
                    setRegion("eastus");
                    setDomain("idp.company.com");
                    setGatewayClass("azure");
                  }}
                >
                  <span style={{ fontSize: "2rem", marginBottom: "0.5rem" }}>🟦</span>
                  <span style={{ fontWeight: "700" }}>Azure</span>
                  <span style={{ fontSize: "0.75rem", color: "var(--muted)" }}>Azure AKS &amp; Compositions</span>
                </div>
                <div
                  className={`provider-card ${cloudProvider === "AWS" ? "selected" : ""}`}
                  onClick={() => {
                    setCloudProvider("AWS");
                    setNodeSize("m5.xlarge");
                    setRegion("us-east-1");
                    setDomain("idp.company.com");
                    setGatewayClass("aws");
                  }}
                >
                  <span style={{ fontSize: "2rem", marginBottom: "0.5rem" }}>🟧</span>
                  <span style={{ fontWeight: "700" }}>AWS</span>
                  <span style={{ fontSize: "0.75rem", color: "var(--muted)" }}>AWS EKS &amp; VPCs</span>
                </div>
                <div
                  className={`provider-card ${cloudProvider === "GCP" ? "selected" : ""}`}
                  onClick={() => {
                    setCloudProvider("GCP");
                    setNodeSize("n1-standard-4");
                    setRegion("us-central1");
                    setDomain("idp.company.com");
                    setGatewayClass("gke");
                  }}
                >
                  <span style={{ fontSize: "2rem", marginBottom: "0.5rem" }}>🟩</span>
                  <span style={{ fontWeight: "700" }}>GCP</span>
                  <span style={{ fontSize: "0.75rem", color: "var(--muted)" }}>Google GKE</span>
                </div>
                <div
                  className={`provider-card ${cloudProvider === "Vanilla" ? "selected" : ""}`}
                  onClick={() => {
                    setCloudProvider("Vanilla");
                    setNodeSize("N/A");
                    setRegion("existing-context");
                    setDomain("idp.company.com");
                    setGatewayClass("istio");
                  }}
                >
                  <span style={{ fontSize: "2rem", marginBottom: "0.5rem" }}>☸️</span>
                  <span style={{ fontWeight: "700" }}>Vanilla K8s</span>
                  <span style={{ fontSize: "0.75rem", color: "var(--muted)" }}>Self-managed / On-Prem</span>
                </div>
              </div>
              {/* Domain field — auto-set per provider, editable */}
              <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>
                  Platform Domain
                  <span style={{ fontWeight: "400", color: "var(--muted)", marginLeft: "0.5rem", fontSize: "0.8rem" }}>
                    {cloudProvider === "Kind" ? "(nip.io wildcard — auto-set for local dev)" : "(your DNS root, e.g. idp.company.com)"}
                  </span>
                </label>
                <input
                  type="text"
                  value={domain}
                  onChange={(e) => setDomain(e.target.value)}
                  disabled={cloudProvider === "Kind"}
                  placeholder="idp.company.com"
                  style={{
                    background: cloudProvider === "Kind" ? "rgba(255,255,255,0.03)" : "rgba(255,255,255,0.05)",
                    border: "1px solid var(--card-border)",
                    color: cloudProvider === "Kind" ? "var(--muted)" : "#fff",
                    padding: "0.6rem 0.8rem",
                    borderRadius: "6px",
                    fontFamily: "monospace",
                    fontSize: "0.9rem",
                    cursor: cloudProvider === "Kind" ? "not-allowed" : "text",
                  }}
                />
                <p style={{ fontSize: "0.75rem", color: "var(--muted)", marginTop: "0.25rem" }}>
                  Services will be available at: <code style={{ color: "var(--primary)" }}>argocd.{domain}</code>, <code style={{ color: "var(--primary)" }}>workflows.{domain}</code>, etc.
                </p>
              </div>
            </div>
          )}

          {/* Wizard Step 2: Sizing */}
          {wizardStep === 2 && (
            <div>
              <h2 style={{ marginBottom: "0.5rem" }}>Cluster Specifications & Capacity</h2>
              <p style={{ color: "var(--muted)", marginBottom: "2rem" }}>
                Configure region, cluster sizing, and autoscale thresholds.
              </p>
              {cloudProvider === "Kind" || cloudProvider === "Vanilla" ? (
                <div className="glass-card bg-gradient-accent" style={{ padding: "2rem", textAlign: "center", marginBottom: "2rem" }}>
                  <span style={{ fontSize: "2rem", display: "block", marginBottom: "0.5rem" }}>⚙️</span>
                  <h3 style={{ marginBottom: "0.5rem" }}>External Infrastructure Target</h3>
                  <p style={{ color: "var(--muted)", fontSize: "0.9rem", maxWidth: "500px", margin: "0 auto" }}>
                    The target environment ({cloudProvider}) uses an existing local or self-managed Kubernetes cluster. 
                    No new Crossplane infrastructure resources will be provisioned.
                  </p>
                </div>
              ) : (
                <>
                  <div className="grid-cols-2" style={{ marginBottom: "1.5rem" }}>
                    <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                      <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>Target region</label>
                      <input
                        type="text"
                        value={region}
                        onChange={(e) => setRegion(e.target.value)}
                        style={{
                          background: "rgba(255,255,255,0.05)",
                          border: "1px solid var(--card-border)",
                          color: "#fff",
                          padding: "0.6rem 0.8rem",
                          borderRadius: "6px",
                        }}
                      />
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                      <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>Node VM Size</label>
                      <input
                        type="text"
                        value={nodeSize}
                        onChange={(e) => setNodeSize(e.target.value)}
                        style={{
                          background: "rgba(255,255,255,0.05)",
                          border: "1px solid var(--card-border)",
                          color: "#fff",
                          padding: "0.6rem 0.8rem",
                          borderRadius: "6px",
                        }}
                      />
                    </div>
                  </div>
                  <div className="grid-cols-2" style={{ marginBottom: "2rem" }}>
                    <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                      <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>Min Node Count</label>
                      <input
                        type="number"
                        value={minNodes}
                        onChange={(e) => setMinNodes(Number(e.target.value))}
                        style={{
                          background: "rgba(255,255,255,0.05)",
                          border: "1px solid var(--card-border)",
                          color: "#fff",
                          padding: "0.6rem 0.8rem",
                          borderRadius: "6px",
                        }}
                      />
                    </div>
                    <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                      <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>Max Node Count</label>
                      <input
                        type="number"
                        value={maxNodes}
                        onChange={(e) => setMaxNodes(Number(e.target.value))}
                        style={{
                          background: "rgba(255,255,255,0.05)",
                          border: "1px solid var(--card-border)",
                          color: "#fff",
                          padding: "0.6rem 0.8rem",
                          borderRadius: "6px",
                        }}
                      />
                    </div>
                  </div>
                </>
              )}
            </div>
          )}

          {/* Wizard Step 3: GitOps & CI */}
          {wizardStep === 3 && (
            <div>
              <h2 style={{ marginBottom: "0.5rem" }}>GitOps Delivery & Pipelines</h2>
              <p style={{ color: "var(--muted)", marginBottom: "2rem" }}>
                Choose the GitOps operator and CI orchestration engines for code deployments.
              </p>
              <div className="grid-cols-2" style={{ marginBottom: "1.5rem" }}>
                <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                  <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>GitOps Delivery Engine</label>
                  <select
                    value={gitOpsEngine}
                    onChange={(e) => setGitOpsEngine(e.target.value)}
                    style={{
                      background: "rgba(0,0,0,0.7)",
                      border: "1px solid var(--card-border)",
                      color: "#fff",
                      padding: "0.6rem 0.8rem",
                      borderRadius: "6px",
                    }}
                  >
                    <option value="ArgoCD">Argo CD (GitOps Controller)</option>
                    <option value="FluxCD">Flux CD (GitOps Daemon)</option>
                  </select>
                </div>
                <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem" }}>
                  <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>CI Pipeline Runner</label>
                  <select
                    value={ciEngine}
                    onChange={(e) => setCiEngine(e.target.value)}
                    style={{
                      background: "rgba(0,0,0,0.7)",
                      border: "1px solid var(--card-border)",
                      color: "#fff",
                      padding: "0.6rem 0.8rem",
                      borderRadius: "6px",
                    }}
                  >
                    <option value="ArgoWorkflows">Argo Workflows (DAG-based)</option>
                    <option value="Tekton">Tekton (K8s Native Pipelines)</option>
                  </select>
                </div>
              </div>
              <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem", marginBottom: "2rem" }}>
                <label style={{ fontSize: "0.9rem", fontWeight: "600" }}>Central GitOps Repository URL</label>
                <input
                  type="text"
                  value={gitRepoUrl}
                  onChange={(e) => setGitRepoUrl(e.target.value)}
                  style={{
                    background: "rgba(255,255,255,0.05)",
                    border: "1px solid var(--card-border)",
                    color: "#fff",
                    padding: "0.6rem 0.8rem",
                    borderRadius: "6px",
                    width: "100%",
                  }}
                />
              </div>
            </div>
          )}

          {/* Wizard Step 4: Add-Ons */}
          {wizardStep === 4 && (
            <div>
              <h2 style={{ marginBottom: "0.5rem" }}>Add-Ons, Routing & Observability</h2>
              <p style={{ color: "var(--muted)", marginBottom: "2rem" }}>
                Toggle advanced features for deployment pipelines and monitoring.
              </p>
              <div style={{ display: "flex", flexDirection: "column", gap: "1.5rem", marginBottom: "2rem" }}>
                <label style={{ display: "flex", alignItems: "center", gap: "1rem", cursor: "pointer" }}>
                  <input
                    type="checkbox"
                    checked={enableProgressiveDelivery}
                    onChange={(e) => setEnableProgressiveDelivery(e.target.checked)}
                    style={{ width: "20px", height: "20px" }}
                  />
                  <div>
                    <span style={{ fontWeight: "700", display: "block" }}>Enable Progressive Delivery (Argo Rollouts)</span>
                    <span style={{ fontSize: "0.8rem", color: "var(--muted)" }}>
                      Deploy Canary and Blue/Green automated release verification.
                    </span>
                  </div>
                </label>

                {enableProgressiveDelivery && (
                  <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem", paddingLeft: "2.5rem" }}>
                    <label style={{ fontSize: "0.85rem", fontWeight: "600", color: "var(--muted)" }}>
                      Gateway Class
                      <span style={{ fontWeight: "400", marginLeft: "0.5rem", fontSize: "0.75rem" }}>(auto-set from provider)</span>
                    </label>
                    <select
                      value={gatewayClass}
                      onChange={(e) => setGatewayClass(e.target.value)}
                      style={{
                        background: "rgba(0,0,0,0.7)",
                        border: "1px solid var(--card-border)",
                        color: "#fff",
                        padding: "0.4rem 0.6rem",
                        borderRadius: "6px",
                        maxWidth: "260px",
                      }}
                    >
                      <option value="istio">Istio (Universal — Kind, Vanilla, EKS)</option>
                      <option value="azure">Azure Application Gateway (AKS)</option>
                      <option value="gke">GKE Gateway Controller (GKE)</option>
                      <option value="aws">AWS Gateway API Controller (EKS)</option>
                      <option value="nginx-gateway">NGINX Gateway Fabric</option>
                    </select>
                  </div>
                )}

                <label style={{ display: "flex", alignItems: "center", gap: "1rem", cursor: "pointer" }}>
                  <input
                    type="checkbox"
                    checked={enableObservability}
                    onChange={(e) => setEnableObservability(e.target.checked)}
                    style={{ width: "20px", height: "20px" }}
                  />
                  <div>
                    <span style={{ fontWeight: "700", display: "block" }}>Enable Zero-Touch Observability Mesh</span>
                    <span style={{ fontSize: "0.8rem", color: "var(--muted)" }}>
                      Deploy Prometheus, Grafana, and Jaeger v2 with OpenTelemetry namespace auto-injection.
                    </span>
                  </div>
                </label>

                <label style={{ display: "flex", alignItems: "center", gap: "1rem", cursor: "pointer" }}>
                  <input
                    type="checkbox"
                    checked={enableBackstage}
                    onChange={(e) => setEnableBackstage(e.target.checked)}
                    style={{ width: "20px", height: "20px" }}
                  />
                  <div>
                    <span style={{ fontWeight: "700", display: "block" }}>Enable Spotify Backstage Developer Portal</span>
                    <span style={{ fontSize: "0.8rem", color: "var(--muted)" }}>
                      Spin up a Backstage portal preconfigured with platform catalog sync and console links.
                    </span>
                  </div>
                </label>

                {enableBackstage && (
                  <div style={{ display: "flex", flexDirection: "column", gap: "0.5rem", paddingLeft: "2.5rem" }}>
                    <label style={{ fontSize: "0.85rem", fontWeight: "600", color: "var(--muted)" }}>
                      Catalog Git Repository URL
                    </label>
                    <input
                      type="text"
                      value={backstageCatalogRepoUrl}
                      onChange={(e) => setBackstageCatalogRepoUrl(e.target.value)}
                      placeholder="https://github.com/my-org/idp-gitops"
                      style={{
                        background: "rgba(0,0,0,0.7)",
                        border: "1px solid var(--card-border)",
                        color: "#fff",
                        padding: "0.5rem 0.75rem",
                        borderRadius: "6px",
                        width: "100%",
                        maxWidth: "480px",
                        fontFamily: "monospace",
                        fontSize: "0.85rem",
                      }}
                    />
                  </div>
                )}

                <label style={{ display: "flex", alignItems: "center", gap: "1rem", cursor: "pointer" }}>
                  <input
                    type="checkbox"
                    checked={enableAppOfApps}
                    onChange={(e) => setEnableAppOfApps(e.target.checked)}
                    style={{ width: "20px", height: "20px" }}
                  />
                  <div>
                    <span style={{ fontWeight: "700", display: "block" }}>Enable GitOps App-of-Apps Pattern</span>
                    <span style={{ fontSize: "0.8rem", color: "var(--muted)" }}>
                      Create an Argo CD root Application syncing all platform apps from your GitOps repository.
                    </span>
                  </div>
                </label>
              </div>
            </div>
          )}

          {/* Wizard Step 5: Review */}
          {wizardStep === 5 && (
            <div>
              <h2 style={{ marginBottom: "0.5rem" }}>Review Platform Configuration</h2>
              <p style={{ color: "var(--muted)", marginBottom: "2.0rem" }}>
                Confirm details before creating custom resources in the Kubernetes cluster.
              </p>
              <div
                style={{
                  background: "rgba(255,255,255,0.02)",
                  borderRadius: "8px",
                  border: "1px solid var(--card-border)",
                  padding: "1.5rem",
                  marginBottom: "2rem",
                  fontSize: "0.9rem",
                  display: "flex",
                  flexDirection: "column",
                  gap: "0.75rem",
                }}
              >
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>Provider Plane:</span>
                  <strong>{cloudProvider}</strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>Sizing & Region:</span>
                  <strong>{nodeSize} ({region})</strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>Scale:</span>
                  <strong>{minNodes} to {maxNodes} Nodes</strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>GitOps Delivery:</span>
                  <strong>{gitOpsEngine}</strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>CI pipeline:</span>
                  <strong>{ciEngine}</strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>Git Repository:</span>
                  <span style={{ fontFamily: "monospace", fontSize: "0.8rem" }}>{gitRepoUrl}</span>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>Add-Ons:</span>
                  <strong>
                    {enableProgressiveDelivery ? "Argo Rollouts (Canary), " : ""}
                    {enableObservability ? "OTel Metrics & Traces (Jaeger v2)" : "None"}
                  </strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>Backstage Portal:</span>
                  <strong>{enableBackstage ? `Enabled — ${backstageCatalogRepoUrl}` : "Disabled"}</strong>
                </div>
                <div>
                  <span style={{ color: "var(--muted)", width: "180px", display: "inline-block" }}>App-of-Apps:</span>
                  <strong>{enableAppOfApps ? "Enabled (root-app-of-apps -> argocd)" : "Disabled"}</strong>
                </div>
              </div>
            </div>
          )}

          {/* Navigation buttons */}
          <div className="flex-between">
            {wizardStep > 1 ? (
              <button className="btn-secondary" onClick={() => setWizardStep((prev) => prev - 1)}>
                Back
              </button>
            ) : (
              <div></div>
            )}

            {wizardStep < 5 ? (
              <button className="btn-primary" onClick={() => setWizardStep((prev) => prev + 1)}>
                Next Step
              </button>
            ) : (
              <button className="btn-primary" onClick={handleDeploy}>
                Deploy platform
              </button>
            )}
          </div>
        </div>
      )}

      {/* Control Panel Dashboard (Day-2 Flow) */}
      {!isLoading && isOperatorConnected && activeDeployment && (
        <div style={{ display: "flex", flexDirection: "column", gap: "2rem" }}>
          {/* Dashboard Summary Banner */}
          <div className="glass-card bg-gradient-accent" style={{ padding: "2rem", display: "flex", justifyContent: "space-between", alignItems: "center" }}>
            <div>
              <div style={{ display: "flex", alignItems: "center", gap: "1rem" }}>
                <h2 style={{ fontSize: "1.5rem" }}>Platform Status Dashboard</h2>
                {activeDeployment.status?.phase === "Ready" ? (
                  <span className="badge badge-success">Status: Ready</span>
                ) : activeDeployment.status?.phase === "Error" ? (
                  <span className="badge badge-error">Status: Error</span>
                ) : (
                  <span className="badge badge-warning" style={{ display: "flex", gap: "0.5rem" }}>
                    <div style={{ border: "2px solid transparent", borderTop: "2px solid #fff", borderRadius: "50%", width: "12px", height: "12px", animation: "spin 1s linear infinite" }}></div>
                    Orchestrating
                  </span>
                )}
              </div>
              <p style={{ color: "var(--muted)", marginTop: "0.5rem", fontSize: "0.9rem" }}>
                {activeDeployment.status?.message || "Platform reconciliation in progress..."}
              </p>
            </div>
            {showTeardownConfirm ? (
              <div style={{ display: "flex", alignItems: "center", gap: "0.75rem", flexShrink: 0 }}>
                <span style={{ fontSize: "0.85rem", color: "var(--error)", fontWeight: 600 }}>Warning: This will delete all platform resources!</span>
                <button
                  id="confirm-teardown-btn"
                  className="btn-secondary"
                  style={{ borderColor: "var(--error)", color: "var(--error)", fontWeight: 700 }}
                  onClick={handleTeardown}
                >
                  Yes, Tear Down
                </button>
                <button
                  className="btn-secondary"
                  onClick={() => setShowTeardownConfirm(false)}
                >
                  Cancel
                </button>
              </div>
            ) : (
              <button
                id="teardown-btn"
                className="btn-secondary"
                style={{ borderColor: "var(--error)", color: "var(--error)", flexShrink: 0 }}
                onClick={() => setShowTeardownConfirm(true)}
              >
                Tear Down Platform
              </button>
            )}
          </div>

          {/* Notification Banner */}
          {teardownMessage && (
            <div style={{
              background: "rgba(239,68,68,0.12)",
              border: "1px solid rgba(239,68,68,0.4)",
              borderRadius: "8px",
              padding: "0.85rem 1.25rem",
              display: "flex",
              justifyContent: "space-between",
              alignItems: "center",
              fontSize: "0.9rem",
            }}>
              <span>{teardownMessage}</span>
              <button
                onClick={() => setTeardownMessage(null)}
                style={{ background: "none", border: "none", color: "inherit", cursor: "pointer", fontSize: "1.1rem", padding: "0 0.25rem" }}
              >x</button>
            </div>
          )}

          {/* Deployed Component Grid */}
          <section>
            <h3 style={{ marginBottom: "1rem", fontSize: "1.1rem" }}>Infrastructure & Control Plane Components</h3>
            <div className="grid-cols-3">
              {activeDeployment.status?.components?.map((component, idx) => (
                <div key={idx} className="glass-card" style={{ padding: "1.5rem", display: "flex", flexDirection: "column", justifyContent: "space-between", minHeight: "180px" }}>
                  <div>
                    <div className="flex-between" style={{ marginBottom: "0.5rem" }}>
                      <strong style={{ fontSize: "1rem" }}>{component.name}</strong>
                      {component.status === "Healthy" ? (
                        <span className="badge badge-success">Healthy</span>
                      ) : component.status === "Error" ? (
                        <span className="badge badge-error">Error</span>
                      ) : component.status === "Reconciling" ? (
                        <span className="badge badge-warning">Reconciling</span>
                      ) : (
                        <span className="badge" style={{ backgroundColor: "#27272a", color: "#a1a1aa" }}>Pending</span>
                      )}
                    </div>
                    <p style={{ fontSize: "0.8rem", color: "var(--muted)", marginBottom: "1rem" }}>
                      Namespace: <code style={{ color: "#fff", background: "rgba(255,255,255,0.05)", padding: "0.1rem 0.3rem", borderRadius: "4px" }}>{component.namespace}</code>
                    </p>
                    <p style={{ fontSize: "0.85rem", color: "#e4e4e7" }}>
                      {component.message || "Waiting for component allocation..."}
                    </p>
                  </div>
                  <div style={{ marginTop: "1rem", display: "flex", gap: "0.5rem" }}>
                    {component.url && (
                      <a
                        href={component.url}
                        target="_blank"
                        rel="noreferrer"
                        className="btn-secondary"
                        style={{ flex: 1, textAlign: "center", fontSize: "0.8rem", padding: "0.5rem" }}
                      >
                        Launch Console
                      </a>
                    )}
                    {component.status === "Error" || component.status === "Reconciling" ? (
                      <button
                        className="btn-secondary"
                        style={{ flex: 1, fontSize: "0.8rem", padding: "0.5rem", borderColor: "var(--warning)", color: "var(--warning)" }}
                        onClick={() => handleViewLogs(component)}
                      >
                        View Logs
                      </button>
                    ) : null}
                  </div>
                </div>
              ))}
            </div>
          </section>
        </div>
      )}

      {/* Terminal Log Modal */}
      {showLogModal && logModalComponent && (
        <div
          style={{
            position: "fixed",
            top: 0,
            left: 0,
            width: "100%",
            height: "100%",
            background: "rgba(0,0,0,0.8)",
            display: "flex",
            alignItems: "center",
            justifyContent: "center",
            zIndex: 100,
            padding: "2rem",
          }}
        >
          <div className="glass-card" style={{ maxWidth: "800px", width: "100%", padding: "1.5rem", border: "1px solid var(--warning)" }}>
            <div className="flex-between" style={{ marginBottom: "1rem" }}>
              <h4>Diagnostics Logs: {logModalComponent.name}</h4>
              <button
                className="btn-secondary"
                style={{ padding: "0.25rem 0.5rem", fontSize: "0.8rem" }}
                onClick={() => setShowLogModal(false)}
              >
                Close Terminal
              </button>
            </div>
            <div className="terminal-header">
              <span>logs: namespace/{logModalComponent.namespace}</span>
              {isFetchingLogs ? <span>● Streaming...</span> : <span>● Complete</span>}
            </div>
            <pre className="terminal-block">{terminalLogs}</pre>
          </div>
        </div>
      )}
    </main>
  );
}

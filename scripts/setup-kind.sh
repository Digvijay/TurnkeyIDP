#!/usr/bin/env bash
set -euo pipefail

CLUSTER_NAME="idp-dev-cluster"

# Check if kind is installed
if ! command -v kind &> /dev/null; then
    echo "Error: kind is not installed. Please install it first."
    exit 1
fi

# Check if helm is installed
if ! command -v helm &> /dev/null; then
    echo "Error: helm is not installed. Please install it first."
    exit 1
fi

# Check if kubectl is installed
if ! command -v kubectl &> /dev/null; then
    echo "Error: kubectl is not installed. Please install it first."
    exit 1
fi

# Check if docker is running
if ! docker info &> /dev/null; then
    echo "Error: Docker is not running. Please start Docker first."
    exit 1
fi

echo "Creating kind cluster: ${CLUSTER_NAME}..."
kind create cluster --name ${CLUSTER_NAME} --config - <<EOF
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
- role: control-plane
  extraPortMappings:
  - containerPort: 80
    hostPort: 80
    protocol: TCP
  - containerPort: 443
    hostPort: 443
    protocol: TCP
EOF

echo "Adding Helm repositories..."
helm repo add crossplane-stable https://charts.crossplane.io/stable
helm repo add kyverno https://kyverno.github.io/kyverno/
helm repo add istio https://istio-release.storage.googleapis.com/charts
helm repo add argo https://argoproj.github.io/argo-helm
helm repo update

echo "Installing Crossplane..."
helm install crossplane --namespace crossplane-system --create-namespace crossplane-stable/crossplane

echo "Installing Kyverno (Compliance Policies)..."
helm install kyverno --namespace kyverno --create-namespace kyverno/kyverno
kubectl rollout status deployment/kyverno-admission-controller -n kyverno --timeout=300s
echo "Waiting 10s for Kyverno webhooks to initialize..."
sleep 10
helm install kyverno-policies --namespace kyverno kyverno/kyverno-policies

echo "Installing Gateway API CRDs (Standard channel v1.1.0)..."
kubectl apply -f https://github.com/kubernetes-sigs/gateway-api/releases/download/v1.1.0/standard-install.yaml

echo "Installing Istio control plane (istiod only — gateway is managed by the Operator)..."
helm install istio-base istio/base --namespace istio-system --create-namespace --wait
helm install istiod istio/istiod --namespace istio-system --wait

echo "Building Turnkey IDP Docker images..."
echo "Building Spotify Backstage Docker image (multi-stage)..."
docker build -t turnkey-idp-backstage:latest "$(dirname "$0")/../src/backstage"

docker build -t turnkey-idp-operator:latest -f "$(dirname "$0")/../src/TurnkeyIdp.Operator/Dockerfile.aot" "$(dirname "$0")/../src/TurnkeyIdp.Operator"
docker build -t turnkey-idp-ui:latest "$(dirname "$0")/../src/turnkey-idp-ui"

echo "Loading Docker images into Kind cluster..."
kind load docker-image turnkey-idp-backstage:latest --name ${CLUSTER_NAME}
kind load docker-image turnkey-idp-operator:latest --name ${CLUSTER_NAME}
kind load docker-image turnkey-idp-ui:latest --name ${CLUSTER_NAME}

echo "Creating and labeling namespace turnkey-idp for Istio injection..."
kubectl create namespace turnkey-idp || true
kubectl label namespace turnkey-idp istio-injection=enabled --overwrite

echo "Installing Turnkey IDP via Helm..."
# We install our Helm chart in the turnkey-idp namespace.
# It registers all CRDs, operator, UI, and default gateway routes.
helm upgrade --install turnkey-idp "$(dirname "$0")/../charts/turnkey-idp" \
  --namespace turnkey-idp --wait \
  --set operator.image.repository=turnkey-idp-operator \
  --set operator.image.tag=latest \
  --set operator.image.pullPolicy=IfNotPresent \
  --set ui.image.repository=turnkey-idp-ui \
  --set ui.image.tag=latest \
  --set ui.image.pullPolicy=IfNotPresent

echo "Applying hostNetwork patch to Istio gateway..."
# Wait for the deployment to exist, then patch it to bind host ports 80/443 in local dev
kubectl rollout status deployment/idp-gateway-istio -n istio-system --timeout=300s
kubectl patch deployment idp-gateway-istio -n istio-system \
  --patch '{"spec":{"template":{"spec":{"hostNetwork":true,"dnsPolicy":"ClusterFirstWithHostNet","securityContext":{"sysctls":null}}}}}' \
  --type=merge

echo ""
echo "✅ Turnkey IDP is fully installed and running inside Kubernetes!"
echo ""
echo "Open the dashboard at: http://idp.127.0.0.1.nip.io"


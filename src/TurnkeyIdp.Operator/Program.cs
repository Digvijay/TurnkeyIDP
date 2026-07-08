using k8s;
using k8s.Models;
using KubeOps.Operator;
using KubeOps.KubernetesClient;
using Microsoft.AspNetCore.Mvc;
using TurnkeyIdp.Operator.Controllers;
using TurnkeyIdp.Operator.Entities;
using TurnkeyIdp.Operator;

using System.Text.Json.Serialization.Metadata;

// Register source generator context for the Kubernetes client
k8s.KubernetesJson.AddJsonOptions(options =>
{
    if (options.TypeInfoResolver != null)
    {
        options.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            OperatorJsonContext.Default,
            options.TypeInfoResolver
        );
    }
    else
    {
        options.TypeInfoResolver = OperatorJsonContext.Default;
    }
});

var builder = WebApplication.CreateBuilder(args);

// Allow requests from the Next.js console
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, OperatorJsonContext.Default);
});

// Register KubeOps operator services and controllers
builder.Services
    .AddKubernetesOperator()
    .AddController<IdpDeploymentController, IdpDeployment>();

var app = builder.Build();

app.Use((context, next) =>
{
    if (context.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
    {
        context.Response.Headers.Append("Access-Control-Allow-Private-Network", "true");
    }
    return next();
});

app.UseCors();

// GET /api/deploy: Retrieve the active deployment custom resource
app.MapGet("/api/deploy", async ([FromServices] IKubernetesClient client) =>
{
    try
    {
        var deployment = await client.GetAsync<IdpDeployment>("primary-idp-cluster", "default");
        if (deployment == null)
        {
            return Results.NotFound(new { Message = "No active deployment found." });
        }
        return Results.Ok(deployment);
    }
    catch
    {
        return Results.NotFound(new { Message = "No active deployment found." });
    }
});

// POST /api/deploy: Create a new deployment from the Next.js UI wizard
app.MapPost("/api/deploy", async ([FromBody] IdpDeploymentSpec spec, [FromServices] IKubernetesClient client) =>
{
    try
    {
        // Check if already exists to prevent duplication
        bool exists = false;
        try
        {
            var existing = await client.GetAsync<IdpDeployment>("primary-idp-cluster", "default");
            if (existing != null)
            {
                exists = true;
            }
        }
        catch {}

        if (exists)
        {
            return Results.BadRequest(new { Message = "An active deployment already exists." });
        }

        var deployment = new IdpDeployment
        {
            ApiVersion = "turnkey.idp.io/v1alpha1",
            Kind = "IdpDeployment",
            Metadata = new V1ObjectMeta
            {
                Name = "primary-idp-cluster",
                NamespaceProperty = "default"
            },
            Spec = spec
        };

        await client.CreateAsync(deployment);
        return Results.Ok(new { Message = "Deployment initiated successfully." });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to create deployment custom resource: {ex.Message}");
    }
});

// DELETE /api/deploy: Tear down the active deployment
app.MapDelete("/api/deploy", async ([FromServices] IKubernetesClient client) =>
{
    try
    {
        var deployment = await client.GetAsync<IdpDeployment>("primary-idp-cluster", "default");
        if (deployment != null)
        {
            await client.DeleteAsync(deployment);
            return Results.Ok(new { Message = "Deployment teardown initiated." });
        }
        return Results.NotFound(new { Message = "No active deployment found to delete." });
    }
    catch
    {
        return Results.NotFound(new { Message = "No active deployment found to delete." });
    }
});

// Day-2 Diagnostics API: stream pod logs for failing components
app.MapGet("/api/logs/{namespace}", async (string @namespace, [FromServices] IKubernetesClient client) =>
{
    try
    {
        var k8sClient = client.ApiClient;
        
        // 1. Fetch all pods in the target namespace
        var podList = await k8sClient.CoreV1.ListNamespacedPodAsync(@namespace);
        
        // 2. Identify a failing or non-ready pod
        var targetPod = podList.Items.FirstOrDefault(p => 
            p.Status.Phase == "Failed" || 
            p.Status.ContainerStatuses?.Any(c => c.Ready == false) == true);

        // Fallback: If no explicitly failing pod, use the most recent pod
        targetPod ??= podList.Items.OrderByDescending(p => p.Metadata.CreationTimestamp).FirstOrDefault();

        if (targetPod == null)
        {
            return Results.NotFound(new { Message = $"No pods found in namespace {@namespace}." });
        }

        // 3. Extract the target container name
        var containerName = targetPod.Spec.Containers.FirstOrDefault()?.Name;
        if (string.IsNullOrEmpty(containerName))
        {
            return Results.NotFound(new { Message = $"No containers found in pod {targetPod.Metadata.Name}." });
        }

        // 4. Retrieve logs (checking previous execution if it crashed/restarted)
        string logs = "";
        try
        {
            using var response = await k8sClient.CoreV1.ReadNamespacedPodLogAsync(
                name: targetPod.Metadata.Name,
                namespaceParameter: @namespace,
                container: containerName,
                tailLines: 100,
                previous: true
            );
            using var reader = new StreamReader(response);
            logs = await reader.ReadToEndAsync();
        }
        catch
        {
            try
            {
                // Fall back to current logs if no previous container exists
                using var response = await k8sClient.CoreV1.ReadNamespacedPodLogAsync(
                    name: targetPod.Metadata.Name,
                    namespaceParameter: @namespace,
                    container: containerName,
                    tailLines: 100,
                    previous: false
                );
                using var reader = new StreamReader(response);
                logs = await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                logs = $"[System Notice: Logs are currently unavailable for container '{containerName}' in pod '{targetPod.Metadata.Name}' (Status: {targetPod.Status.Phase}).\nReason: {ex.Message}]";
            }
        }

        return Results.Ok(new DiagnosticLogsResponse
        { 
            PodName = targetPod.Metadata.Name, 
            ContainerName = containerName, 
            Status = targetPod.Status.Phase,
            Logs = logs 
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to fetch cluster logs: {ex.Message}");
    }
});

// Start application
await app.RunAsync();

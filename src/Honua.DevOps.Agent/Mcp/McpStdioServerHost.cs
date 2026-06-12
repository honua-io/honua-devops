using Honua.DevOps.Agent.Operations;
using Honua.DevOps.Agent.Operations.Audit;
using Honua.DevOps.Agent.Operations.OperatorPolicy;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OperatorPolicyModel = Honua.DevOps.Agent.Operations.OperatorPolicy.OperatorPolicy;

namespace Honua.DevOps.Agent.Mcp;

/// <summary>
/// Hosts the operator capability surface as a Model Context Protocol stdio server so MCP
/// clients (Claude Code, Codex, etc.) can call the existing tools directly.
///
/// This is a thin adapter: it reuses <see cref="CapabilityToolset"/> verbatim — the same
/// handlers, schemas, execution-mode/tier gates, approval gates, edition gates, and
/// redaction as the interactive agent host. No tool logic is forked. Gate state continues
/// to come from the same HONUA_DEVOPS_* environment variables, so the default posture over
/// MCP remains plan mode with pr-first approval, and gated tools return their
/// approval-required / edition-gated projections instead of executing.
///
/// stdout is reserved for the MCP wire protocol; diagnostics and stdout-targeted audit
/// evidence are routed to stderr. Each tool call still emits exactly one JSONL audit
/// record through the shared <see cref="ToolCallAuditor"/>.
/// </summary>
internal static class McpStdioServerHost
{
    internal const string ServerName = "honua-devops";

    internal static async Task<int> RunAsync(
        OperationRuntime runtime,
        OperatorPolicyModel policy,
        BackendConfiguration backendConfiguration,
        BackendGateway backendGateway,
        SupportGateway supportGateway,
        CancellationToken cancellationToken)
    {
        IAuditSink auditSink = AuditSinkFactory.Create(policy.AuditHookTarget, reserveStdout: true);
        await using (auditSink.ConfigureAwait(false))
        {
            string detectedEdition = await EditionDetector.DetectAsync(backendGateway, cancellationToken);
            IList<AITool> tools = CapabilityToolset.Create(runtime, backendGateway, policy, supportGateway, detectedEdition);

            string sessionId = Guid.NewGuid().ToString("n");
            AuditContext auditContext = new(
                sessionId,
                runtime.ExecutionMode.ToString().ToLowerInvariant(),
                runtime.ExecutionTier.ToConfigValue(),
                policy.ApprovalMode.ToConfigValue(),
                Provider: "mcp",
                auditSink);

            McpServerPrimitiveCollection<McpServerTool> toolCollection = [];
            foreach (AITool tool in tools)
            {
                if (tool is AIFunction function)
                {
                    toolCollection.Add(McpServerTool.Create(new AuditingAIFunction(function, auditContext)));
                }
            }

            McpServerOptions serverOptions = new()
            {
                ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Title = "Honua AI DevOps operator",
                    Version = typeof(McpStdioServerHost).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
                },
                ToolCollection = toolCollection,
                ServerInstructions =
                    "honua-devops operator tools for the connected Honua environment. " +
                    $"Execution mode is `{runtime.ExecutionMode.ToString().ToLowerInvariant()}` " +
                    $"(tier `{runtime.ExecutionTier.ToConfigValue()}`, approval `{policy.ApprovalMode.ToConfigValue()}`). " +
                    "The operator defaults to plan-only behavior: planning tools emit evidence bundles and never " +
                    "apply manifests, submit, or roll back deploy operations. Edition-gated tools " +
                    "(honua_runbook_execute, honua_auto_remediation_plan) return approval-required or edition-gated " +
                    "responses unless the runtime gates allow execution. Call describe_environment first when the " +
                    "request lacks an explicit service, environment, or edition."
            };

            Console.Error.WriteLine(
                $"honua-devops MCP stdio server ready (tools={toolCollection.Count}, " +
                $"mode={runtime.ExecutionMode.ToString().ToLowerInvariant()}, tier={runtime.ExecutionTier.ToConfigValue()}, " +
                $"approval={policy.ApprovalMode.ToConfigValue()}, audit={auditSink.Target}, edition={detectedEdition}, session={sessionId}).");
            Console.Error.WriteLine($"honua-api={backendConfiguration.HonuaApiBaseUri} otel={backendConfiguration.OTelBaseUri}");

            await using StdioServerTransport transport = new(ServerName);
            await using McpServer server = McpServer.Create(transport, serverOptions);
            await server.RunAsync(cancellationToken);
        }

        return 0;
    }

    /// <summary>
    /// Wraps an existing capability-tool <see cref="AIFunction"/> so each MCP tool call emits
    /// the same JSONL audit record the interactive host emits. Invocation is delegated
    /// unchanged to the wrapped function.
    /// </summary>
    private sealed class AuditingAIFunction(AIFunction innerFunction, AuditContext auditContext)
        : DelegatingAIFunction(innerFunction)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            object? result;
            try
            {
                result = await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                await ToolCallAuditor.EmitAsync(
                    auditContext,
                    new ToolCallRecord(Name, arguments),
                    new OperationResponse(
                        Status: "error",
                        Summary: Redaction.Scrub(exception.Message),
                        Findings: [],
                        Actions: [],
                        ValidationChecks: [],
                        Risks: []),
                    cancellationToken);
                throw;
            }

            await ToolCallAuditor.EmitAsync(
                auditContext,
                new ToolCallRecord(Name, arguments),
                result,
                cancellationToken);
            return result;
        }
    }
}

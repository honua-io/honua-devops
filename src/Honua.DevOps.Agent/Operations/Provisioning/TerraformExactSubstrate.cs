namespace Honua.DevOps.Agent.Operations;

/// <summary>
/// Locates the governed Terraform execution substrate honua-iac owns
/// (honua-iac#149/#158) inside a configured honua-iac checkout.
/// </summary>
/// <remarks>
/// <para>
/// honua-devops does not re-implement Terraform invocation. Every init/plan/apply
/// this process performs goes through <c>scripts/terraform-exact-plan.sh</c> and
/// <c>scripts/terraform-exact-apply.sh</c>, which own the backend resolution, the
/// short-lived-identity check, the saved-plan binding, the one-time claim, and the
/// fail-closed refusal matrix documented in
/// <c>docs/devops/terraform-exact-plan-contract.md</c>. Hand-rolled argv would
/// silently bypass all of it.
/// </para>
/// <para>
/// The schemas the wrappers emit against ship in the same checkout (and in the
/// customer distribution tarball), so documents are validated against the
/// substrate's own published contract rather than a vendored copy that could drift.
/// </para>
/// </remarks>
internal sealed record TerraformExactSubstrate(
    string IacRoot,
    string PlanScript,
    string ApplyScript,
    string BackendIdentityScript,
    string ContractsDirectory)
{
    /// <summary>
    /// Set by the wrappers' callers to read STS/state fixtures instead of live AWS.
    /// honua-devops never sets this: an offline run is stamped
    /// <c>identity.evidence_mode = "offline-test"</c> and can never present as release
    /// evidence. It is listed here so the name has one definition.
    /// </summary>
    internal const string OfflineVariable = "HONUA_IAC_OFFLINE";

    /// <summary>
    /// honua-devops always sets this to <c>1</c>. It makes the apply wrapper refuse
    /// with <c>approval-binding-missing</c> when no approval digest is supplied, so a
    /// missing approval fails inside the substrate as well as in this process.
    /// </summary>
    internal const string RequireApprovalVariable = "HONUA_IAC_REQUIRE_APPROVAL";

    internal string OperatorContractSchemaPath
        => Path.Combine(ContractsDirectory, "operator-contract.v1.schema.json");

    internal string ExactPlanSchemaPath
        => Path.Combine(ContractsDirectory, "terraform-exact-plan.v1.schema.json");

    internal string ExecReceiptSchemaPath
        => Path.Combine(ContractsDirectory, "terraform-exec-receipt.v1.schema.json");

    internal string BackendIdentitySchemaPath
        => Path.Combine(ContractsDirectory, "terraform-backend-identity.v1.schema.json");

    /// <summary>
    /// Resolves the substrate from a configured honua-iac checkout, or explains
    /// precisely which part of it is missing. A checkout that predates honua-iac#158
    /// has no wrappers, and provisioning must refuse rather than fall back to
    /// hand-rolled Terraform argv.
    /// </summary>
    internal static bool TryResolve(string configuredIacRoot, out TerraformExactSubstrate? substrate, out string error)
    {
        substrate = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredIacRoot))
        {
            error = "The honua-iac root is not configured.";
            return false;
        }

        string iacRoot = Path.GetFullPath(configuredIacRoot);
        if (!Directory.Exists(iacRoot))
        {
            error = "The configured honua-iac checkout does not exist.";
            return false;
        }

        string scripts = Path.Combine(iacRoot, "scripts");
        string planScript = Path.Combine(scripts, "terraform-exact-plan.sh");
        string applyScript = Path.Combine(scripts, "terraform-exact-apply.sh");
        string backendIdentityScript = Path.Combine(scripts, "terraform-backend-identity.sh");
        string contracts = Path.Combine(iacRoot, "infrastructure", "terraform", "contracts");

        foreach (string required in new[] { planScript, applyScript, backendIdentityScript })
        {
            if (!File.Exists(required))
            {
                error = $"The configured honua-iac checkout does not ship `scripts/{Path.GetFileName(required)}`. "
                    + "The governed exact-plan substrate (honua-iac#158) is required; honua-devops does not "
                    + "fall back to hand-rolled Terraform invocation.";
                return false;
            }
        }

        if (!Directory.Exists(contracts))
        {
            error = "The configured honua-iac checkout does not ship `infrastructure/terraform/contracts`, "
                + "so emitted documents cannot be validated against their published schemas.";
            return false;
        }

        substrate = new TerraformExactSubstrate(iacRoot, planScript, applyScript, backendIdentityScript, contracts);
        return true;
    }

    /// <summary>
    /// Resolves a Terraform root by name and checks it against the substrate's
    /// qualified-root expectations: it must live under the checkout's
    /// <c>infrastructure/terraform/examples</c> directory, be a real root
    /// (<c>main.tf</c> + <c>variables.tf</c>), and carry a committed provider lock.
    /// </summary>
    /// <remarks>
    /// The lock check is deliberately duplicated here even though the wrappers refuse
    /// with <c>provider-lock-missing</c>: naming an unpinnable root is a configuration
    /// error worth reporting before a process starts. Everything else about
    /// qualification — committed source, remote backend, locking primitive, live STS
    /// session — is the wrapper's to decide, and this method must not pre-empt it.
    /// </remarks>
    internal string ResolveQualifiedRoot(string rootName)
    {
        if (string.IsNullOrWhiteSpace(rootName) || rootName.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidOperationException("The Terraform root name is not a bare directory name.");
        }

        string examplesRoot = Path.GetFullPath(Path.Combine(IacRoot, "infrastructure", "terraform", "examples"));
        string root = Path.GetFullPath(Path.Combine(examplesRoot, rootName));
        string expectedPrefix = examplesRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!root.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The Terraform root escapes the honua-iac examples directory.");
        }

        if (!File.Exists(Path.Combine(root, "main.tf")) || !File.Exists(Path.Combine(root, "variables.tf")))
        {
            throw new InvalidOperationException(
                $"The deployable root `{rootName}` is missing from the configured honua-iac checkout.");
        }

        if (!File.Exists(Path.Combine(root, ".terraform.lock.hcl")))
        {
            throw new InvalidOperationException(
                $"The deployable root `{rootName}` carries no committed `.terraform.lock.hcl`, so its provider set "
                + "cannot be pinned. The exact-plan substrate refuses an unpinnable root.");
        }

        return root;
    }
}

/// <summary>
/// The stacks honua-devops is allowed to provision, and the honua-iac root each one
/// resolves to. The root is data here rather than a literal in the provisioning
/// path, so adding aws-serverless or Helm is a catalog entry plus its contract
/// projection — never a new hard-coded path.
/// </summary>
/// <remarks>
/// The root is deliberately NOT caller-supplied. A caller chooses a stack id; the
/// catalog chooses the root. That keeps a caller-controlled string from ever
/// reaching a filesystem path while still removing the hard-coded single root.
/// </remarks>
internal sealed record ProvisioningStack(
    string Id,
    string TerraformRootName,
    IReadOnlySet<string> Sizes,
    bool ProjectsOperatorContract);

internal static class ProvisioningStackCatalog
{
    private static readonly IReadOnlyList<ProvisioningStack> Stacks =
    [
        new ProvisioningStack(
            "aws-ecs",
            "aws",
            new HashSet<string>(StringComparer.Ordinal) { "small" },
            ProjectsOperatorContract: true)
    ];

    internal static IReadOnlyList<string> Ids => [.. Stacks.Select(stack => stack.Id)];

    internal static bool TryResolve(string stackId, out ProvisioningStack? stack)
    {
        stack = Stacks.FirstOrDefault(candidate => string.Equals(candidate.Id, stackId, StringComparison.Ordinal));
        return stack is not null;
    }
}

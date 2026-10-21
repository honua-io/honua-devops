using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Honua.DevOps.Agent.Operations;

/// <summary>Host-owned input witness for the exact-plan wrapper; never an MCP tool result.</summary>
internal static class PrivatePlanHostEvidence
{
    internal static bool IsSupportedHost => OperatingSystem.IsLinux();
    private const string SidecarName = "private-host-evidence.json";
    private const int MaxVariablesBytes = 16 * 1024;
    private const int MaxInputBytes = 8 * 1024 * 1024;
    private const int MaxMetadataBytes = 64 * 1024;
    private const int MaxSidecarBytes = 96 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal sealed record Snapshot(byte[] VariablesBytes, string[] Entries, string InputDigest, string[] InputRefs);
    private sealed record Sidecar(string ProvisioningOperationId, string ActorId, string TerraformRoot,
        string? TargetId, string Action,
        string PlanSha256,
        string PlanMetadataDigest, string MetadataContentSha256, string VariablesSha256,
        string VariablesBase64, string[] ExecutedInputEntries, string InputDigest,
        DateTimeOffset ExpiresAtUtc);

    internal static Snapshot Capture(string root, string variableFile, IReadOnlyDictionary<string, string>? overlay)
    {
        foreach (DictionaryEntry value in Environment.GetEnvironmentVariables())
        {
            if (value.Key is string name && name.StartsWith("TF_CLI_ARGS", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(value.Value as string))
                throw new InvalidDataException("Terraform CLI argument environment is unsupported for private host evidence.");
        }
        if (overlay?.Any(item => item.Key.StartsWith("TF_CLI_ARGS", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(item.Value)) == true)
            throw new InvalidDataException("Terraform CLI argument environment is unsupported for private host evidence.");
        if (File.Exists(Path.Combine(root, "terraform.tfvars.json")))
            throw new InvalidDataException("terraform.tfvars.json is unsupported for private host evidence.");

        var entries = new List<string>();
        string absoluteVariables = Path.GetFullPath(variableFile);
        byte[] variables = BoundedRead(absoluteVariables, MaxVariablesBytes);
        AddFile("varfile:", absoluteVariables, variables);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry value in Environment.GetEnvironmentVariables())
        {
            if (value.Key is string name && name.StartsWith("TF_VAR_", StringComparison.Ordinal))
                environment[name] = value.Value as string ?? throw new InvalidDataException("Terraform variable input is not text.");
        }
        if (overlay is not null)
            foreach (var value in overlay.Where(item => item.Key.StartsWith("TF_VAR_", StringComparison.Ordinal)))
                environment[value.Key] = value.Value;
        foreach (var variable in environment)
        {
            string name = variable.Key[7..];
            if (name.Length is < 1 or > 128 || !AsciiName(name))
                throw new InvalidDataException("Terraform variable name is unsupported for private host evidence.");
            byte[] bytes = StrictUtf8.GetBytes(variable.Value);
            if (bytes.Length > MaxInputBytes)
                throw new InvalidDataException("Terraform variable value is too large for private host evidence.");
            entries.Add($"env:{name}={Sha(bytes)}");
        }
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (name == "terraform.tfvars" || name.EndsWith(".auto.tfvars", StringComparison.Ordinal)
                || name.EndsWith(".auto.tfvars.json", StringComparison.Ordinal))
                AddFile("autofile:", name, BoundedRead(path, MaxInputBytes));
        }
        entries.Sort(StringComparer.Ordinal);
        if (entries.Count is < 1 or > 128 || entries.Distinct(StringComparer.Ordinal).Count() != entries.Count
            || entries.Any(item => item.Length > 512))
            throw new InvalidDataException("Terraform input closure is unsupported for private host evidence.");
        string[] ordered = [.. entries];
        return new Snapshot(variables, ordered, Sha(StrictUtf8.GetBytes(string.Join('\n', ordered))),
            [.. ordered.Select(item => item[..item.IndexOf('=')])]);

        void AddFile(string kind, string reference, byte[] bytes)
        {
            if (reference.Length is < 1 or > 256 || reference.Contains('=') || reference.Contains('\n')
                || reference.Contains('\r') || reference.Contains('\\') ||
                (kind == "varfile:" && (!Path.IsPathFullyQualified(reference) || !reference.StartsWith('/'))))
                throw new InvalidDataException("Terraform input path is unsupported for private host evidence.");
            string segments = kind == "varfile:" ? reference[1..] : reference;
            if (segments.Split('/').Any(segment => segment is "" or "." or ".."
                || segment.Any(character => !char.IsAsciiLetterOrDigit(character)
                    && character is not '.' and not '_' and not '-')))
                throw new InvalidDataException("Terraform input path is unsupported for private host evidence.");
            entries.Add($"{kind}{reference}={Sha(bytes)}");
        }
    }

    internal static bool MatchesMetadata(Snapshot snapshot, string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        var inputs = document.RootElement.GetProperty("inputs");
        return inputs.GetProperty("input_digest").GetString() == snapshot.InputDigest
            && inputs.GetProperty("input_refs").EnumerateArray().Select(item => item.GetString())
                .SequenceEqual(snapshot.InputRefs, StringComparer.Ordinal);
    }

    internal static byte[] ReadMetadata(string path) => BoundedRead(path, MaxMetadataBytes);

    internal static void Save(string directory, string operationId, string actorId, string terraformRoot,
        string planSha,
        ExactPlanMetadata metadata, byte[] metadataBytes, Snapshot snapshot)
    {
        if (metadataBytes.Length is < 1 or > MaxMetadataBytes ||
            !MatchesMetadata(snapshot, StrictUtf8.GetString(metadataBytes)) ||
            !ExactPlanCanonicalDigest.Matches(StrictUtf8.GetString(metadataBytes)) ||
            metadata.ActorId != actorId || metadata.TerraformRoot != terraformRoot)
            throw new InvalidDataException("The exact plan metadata differs from the executed input witness.");
        var sidecar = new Sidecar(operationId, actorId, terraformRoot, metadata.TargetId, metadata.Action,
            planSha, metadata.PlanMetadataDigest, Sha(metadataBytes),
            Sha(snapshot.VariablesBytes), Convert.ToBase64String(snapshot.VariablesBytes), snapshot.Entries,
            snapshot.InputDigest, metadata.ExpiresAtUtc);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(sidecar);
        if (bytes.Length > MaxSidecarBytes) throw new InvalidDataException("Private plan evidence is too large.");
        string path = Path.Combine(directory, SidecarName);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        stream.Write(bytes);
        stream.Flush(true);
    }

    internal static async Task<int> ExportAsync(string operationId, TextWriter output, TextWriter error,
        CancellationToken cancellationToken = default)
    {
        if (!IsSupportedHost)
        {
            await error.WriteLineAsync("private-plan-evidence-refused: unsupported-platform (Linux/WSL required)");
            return 1;
        }
        try
        {
            const string prefix = "urn:honua:provisioning:";
            if (!operationId.StartsWith(prefix, StringComparison.Ordinal) || operationId.Length != prefix.Length + 32
                || operationId[prefix.Length..].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Provisioning operation id is invalid.");
            string token = operationId[prefix.Length..];
            string root = HonuaOperationsToolkit.SavedPlanRootForHostEvidence();
            string directory = Path.Combine(root, token);
            string manifestPath = Path.Combine(directory, "manifest.json");
            string sidecarPath = Path.Combine(directory, SidecarName);
            string metadataPath = Path.Combine(directory, "honua.tfplan.metadata.json");
            string planPath = Path.Combine(directory, "honua.tfplan");
            string variablesPath = Path.Combine(directory, "small.auto.tfvars.json");
            await RequireProtectedAsync(root, true, cancellationToken);
            await RequireProtectedAsync(directory, true, cancellationToken);
            foreach (string path in new[] { manifestPath, sidecarPath, metadataPath, planPath, variablesPath })
                await RequireProtectedAsync(path, false, cancellationToken);
            byte[] sidecarBytes = BoundedRead(sidecarPath, MaxSidecarBytes);
            Sidecar sidecar = JsonSerializer.Deserialize<Sidecar>(sidecarBytes)
                ?? throw new InvalidDataException("Private plan evidence is invalid.");
            byte[] manifestBytes = BoundedRead(manifestPath, MaxMetadataBytes);
            using var manifest = JsonDocument.Parse(manifestBytes);
            byte[] metadataBytes = BoundedRead(metadataPath, MaxMetadataBytes);
            string metadataJson = StrictUtf8.GetString(metadataBytes);
            using var metadata = JsonDocument.Parse(metadataJson);
            if (!ExactPlanCanonicalDigest.Matches(metadataJson))
                throw new InvalidDataException("Protected plan metadata canonical digest changed.");
            byte[] variablesBytes = Convert.FromBase64String(sidecar.VariablesBase64);
            if (sidecar.ExecutedInputEntries is null || sidecar.ExecutedInputEntries.Length is < 1 or > 128 ||
                sidecar.ExecutedInputEntries.Any(entry => entry is null || entry.Length > 512 ||
                    entry.IndexOf('=') < 1 || entry.IndexOf('=') == entry.Length - 1) ||
                !sidecar.ExecutedInputEntries.SequenceEqual(
                    sidecar.ExecutedInputEntries.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw new InvalidDataException("Private plan input entries are invalid.");
            DateTimeOffset expiry = DateTimeOffset.Parse(
                metadata.RootElement.GetProperty("expires_at_utc").GetString()!, CultureInfo.InvariantCulture);
            if (variablesBytes.Length is < 2 or > MaxVariablesBytes ||
                !variablesBytes.AsSpan().SequenceEqual(BoundedRead(variablesPath, MaxVariablesBytes)) ||
                !IsSha(sidecar.PlanSha256) || !IsSha(sidecar.PlanMetadataDigest) ||
                !IsSha(sidecar.InputDigest) ||
                sidecar.ProvisioningOperationId != operationId ||
                metadata.RootElement.GetProperty("actor_id").GetString() != sidecar.ActorId ||
                metadata.RootElement.GetProperty("source").GetProperty("terraform_root").GetString() != sidecar.TerraformRoot ||
                metadata.RootElement.GetProperty("target_id").GetString() != sidecar.TargetId ||
                metadata.RootElement.GetProperty("action").GetString() != sidecar.Action ||
                sidecar.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1) ||
                expiry != sidecar.ExpiresAtUtc ||
                manifest.RootElement.GetProperty("Token").GetString() != token ||
                manifest.RootElement.GetProperty("ProvisioningOperationId").GetString() != operationId ||
                manifest.RootElement.GetProperty("TerraformRoot").GetString() != sidecar.TerraformRoot ||
                manifest.RootElement.GetProperty("DestroyPlan").GetBoolean() != (sidecar.Action == "destroy") ||
                manifest.RootElement.GetProperty("PlanSha256").GetString() != sidecar.PlanSha256 ||
                manifest.RootElement.GetProperty("PlanMetadataFile").GetString() != metadataPath ||
                manifest.RootElement.GetProperty("Metadata").GetProperty("PlanMetadataDigest").GetString() != sidecar.PlanMetadataDigest ||
                manifest.RootElement.GetProperty("Metadata").GetProperty("InputDigest").GetString() != sidecar.InputDigest ||
                metadata.RootElement.GetProperty("plan_metadata_digest").GetString() != sidecar.PlanMetadataDigest ||
                metadata.RootElement.GetProperty("plan").GetProperty("sha256").GetString() != sidecar.PlanSha256 ||
                Sha(metadataBytes) != sidecar.MetadataContentSha256 ||
                Sha(variablesBytes) != sidecar.VariablesSha256 ||
                HashFile(planPath) != sidecar.PlanSha256)
                throw new InvalidDataException("Protected plan evidence no longer matches the saved operation.");
            var snapshot = new Snapshot(variablesBytes, sidecar.ExecutedInputEntries,
                Sha(StrictUtf8.GetBytes(string.Join('\n', sidecar.ExecutedInputEntries))),
                [.. sidecar.ExecutedInputEntries.Select(item => item[..item.IndexOf('=')])]);
            if (snapshot.InputDigest != sidecar.InputDigest || !MatchesMetadata(snapshot, metadataJson))
                throw new InvalidDataException("Protected plan inputs no longer match the exact-plan metadata.");
            string variablesJson = StrictUtf8.GetString(variablesBytes);
            using var variables = JsonDocument.Parse(variablesJson);
            if (variables.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Protected plan variables are invalid.");
            string result = JsonSerializer.Serialize(new
            {
                schemaVersion = "honua.devops.private-plan-host-evidence/v1",
                provisioningOperationId = operationId,
                planSha256 = sidecar.PlanSha256,
                planMetadataDigest = sidecar.PlanMetadataDigest,
                metadataContentSha256 = sidecar.MetadataContentSha256,
                planMetadataJson = metadataJson,
                executedVariablesJson = variablesJson,
                executedInputEntries = sidecar.ExecutedInputEntries
            });
            if (StrictUtf8.GetByteCount(result) > 192 * 1024)
                throw new InvalidDataException("Private plan evidence exceeds the export limit.");
            await output.WriteLineAsync(result);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or JsonException or FormatException or ArgumentException or KeyNotFoundException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await error.WriteLineAsync($"private-plan-evidence-refused: {exception.GetType().Name}");
            return 1;
        }
    }

    private static byte[] BoundedRead(string path, int limit)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length is < 0 || info.Length > limit)
            throw new InvalidDataException("Private plan input is not a bounded regular file.");
        using var stream = OpenRegularRead(path);
        using var result = new MemoryStream(Math.Min((int)info.Length, limit));
        byte[] chunk = new byte[Math.Min(64 * 1024, limit + 1)];
        while (true)
        {
            int count = stream.Read(chunk, 0, Math.Min(chunk.Length, limit + 1 - (int)result.Length));
            if (count == 0) break;
            result.Write(chunk, 0, count);
            if (result.Length > limit)
                throw new InvalidDataException("Private plan input changed while reading.");
        }
        return result.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static bool IsSha(string? value) => value is { Length: 64 } && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool AsciiName(string name) => (char.IsAsciiLetter(name[0]) || name[0] == '_')
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    private static string HashFile(string path)
    {
        using var stream = OpenRegularRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private const int OpenReadOnlyNonBlockingNoFollow = 0x800 | 0x20000 | 0x80000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNoFollow(string path, int flags);

    private static FileStream OpenRegularRead(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            var info = new FileInfo(path);
            if ((info.Attributes & (FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidDataException("Private plan input is not a regular file.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        int descriptor = OpenNoFollow(path, OpenReadOnlyNonBlockingNoFollow);
        if (descriptor < 0)
            throw new InvalidDataException("Private plan input cannot be opened without following a link.");
        var handle = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
        try
        {
            // Stat the already-open descriptor through procfs. O_NONBLOCK prevents a
            // substituted FIFO from hanging before this regular-file check.
            string mode = Stat($"/proc/{Environment.ProcessId}/fd/{descriptor}", "%f", follow: true);
            if (!HasFileType(mode, 0x8000))
                throw new InvalidDataException("Private plan input is not a regular file.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string Stat(string path, string format, bool follow)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/stat", UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true
        } };
        process.StartInfo.Environment["LC_ALL"] = "C";
        if (follow) process.StartInfo.ArgumentList.Add("-L");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(format);
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(path);
        if (!process.Start()) throw new InvalidDataException("Private plan file type could not be checked.");
        string value = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || value.Length > 128)
            throw new InvalidDataException("Private plan file type could not be checked.");
        return value.Trim();
    }

    private static bool HasFileType(string mode, int expected)
        => int.TryParse(mode, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) &&
            (value & 0xF000) == expected;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUserId();

    private static Task RequireProtectedAsync(string path, bool directory, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new InvalidDataException("Private host evidence requires Unix file ownership.");
        FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Private plan evidence path is missing or linked.");
        UnixFileMode mode = File.GetUnixFileMode(path);
        UnixFileMode expected = directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        if (mode != expected) throw new InvalidDataException("Private plan evidence permissions are not protected.");
        cancellationToken.ThrowIfCancellationRequested();
        string ownerAndType = Stat(path, "%u:%f", follow: false);
        string[] parts = ownerAndType.Split(':', 2, StringSplitOptions.None);
        if (parts.Length != 2 || !HasFileType(parts[1], directory ? 0x4000 : 0x8000) ||
            !uint.TryParse(parts[0], NumberStyles.None,
                CultureInfo.InvariantCulture, out uint actual) || actual != EffectiveUserId())
            throw new InvalidDataException("Private plan evidence owner changed.");
        return Task.CompletedTask;
    }
}

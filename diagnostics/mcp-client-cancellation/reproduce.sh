#!/usr/bin/env bash
set -euo pipefail
# Diagnostic only: this runs the original failing assertions, never an xfail.
# Invoke through the maintained build guard; no cloud/Terraform action is used.
repository=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
revision=cd24459a6857e7e96fd99cce8e5f70fdfb6308fe
snapshot_sha=05a73212768a96aa3d6be24dd398bb0f10e01a026d18719e28f421cec7a5e0f5
# Builds stay on the repository's real disk; retain results after exact source cleanup.
result_base="$repository/artifacts/mcp-client-cancellation-228"
mkdir -p -- "$result_base"
result_root=$(mktemp -d "$result_base/run.XXXXXXXX")
chmod 700 "$result_root"
scratch="$result_root/source"
mkdir -m 700 "$scratch"
trap 'rm -rf -- "$scratch"' EXIT
git -C "$repository" archive "$revision" | tar -x -C "$scratch"
file=tests/Honua.DevOps.Agent.Tests/McpProvisionCancellationTests.cs
printf '%s  %s
' "$snapshot_sha" "$scratch/$file" | sha256sum --check --status
cmp "$repository/diagnostics/mcp-client-cancellation/McpProvisionCancellationTests.cs.snapshot" "$scratch/$file"
printf 'UNQUALIFIED diagnostic issue228; original source %s; original assertions retain nonzero failure.\n' "$revision"
cd "$scratch"
set +e
"${DOTNET_EXECUTABLE:-dotnet}" test tests/Honua.DevOps.Agent.Tests/Honua.DevOps.Agent.Tests.csproj --configuration Release --filter 'FullyQualifiedName~CancelledPublicProvisionCall_StopsItsActualPlanProcessTree' --logger 'console;verbosity=normal' --logger 'trx;LogFileName=interop.trx' --results-directory "$result_root/results" > "$result_root/dotnet.log" 2>&1
dotnet_status=$?
set -e
python3 - "$result_root" "$dotnet_status" <<'PYTHON'
import hashlib, json, pathlib, sys, xml.etree.ElementTree as E
root = pathlib.Path(sys.argv[1]); status = int(sys.argv[2]); rows = []; error = None
try:
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    trx = root / 'results/interop.trx'; tree = E.parse(trx).getroot()
    definitions = {x.attrib['id']: x.attrib['name'] for x in tree.findall('.//t:UnitTest', ns)}
    for row in tree.findall('.//t:UnitTestResult', ns):
        name = row.attrib.get('testName', '')
        if 'McpProvisionCancellationTests.CancelledPublicProvisionCall_StopsItsActualPlanProcessTree(' in name:
            rows.append({'testName': name, 'testId': row.attrib['testId'], 'definitionName': definitions.get(row.attrib['testId']), 'outcome': row.attrib['outcome']})
    executed = len(rows) == 2 and all(x['definitionName'] == x['testName'] for x in rows) and {x['testName'].split('traceProtocol: ')[-1] for x in rows} == {'True)', 'False)'} and all(x['outcome'] in ('Passed', 'Failed') for x in rows)
except (OSError, E.ParseError, KeyError, ValueError) as exc:
    executed = False; error = type(exc).__name__
classification = 'UNVERIFIED'
if executed:
    classification = 'REPRODUCED_ORIGINAL_FAILURES' if all(x['outcome'] == 'Failed' for x in rows) else 'ORIGINAL_CASES_EXECUTED_DIFFERENT_OUTCOME'
files = [{'path': str(f.relative_to(root)), 'sha256': hashlib.sha256(f.read_bytes()).hexdigest()} for f in [root / 'dotnet.log', root / 'results/interop.trx'] if f.is_file()]
receipt = {'diagnosticIssue': 228, 'qualification': False, 'sourceRevision': 'cd24459a6857e7e96fd99cce8e5f70fdfb6308fe', 'dotnetExitCode': status, 'classification': classification, 'originalCasesExecuted': executed, 'cases': rows, 'errorType': error, 'files': files}
(root / 'diagnostic-result.json').write_text(json.dumps(receipt, indent=2) + '\n')
print(classification + ': ' + str(root / 'diagnostic-result.json'))
PYTHON
exit "$dotnet_status"

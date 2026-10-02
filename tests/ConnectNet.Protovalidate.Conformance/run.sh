#!/usr/bin/env bash
# Runs the official Protovalidate conformance suite against ConnectNet.Validation.
#
# The runner is tools/protovalidate-conformance from the pinned Protovalidate revision; it is
# fetched with the Go toolchain (go 1.24 or newer). Every one of the suite's cases must pass
# with strict error and message comparison.
set -euo pipefail

REVISION=3807e3d1c38b48295eae269e2f3b97cee668edd3
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIGURATION="${CONFIGURATION:-Release}"

dotnet build "$HERE/ConnectNet.Protovalidate.Conformance.csproj" -c "$CONFIGURATION" -nologo -v q
EXECUTOR="$HERE/bin/$CONFIGURATION/net10.0/ConnectNet.Protovalidate.Conformance.dll"

RUNNER="${PROTOVALIDATE_CONFORMANCE:-}"
if [ -z "$RUNNER" ]; then
  GOBIN="$HERE/bin/tools" go install "github.com/bufbuild/protovalidate/tools/protovalidate-conformance@$REVISION"
  RUNNER="$HERE/bin/tools/protovalidate-conformance"
fi

exec "$RUNNER" --strict_error --strict_message --timeout 300s "$@" dotnet "$EXECUTOR"

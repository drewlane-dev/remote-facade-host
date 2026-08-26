#!/bin/sh
# Compares /invoke responses from the image under test against the PUBLISHED
# v1.0.1 image, byte for byte, for a v1.0-shaped configuration.
#
# This exists because a wire-format change is invisible to ordinary tests: a
# regression that drops a field still returns ok:true and still passes any
# assertion that greps for a value. Only a byte comparison against the previous
# release catches it.
set -eu

IMAGE="${1:-remote-facade-host:dev}"
# Deliberately the OLD image name. v1.0.1 was published as remote-class-host,
# before the rename, and that is the artifact whose wire format this guard pins.
# Rewriting it to the new name would point at a tag that has never existed, and
# the guard would fail to pull rather than compare anything.
BASELINE="ghcr.io/drewlane-dev/remote-class-host:1.0.1"
HERE="$(cd "$(dirname "$0")" && pwd)"
NET="rch-baseline-$$"
PASS=0
FAIL=0

ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; }

cleanup() {
  docker rm -f "base-${NET}" "test-${NET}" >/dev/null 2>&1 || true
  docker network rm "${NET}" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "${NET}" >/dev/null

start() { # start <name> <image>
  # LIB_SERVICES is REQUIRED here: CsLib.Store's constructor takes IStamp, so
  # without a mapping the container fails to construct and never becomes
  # healthy. This mirrors exactly how test/run.sh starts its own `cs` host.
  docker run -d --name "$1" --network "${NET}" --network-alias "$1" \
    -v "${HERE}/publish/cslib:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store \
    -e LIB_OPTIONS='{"RootPath":"/tmp/baseline"}' \
    -e LIB_SERVICES='{"CsLib.IStamp":"CsLib.RealStamp"}' \
    -e DOTNET_EnableDiagnostics=0 \
    "$2" >/dev/null
}

call() { # call <alias> <json> -> raw response body
  docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s -m 10 \
    -X POST "http://$1:8080/invoke" -H 'Content-Type: application/json' -d "$2"
}

wait_healthy() {
  i=0
  while [ "$i" -lt 60 ]; do
    if docker run --rm --network "${NET}" curlimages/curl:8.10.1 \
         -s -m 2 -o /dev/null "http://$1:8080/health" 2>/dev/null; then
      return 0
    fi
    i=$((i + 1)); sleep 1
  done
  return 1
}

echo "== wire-format baseline: ${IMAGE} vs ${BASELINE} =="
docker pull -q "${BASELINE}" >/dev/null 2>&1 || true
start "base-${NET}" "${BASELINE}"
start "test-${NET}" "${IMAGE}"
wait_healthy "base-${NET}" || { echo "baseline image did not become healthy"; exit 1; }
wait_healthy "test-${NET}" || { echo "image under test did not become healthy"; exit 1; }

# Each case is a full /invoke body. Compare the WHOLE response envelope: a
# substring check would pass against a payload that had lost a field.
# Method names verified against test/fixtures/CsLib/Store.cs. Two of these --
# VtValueAsync and PolyReturn -- are the exact shapes that regressed in the last
# release cycle, so they are the ones most worth pinning byte-for-byte.
for body in \
  '{"method":"WriteAsync","args":["a.txt","hello"]}' \
  '{"method":"ReadAsync","args":["a.txt"]}' \
  '{"method":"Count","args":[]}' \
  '{"method":"VtValueAsync","args":[]}' \
  '{"method":"Stamp","args":[]}' \
  '{"method":"RefArg","args":[1]}' \
  '{"method":"Echo","args":[1]}' \
  '{"method":"PolyReturn","args":[]}' \
  '{"method":"DefinitelyMissing","args":[]}' \
  ; do
  a=$(call "base-${NET}" "$body")
  b=$(call "test-${NET}" "$body")
  if [ "$a" = "$b" ]; then
    ok "identical response for $body"
  else
    bad "response DIFFERS for $body"
    echo "      v1.0.1: $a"
    echo "      under test: $b"
  fi
done

echo ""
echo "baseline passed: ${PASS}  failed: ${FAIL}"
[ "$FAIL" -eq 0 ]

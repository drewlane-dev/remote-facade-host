#!/bin/sh
# Compares /invoke responses from the image under test against the PUBLISHED
# v2.1.0 image, byte for byte, for a composition-root configuration.
#
# This exists because a wire-format change is invisible to ordinary tests: a
# regression that drops a field still returns ok:true and still passes any
# assertion that greps for a value. Only a byte comparison against the previous
# release catches it.
set -eu

IMAGE="${1:-remote-facade-host:dev}"
# Re-baselined for v3. The previous baseline was remote-class-host:1.0.1 driven
# through a LIB_TYPE configuration -- which v3 removed, so that comparison can
# no longer be run at all: the old image cannot speak the new configuration and
# the new image cannot speak the old one.
#
# v2.1.0 is the last release that supports BOTH, so it is the only artifact
# against which a composition-root configuration can be compared across the
# break. Everything below is service-routed, which is exactly the surface v3
# keeps, so any drift in it is a real regression rather than the intended
# removal.
BASELINE="ghcr.io/drewlane-dev/remote-facade-host:2.1.0"
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
  # GraphStartup registers the whole spread this compares over: a plain
  # facade, an inherited interface member, a counter, and a Scoped
  # registration whose rejection is itself part of the wire format.
  docker run -d --name "$1" --network "${NET}" --network-alias "$1" \
    -v "${HERE}/publish/cslib:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
    -e LIB_REGISTRAR=CsLib.GraphStartup.Configure \
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
# substring check would pass against a payload that had lost a field. Service
# names and methods verified against test/fixtures/CsLib/Store.cs.
#
# The error shapes matter as much as the successes: three of these pin
# rejections (no such method, unknown service, Scoped), and those messages are
# what a consumer actually reads when something is wrong.
for body in \
  '{"service":"CsLib.IRootFacade","method":"Who","args":[]}' \
  '{"service":"CsLib.IDerivedFacade","method":"FromBase","args":[]}' \
  '{"service":"CsLib.IDerivedFacade","method":"FromDerived","args":[]}' \
  '{"service":"CsLib.IExplicitThing","method":"Go","args":[]}' \
  '{"service":"CsLib.ICounter","method":"Next","args":[]}' \
  '{"service":"CsLib.IScopedThing","method":"Say","args":[]}' \
  '{"service":"CsLib.IRootFacade","method":"DefinitelyMissing","args":[]}' \
  '{"service":"CsLib.NoSuchService","method":"Who","args":[]}' \
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

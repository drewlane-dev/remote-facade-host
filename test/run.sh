#!/bin/sh
# Self-tests for remote-facade-host. Each case runs a real container.
set -eu

IMAGE="${1:-remote-facade-host:dev}"
PASS=0
FAIL=0
HERE="$(cd "$(dirname "$0")" && pwd)"

ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; }

# One long-lived container per plugin; api() talks to it over a private network.
NET="rch-test-$$"
cleanup() {
  for c in $(docker ps -aq --filter "network=${NET}" 2>/dev/null); do
    docker rm -f "$c" >/dev/null 2>&1 || true
  done
  docker network rm "${NET}" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "${NET}" >/dev/null

start_host() { # start_host <alias> <pluginDir> <assembly> <registrar> [storeRoot] [sentinelPath]
  # DOTNET_EnableDiagnostics=0: belt-and-suspenders alongside giving the
  # plugin its own private RootPath (below) -- without it the runtime drops
  # its own diagnostic pipe/socket files straight into /tmp, which would
  # again pollute any OTHER process's directory count if it ever pointed
  # back at /tmp directly.
  docker run -d --rm --name "$1-${NET}" --network "${NET}" --network-alias "$1" \
    -v "$2:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY="$3" -e LIB_REGISTRAR="$4" \
    -e STORE_ROOT="${5:-/tmp}" \
    -e SENTINEL_PATH="${6:-}" \
    -e DOTNET_EnableDiagnostics=0 \
    "$IMAGE" >/dev/null 2>&1
}

api() { # api <alias> <path> [curl args...]
  alias_="$1"; path="$2"; shift 2
  docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s "$@" "http://${alias_}:8080${path}" 2>/dev/null
}

api_service() { # api_service <alias> <serviceFullName> <method> [jsonArgs]
  # Built on the existing `api` helper (test/run.sh:50), whose signature is
  # `api <alias> <path> [curl args...]`, so there is one place that knows how
  # this suite reaches a container.
  args_="${4:-[]}"
  api "$1" /invoke -X POST -H 'Content-Type: application/json' \
    -d "{\"service\":\"$2\",\"method\":\"$3\",\"args\":${args_}}"
}

wait_healthy() {
  for _ in $(seq 1 30); do
    [ "$(api "$1" /health -o /dev/null -w '%{http_code}')" = "200" ] && return 0
    sleep 1
  done
  return 1
}

# wait_smb <alias> — polls until <alias>:445 accepts a TCP connection, using a
# throwaway container the same way api()/wait_healthy do for HTTP. Replaces a
# fixed `sleep N`, which either wastes time or — worse, on a loaded box —
# under-waits and makes every mount attempt that follows fail.
wait_smb() {
  for _ in $(seq 1 30); do
    docker run --rm --network "${NET}" busybox:1.36 nc -z "$1" 445 >/dev/null 2>&1 && return 0
    sleep 1
  done
  return 1
}

# wait_stopped <containerName> — polls until the container is no longer
# running, then echoes its real exit code. Returns 1 (no output) if it is
# still running after the timeout. Needs a container started WITHOUT --rm:
# --rm deletes the container the instant it exits, before this can inspect
# it, which is exactly the gap that let "a bad registrar exits non-zero"
# pass for reasons other than the container actually exiting non-zero.
wait_stopped() {
  for _ in $(seq 1 30); do
    if [ "$(docker inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" = "false" ]; then
      docker inspect -f '{{.State.ExitCode}}' "$1"
      return 0
    fi
    sleep 1
  done
  return 1
}

echo "== loads and constructs a C# class =="
# A private subdirectory, not /tmp itself: /tmp also holds the .NET runtime's
# own diagnostic pipe/socket files, which Store.Count() -- Directory.GetFiles
# over RootPath -- would otherwise count as its own.
start_host cs "${HERE}/publish/cslib" CsLib.dll CsLib.StoreStartup.Configure \
  /tmp/cslib-data
if wait_healthy cs; then
  ok "host constructs the configured type"
else
  docker logs "cs-${NET}" 2>&1 | tail -4
  bad "host constructs the configured type"
fi

echo "== /types is a usable diagnostic =="
api cs /types | grep -q "CsLib.Store" \
  && ok "/types lists the assembly's types" || bad "/types lists the assembly's types"

echo "== a plugin cannot add routes to the host that loaded it =="
# The host serves its API from an MVC controller, and MVC finds controllers by
# CONVENTION -- any public class whose name ends in "Controller". CsLib carries
# exactly such a type (CsLib.HijackController) with no ASP.NET Core reference of
# its own, so this asks the real question: can a loaded plugin get a route
# served? Discovery is scoped to the host's application parts, and a plugin
# loaded by Assembly.LoadFrom is not one.
#
# Vacuity guard: /types must actually list the type, or a 404 below would prove
# only that the fixture failed to publish.
api cs /types | grep -q "CsLib.HijackController" \
  && ok "the hijack fixture really is in the loaded assembly" \
  || bad "the hijack fixture really is in the loaded assembly"

hijack_code=$(api cs /hijack -o /dev/null -w '%{http_code}')
hijack_index=$(api cs /Hijack/Index -o /dev/null -w '%{http_code}')
if [ "$hijack_code" = "404" ] && [ "$hijack_index" = "404" ]; then
  ok "a controller-shaped type in a plugin is not routed"
else
  bad "a controller-shaped type in a plugin is not routed (/hijack=$hijack_code /Hijack/Index=$hijack_index)"
fi

# The host's own five routes are the whole surface. A plugin type that shadowed
# one of them would show up here as a changed body, not as a new route.
api cs /health | grep -q '"registrar":"CsLib.StoreStartup.Configure"' \
  && ok "the host's own routes are unaffected by a plugin's controller-shaped type" \
  || bad "the host's own routes are unaffected by a plugin's controller-shaped type"

echo "== all four return shapes over /invoke =="
CLIENT_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/CsClient/CsClient.csproj -c Release 2>&1 | grep '^RESULT:')

echo "$CLIENT_OUT" | grep -q "RESULT: async-void ok" \
  && ok "Task (async, no value)" || bad "Task (async, no value)"
echo "$CLIENT_OUT" | grep -q "RESULT: async-value hello" \
  && ok "Task<T> (async with value)" || bad "Task<T> (async with value)"
echo "$CLIENT_OUT" | grep -q "RESULT: sync-value 2" \
  && ok "synchronous method with a return value" || bad "synchronous method with a return value"
echo "$CLIENT_OUT" | grep -q "RESULT: sync-void ok" \
  && ok "synchronous void method" || bad "synchronous void method"

echo "== a polymorphic return value keeps its concrete type across /invoke =="
# IStore.PolyReturn() is declared to return PolyBase; the container hands
# back a PolyDerived. System.Text.Json only preserves that distinction (the
# "$type" discriminator) when Invoker serializes through a declared type
# OTHER than the exact runtime type -- serializing through the runtime type
# directly (PolyDerived) is byte-identical to every OTHER value in this
# suite but silently drops it for THIS one, and the client then either
# downgrades to a plain PolyBase (Extra gone) or throws, if the base is
# abstract. Assert the concrete type survived the full round trip, not just
# that the raw response contains the substring "$type".
echo "$CLIENT_OUT" | grep -q "RESULT: poly-type PolyDerived" \
  && ok "a polymorphic return value's concrete type survives the round trip" \
  || bad "a polymorphic return value's concrete type survives the round trip"

echo "== ValueTask and ValueTask<T> are awaited, not handed over as data =="
# Both are STRUCTS, so a host that tests only `result is Task` never awaits
# them and hands the AWAITABLE ITSELF to System.Text.Json: the measured
# response was {"ok":true,"result":{"isCompleted":true,...,"result":"vt-value"}},
# which the client then deserialized into a DEFAULT ValueTask<string> (a struct
# with a public parameterless ctor and get-only properties, so nothing was
# rejected) and awaited as null -- HTTP 200, ok:true, no error at any hop.
# So assert the VALUE, on the wire and at the client. An {ok:true} assertion
# alone passes against that null, which is exactly the trap.
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"VtValueAsync","args":[]}')
# Compared as the WHOLE envelope, not grepped for '"result":"vt-value"'. The
# broken shape CONTAINS that substring as a NESTED field --
# {"ok":true,"result":{"isCompleted":false,...,"result":"vt-value"}} -- so a
# grep passes against the very bug this case exists to catch. (Observed, not
# assumed: the grep version was run against the deleted fix and reported ok.)
# The same body also shows isCompleted:false, i.e. the ValueTask was still
# RUNNING when the host serialized it.
if [ "$body" = '{"ok":true,"result":"vt-value"}' ]; then
  ok "a ValueTask<T>'s VALUE is on the wire, not the awaitable's own fields"
else
  bad "a ValueTask<T>'s VALUE is on the wire, not the awaitable's own fields (body=$body)"
fi

echo "$CLIENT_OUT" | grep -q "RESULT: vt-value vt-value" \
  && ok "ValueTask<T>'s value arrives at the typed client" \
  || bad "ValueTask<T>'s value arrives at the typed client"

# A non-generic ValueTask has no value to check, only an effect. VtVoidAsync
# writes its file only AFTER a delay, so reading the content back is what
# proves the host really awaited it instead of answering early.
echo "$CLIENT_OUT" | grep -q "RESULT: vt-void vt-written" \
  && ok "a plain ValueTask's work completes before the call returns" \
  || bad "a plain ValueTask's work completes before the call returns"

echo "== failures come back as {ok:false}, not a raw 500 =="
# Finding 1: an async method's exception surfaces at `await task`, as the
# ORIGINAL exception type, never wrapped in TargetInvocationException -- a
# catch scoped only around the synchronous call (or around method.Invoke
# alone) lets it escape /invoke as an unhandled 500. FailAsync throws AFTER
# yielding, so its exception can only ever be observed this way.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"FailAsync","args":[]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"FailAsync","args":[]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "deliberate failure"; then
  ok "an async method's exception is reported as {ok:false}, not a 500"
else
  bad "an async method's exception is reported as {ok:false}, not a 500 (code=$code body=$body)"
fi

# Finding 2: the argument count matches, but its JSON shape doesn't bind to
# the parameter type -- that used to throw JsonException BEFORE the try,
# uncaught.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Touch","args":[{"not":"a string"}]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Touch","args":[{"not":"a string"}]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "argument 'name'"; then
  ok "a malformed argument is reported as {ok:false}, not a 500"
else
  bad "a malformed argument is reported as {ok:false}, not a 500 (code=$code body=$body)"
fi

echo "== ref/out and open generic methods are rejected before they run =="
# A ref/out parameter has no JSON shape to bind to -- it's a location to
# write into, not a value read out of the request. Without Invoker's
# explicit check, argument binding hits that refusal first and throws
# System.InvalidOperationException ("The type 'System.Int32&' is invalid
# for serialization or deserialization..."), naming the byref TYPE but not
# this method, the parameter, or which of ref/out/in it is. (Measured
# directly: it is InvalidOperationException, not NotSupportedException,
# despite the message text reading like the latter.)
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"RefArg","args":[1]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"RefArg","args":[1]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "ref parameter 'x'"; then
  ok "a ref parameter is rejected by name, before the method ever runs"
else
  bad "a ref parameter is rejected by name, before the method ever runs (code=$code body=$body)"
fi

# An open generic method has no type argument for reflection to close over.
# Without Invoker's explicit check, argument binding hits the same kind of
# refusal -- System.InvalidOperationException naming the unbound generic
# parameter ('T') -- but not this method or why it's unbound.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Echo","args":[1]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Echo","args":[1]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "open generic method"; then
  ok "an open generic method is rejected by name, before the method ever runs"
else
  bad "an open generic method is rejected by name, before the method ever runs (code=$code body=$body)"
fi

echo "== a return value System.Text.Json can't serialize gets the same envelope =="
# BadReturn() runs to completion and returns a real System.Type value --
# it's serializing THAT value for the response, back in Invoker, that must
# fail. Without Invoker's guarded serialization this escapes as an EMPTY
# HTTP 500 (Results.Ok's own serialization runs after Invoker returns,
# outside every catch), which a client sees as an unattributable
# "the input does not contain any JSON tokens" -- naming neither the method
# nor the type. Assert the envelope directly, not just the status code: a
# 500 alone wouldn't distinguish this from any other unguarded crash.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"BadReturn","args":[]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"BadReturn","args":[]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "return value of 'BadReturn' (Type)"; then
  ok "a non-serializable return value is reported as {ok:false}, not an empty 500"
else
  bad "a non-serializable return value is reported as {ok:false}, not an empty 500 (code=$code body=$body)"
fi

# StreamReturn() runs to completion and returns a real, readable
# MemoryStream -- System.Text.Json's OWN attempt to serialize it is what
# fails, with InvalidOperationException ("Timeouts are not supported on
# this stream."), a message that reads like a network fault and is not
# one. A catch narrowed to NotSupportedException/JsonException (as
# BadReturn's alone would suggest) misses this one entirely; it must be
# attributed exactly like BadReturn is.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"StreamReturn","args":[]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"StreamReturn","args":[]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "return value of 'StreamReturn' (Stream)"; then
  ok "a Stream return value is attributed, not left with a misleading message"
else
  bad "a Stream return value is attributed, not left with a misleading message (code=$code body=$body)"
fi

# TypeArg()'s argument-side twin of BadReturn: System.Type also refuses to
# be DESERIALIZED, not just serialized. The argument-binding catch used to
# be narrowed to JsonException alone, which misses this one (it throws
# InvalidOperationException) exactly the way the return-side catch missed
# StreamReturn above.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"TypeArg","args":["x"]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"TypeArg","args":["x"]}')
if [ "$code" = "200" ] && echo "$body" | grep -q '"ok":false' \
                       && echo "$body" | grep -q "argument 't' (Type)"; then
  ok "a non-deserializable argument is attributed, whatever exception it throws"
else
  bad "a non-deserializable argument is attributed, whatever exception it throws (code=$code body=$body)"
fi

echo "== instance lifetime =="
# LockCount is IN-MEMORY state a genuinely new Store does not carry over --
# unlike a file on disk, or the static type.FullName /health reports (both of
# which would look identical whether Reset() actually ran or was a no-op:
# see task-2-report.md for the non-vacuity check against a stubbed-out
# no-op Reset()).
invoke_result() { # invoke_result <methodJsonBody> -- extracts the numeric "result"
  # `|| true`: under `set -e`, a failing pipe inside THIS assignment's
  # command substitution would otherwise silently kill the whole script
  # instead of leaving before/after empty and letting the `if` below report
  # a normal "bad".
  api cs /invoke -X POST -H 'Content-Type: application/json' -d "$1" \
    | grep -o '"result":[0-9]*' | grep -o '[0-9]*$' || true
}
api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Lock","args":[]}' >/dev/null
before=$(invoke_result '{"service":"CsLib.Store","method":"LockCount","args":[]}')
code=$(api cs /instance -X DELETE -o /dev/null -w '%{http_code}')
after=$(invoke_result '{"service":"CsLib.Store","method":"LockCount","args":[]}')
if [ "$code" = "204" ] && [ "$before" = "1" ] && [ "$after" = "0" ]; then
  ok "DELETE /instance resets the object"
else
  bad "DELETE /instance resets the object (code=$code before=$before after=$after)"
fi
api cs /health | grep -q "CsLib.Store" \
  && ok "host still serves after a reset" || bad "host still serves after a reset"

echo "== the same image serves a VB library, unchanged =="
start_host vb "${HERE}/publish/vblib" VbLib.dll VbLib.VbStartup.Configure /tmp
if wait_healthy vb; then
  ok "host constructs a VB type"
else
  docker logs "vb-${NET}" 2>&1 | tail -4
  bad "host constructs a VB type"
fi

VB_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/VbClient/VbClient.csproj -c Release 2>&1 | grep '^RESULT:')

echo "$VB_OUT" | grep -q "RESULT: vb-sync VB store" \
  && ok "VB synchronous method with a return value" || bad "VB synchronous method with a return value"
echo "$VB_OUT" | grep -q "RESULT: vb-async touched by VB" \
  && ok "VB async method" || bad "VB async method"

echo "== two instances against one share =="
docker run -d --rm --name "samba-${NET}" --network "${NET}" --network-alias samba \
  -e SMB_USER=azure -e SMB_PASS='Passw0rd!' -e SMB_UID=0 \
  ghcr.io/drewlane-dev/azure-files-emulator:1 >/dev/null
wait_smb samba && ok "samba server is ready" || bad "samba server is ready"

for n in ia ib; do
  docker run -d --rm --name "$n-${NET}" --network "${NET}" --network-alias "$n" \
    --cap-add SYS_ADMIN --cap-add DAC_READ_SEARCH --security-opt apparmor=unconfined \
    -v "${HERE}/publish/cslib:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
    -e LIB_REGISTRAR=CsLib.StoreStartup.Configure -e STORE_ROOT=/mnt/share \
    -e SMB_SERVER=samba -e SMB_SHARE=data \
    "$IMAGE" >/dev/null 2>&1
done
wait_healthy ia && wait_healthy ib && ok "both instances mounted the share" \
  || bad "both instances mounted the share"

# `|| true`: this is an unguarded assignment under `set -eu`, so without it, a
# run where B genuinely CANNOT read A's file — the single most informative
# failure this test can produce — would make grep find no "RESULT:" line,
# fail, and abort the whole script right here, skipping both the ok/bad below
# AND the server-side check that would say whether the bytes reached the
# share at all.
TWO_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/TwoInstanceClient/TwoInstanceClient.csproj -c Release 2>&1 | grep '^RESULT:') || true

echo "$TWO_OUT" | grep -q "RESULT: b-read written-by-a" \
  && ok "instance B reads what A wrote, over its own mount" \
  || bad "instance B reads what A wrote, over its own mount"

# Server-side truth. Without this a host whose mount silently failed would write
# to its own container filesystem and the test above could still pass.
docker exec "samba-${NET}" sh -c 'cat /srv/data/shared.txt 2>/dev/null' | grep -q "written-by-a" \
  && ok "the bytes really landed on the share (verified server-side)" \
  || bad "the bytes really landed on the share (verified server-side)"

# I2: the point of the image is several REAL instances driven CONCURRENTLY.
# The client proxy used to block on every call, so Task.WhenAll(a.X(), b.X())
# ran them strictly one after the other (measured: first Task returned at
# t=85ms, second at t=166ms) -- a contention test would pass while overlapping
# nothing. Asserted on server-side truth: each container reports the UTC tick
# window its own call actually ran in, and the two must INTERSECT. A blocking
# proxy cannot begin B's call until A's has returned, so its windows are
# disjoint by construction -- no wall-clock tolerance, no slow-machine flake.
OVERLAP_LINE=$(echo "$TWO_OUT" | grep '^RESULT: overlap' || true)
if echo "$OVERLAP_LINE" | grep -q "RESULT: overlap yes"; then
  ok "two calls to two instances really overlap, rather than serializing"
else
  bad "two calls to two instances really overlap, rather than serializing (got: $OVERLAP_LINE)"
fi

echo "== a misconfigured mount is fatal, not silently skipped =="
# Nothing previously locked down "a configured mount that fails is fatal" —
# a refactor wrapping ShareMounter's mount call in a try/catch would break no
# test. Point SMB_SERVER at a host that cannot resolve (a plain hostname, not
# an IP: on the docker network's embedded DNS this fails fast with NXDOMAIN
# rather than hanging on a TCP connect timeout) and assert the container
# exits non-zero. wait_stopped needs a container started WITHOUT --rm so its
# exit code can still be inspected after it exits.
docker run -d --name "badhost-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_REGISTRAR=CsLib.StoreStartup.Configure \
  --cap-add SYS_ADMIN --cap-add DAC_READ_SEARCH --security-opt apparmor=unconfined \
  -e SMB_SERVER=no-such-smb-host -e SMB_SHARE=data \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "badhost-${NET}")" && [ "$code" -ne 0 ]; then
  ok "a mount pointed at an unreachable server is fatal"
else
  docker logs "badhost-${NET}" 2>&1 | tail -4
  bad "a mount pointed at an unreachable server is fatal"
fi
docker rm -f "badhost-${NET}" >/dev/null 2>&1 || true

# Partial configuration (only one of SMB_SERVER/SMB_SHARE set) used to be
# treated as "unconfigured" and silently skip the mount entirely — reaching
# the exact same "serving from the container's own filesystem" outcome the
# fatal-on-failure rule exists to prevent, just by a different route. No
# --cap-add here: this must fail before ever calling `mount`.
docker run -d --name "badcfg-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_REGISTRAR=CsLib.StoreStartup.Configure \
  -e SMB_SERVER=samba \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "badcfg-${NET}")" && [ "$code" -ne 0 ]; then
  ok "SMB_SERVER without SMB_SHARE is fatal, not silently unconfigured"
else
  docker logs "badcfg-${NET}" 2>&1 | tail -4
  bad "SMB_SERVER without SMB_SHARE is fatal, not silently unconfigured"
fi
docker rm -f "badcfg-${NET}" >/dev/null 2>&1 || true

echo "== service registration =="
start_host stampreal "${HERE}/publish/cslib" CsLib.dll CsLib.StoreStartup.Configure
wait_healthy stampreal \
  && ok "constructs a type with a registered dependency" \
  || bad "constructs a type with a registered dependency"

api stampreal /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Stamp","args":[]}' | grep -q '"result":"real"' \
  && ok "resolves the registered implementation" || bad "resolves the registered implementation"

echo "== a removed configuration variable is refused, not ignored =="
# Nothing covered this before: the guard in Program.cs that rejects LIB_TYPE
# and LIB_OPTIONS had no test at any level, so deleting it would have broken
# nothing. It matters because a config carried forward unchanged would
# otherwise start cleanly and serve a graph that silently did not include what
# the operator asked for -- which is the whole reason the guard exists.
#
# LIB_SERVICES joins them: interface-to-implementation overrides are gone, and
# a startup that composes another startup and calls Replace expresses the same
# thing in ordinary C# (CsLib.FakeStampStartup, exercised below).
for gone in LIB_TYPE LIB_OPTIONS LIB_SERVICES; do
  docker run -d --name "gone-${NET}" --network "${NET}" \
    -v "${HERE}/publish/cslib:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
    -e LIB_REGISTRAR=CsLib.StoreStartup.Configure \
    -e "${gone}={\"CsLib.IStamp\":\"CsLib.FakeStamp\"}" \
    "$IMAGE" >/dev/null 2>&1
  if code="$(wait_stopped "gone-${NET}")" && [ "$code" -ne 0 ] \
     && docker logs "gone-${NET}" 2>&1 | grep -q "${gone}"; then
    ok "${gone} is refused at startup, naming itself"
  else
    docker logs "gone-${NET}" 2>&1 | tail -4
    bad "${gone} is refused at startup, naming itself"
  fi
  docker rm -f "gone-${NET}" >/dev/null 2>&1 || true
done

# "{}" must NOT count as set. It was LIB_OPTIONS's own default in v2 and
# LIB_SERVICES's default in v3, so every harness and compose file in existence
# passes it as inert filler -- refusing it would reject configurations that ask
# for nothing at all. This is the same over-strict guard the codebase already
# got wrong once and documented.
docker run -d --name "inert-${NET}" --network "${NET}" --network-alias inert \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e LIB_REGISTRAR=CsLib.StoreStartup.Configure \
  -e LIB_OPTIONS='{}' -e LIB_SERVICES='{}' -e LIB_TYPE='' \
  "$IMAGE" >/dev/null 2>&1
if wait_healthy inert; then
  ok "an inert '{}' carried forward from an old harness still starts"
else
  docker logs "inert-${NET}" 2>&1 | tail -4
  bad "an inert '{}' carried forward from an old harness still starts"
fi
docker rm -f "inert-${NET}" >/dev/null 2>&1 || true

echo "== LIB_REGISTRAR wires the graph from the app's own code =="
start_host reg "${HERE}/publish/cslib" CsLib.dll CsLib.Registration.AddCsLib
wait_healthy reg \
  && ok "an extension-method registrar supplies its own dependencies" \
  || bad "an extension-method registrar supplies its own dependencies"

api reg /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Stamp","args":[]}' | grep -q '"result":"real"' \
  && ok "registrar's registration is used" || bad "registrar's registration is used"

# The combination that matters: real wiring, one thing faked. This is what
# LIB_SERVICES existed for, and CsLib.FakeStampStartup is the whole of its
# replacement -- a startup that calls the real composition root (an extension
# method) and then Replace. If a substitution ever stopped applying, this case
# would report "real" instead of "fake".
start_host regfake "${HERE}/publish/cslib" CsLib.dll CsLib.FakeStampStartup.Configure
wait_healthy regfake >/dev/null 2>&1
api regfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.Store","method":"Stamp","args":[]}' | grep -q '"result":"fake"' \
  && ok "a startup can compose real wiring and replace one dependency" \
  || bad "a startup can compose real wiring and replace one dependency"

echo "== an unregistered dependency is named when the call needs it =="
# v2 caught this at startup: LIB_TYPE made the host construct the root before
# serving, so a missing dependency killed the container. v3 resolves per call,
# so the container starts fine and the mistake surfaces at the first call that
# needs the missing type. Later, but with the same name in the message -- and
# the host no longer has to guess what will be asked for in order to check it.
start_host stampmissing "${HERE}/publish/cslib" CsLib.dll CsLib.IncompleteStartup.Configure
wait_healthy stampmissing \
  && ok "a host with incomplete wiring still starts -- resolution is per call" \
  || bad "a host with incomplete wiring still starts -- resolution is per call"

MISSING_OUT=$(api_service stampmissing CsLib.Store Stamp)
echo "$MISSING_OUT" | grep -q '"ok":false' \
  && ok "the call fails rather than returning a wrong answer" \
  || bad "the call fails rather than returning a wrong answer (got: $MISSING_OUT)"
echo "$MISSING_OUT" | grep -q "IStamp" \
  && ok "the failure names the unsatisfiable dependency" \
  || bad "the failure names the unsatisfiable dependency (got: $MISSING_OUT)"

echo "== the startup is the only way to say what to serve =="
docker run -d --name "croot-${NET}" --network "${NET}" --network-alias croot \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e LIB_REGISTRAR=CsLib.GraphStartup.Configure \
  -e DOTNET_EnableDiagnostics=0 \
  "${IMAGE}" >/dev/null
wait_healthy croot && ok "the host starts from a registrar alone" \
                  || bad "the host starts from a registrar alone"

docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s -m 10 \
  http://croot:8080/services | grep -q "CsLib.IRootFacade" \
  && ok "GET /services lists a registered service" \
  || bad "GET /services lists a registered service"

echo "== a missing LIB_REGISTRAR is fatal =="
docker run -d --name "noconfig-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e DOTNET_EnableDiagnostics=0 \
  "${IMAGE}" >/dev/null
if wait_stopped "noconfig-${NET}" \
   && [ "$(docker inspect -f '{{.State.ExitCode}}' "noconfig-${NET}")" != "0" ]; then
  ok "a host with no LIB_REGISTRAR exits non-zero"
else
  bad "a host with no LIB_REGISTRAR exits non-zero"
fi
docker logs "noconfig-${NET}" 2>&1 | grep -q "LIB_REGISTRAR" \
  && docker logs "noconfig-${NET}" 2>&1 | grep -q "LIB_REGISTRAR" \
  && ok "the failure names both variables" \
  || bad "the failure names both variables"
docker rm -f "noconfig-${NET}" >/dev/null 2>&1 || true

out=$(api croot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Who","args":[]}')
echo "$out" | grep -qi "must name the service" \
  && ok "an invoke with no service says so plainly" \
  || bad "an invoke with no service says so plainly"

echo "== a call names the service it wants =="
[ "$(api_service croot CsLib.IRootFacade Who)" = '{"ok":true,"result":"root-facade"}' ] \
  && ok "an invoke naming a service reaches it" \
  || bad "an invoke naming a service reaches it"

echo "== a service resolved via an EXPLICIT interface implementation is reachable =="
# ExplicitThing.Go() is an explicit interface implementation -- it compiles to
# a private, specially-named method the concrete type's own GetMethods() does
# not surface. If /invoke dispatched against target.GetType() (the concrete
# type), this would come back "no method 'Go' ..." instead of the value.
[ "$(api_service croot CsLib.IExplicitThing Go)" = '{"ok":true,"result":"explicit"}' ] \
  && ok "an explicitly-implemented interface method is still reachable by name" \
  || bad "an explicitly-implemented interface method is still reachable by name"

echo "== a service-routed call reaches members the interface INHERITS (review final, C1) =="
# /invoke's "service" field dispatches against the type Resolve found, which
# is the interface -- and a member inherited from a BASE interface is part of
# the served contract just as a directly-declared one is. Type.GetMethods() on
# an interface returns only what that interface itself declares, so this was
# once unreachable. FromDerived is the paired positive control.
[ "$(api_service croot CsLib.IDerivedFacade FromDerived)" = '{"ok":true,"result":"derived-method"}' ] \
  && ok "a DECLARED member is reachable through the service field" \
  || bad "a DECLARED member is reachable through the service field"
[ "$(api_service croot CsLib.IDerivedFacade FromBase)" = '{"ok":true,"result":"base-method"}' ] \
  && ok "an INHERITED member is reachable through the service field" \
  || bad "an INHERITED member is reachable through the service field"

echo "== a service CONSTRUCTOR that throws stays inside the {ok,...} envelope =="
# DI does not wrap a constructor's exception, so an ArgumentException from
# one propagated unwrapped out of provider.GetService, past a catch that
# named only InvalidOperationException, past UseAsync, and reached Kestrel:
# measured as HTTP 500 with a ZERO-byte body, the plugin's real message
# reaching the container log and nowhere else. The status is asserted
# alongside the body, because an empty 500 and a well-formed error body are
# indistinguishable to a grep that only looks at stdout.
ctor_code=$(api croot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"service":"CsLib.IThrowingCtor","method":"Boom","args":[]}' -o /dev/null -w '%{http_code}')
[ "$ctor_code" = "200" ] \
  && ok "a throwing service constructor answers 200, not a bare 500" \
  || bad "a throwing service constructor answers 200, not a bare 500 (code=$ctor_code)"
ctor_body=$(api_service croot CsLib.IThrowingCtor Boom)
echo "$ctor_body" | grep -q '"ok":false' \
  && echo "$ctor_body" | grep -q "CsLib.IThrowingCtor" \
  && echo "$ctor_body" | grep -q "no connection string" \
  && ok "the constructor's own message reaches the caller, attributed to the service" \
  || bad "the constructor's own message reaches the caller, attributed to the service (got: $ctor_body)"

echo "== an unknown service names what IS registered =="
out=$(api_service croot CsLib.INotRegistered Who)
echo "$out" | grep -q "CsLib.INotRegistered" \
  && echo "$out" | grep -q "CsLib.IRootFacade" \
  && ok "an unknown service error lists the registered ones" \
  || bad "an unknown service error lists the registered ones"

echo "== a REAL type that nothing registers is reported as unregistered, not just unknown =="
# CsLib.INotRegistered above isn't a type in the assembly at all, so it only
# exercises Resolve's "not a type in the plugin assembly" branch. IStamp IS a
# real CsLib interface -- it just isn't registered anywhere GraphStartup
# reaches -- so this is the only case that exercises the sibling "type found,
# but provider.GetService returned null" branch.
out=$(api_service croot CsLib.IStamp Value)
echo "$out" | grep -q "CsLib.IStamp" \
  && echo "$out" | grep -q "not registered" \
  && echo "$out" | grep -q "CsLib.IRootFacade" \
  && ok "a real but unregistered service names itself and lists what IS registered" \
  || bad "a real but unregistered service names itself and lists what IS registered"

echo "== a scoped service is rejected, not silently rooted =="
out=$(api_service croot CsLib.IScopedThing Say)
echo "$out" | grep -q "IScopedThing" && echo "$out" | grep -qi "scope" \
  && ok "a scoped service is rejected by name" \
  || bad "a scoped service is rejected by name"

echo "== a service registered BOTH Scoped and Singleton resolves as the WINNING registration =="
# IScopedThenSingleton is registered AddScoped first, then AddSingleton,
# without Replace -- DI resolves the LAST one, so the container actually
# serves the singleton. Scoped-detection must follow that same last-wins
# rule, not reject this service just because SOME descriptor for it is
# Scoped.
[ "$(api_service croot CsLib.IScopedThenSingleton Say)" = '{"ok":true,"result":"singleton-wins"}' ] \
  && ok "scoped-detection follows the descriptor that actually resolves, not any matching one" \
  || bad "scoped-detection follows the descriptor that actually resolves, not any matching one"

echo "== DELETE /instance rebuilds the whole graph =="
# ICounter is a SINGLETON inside the provider GraphStartup builds, so its count
# survives every call within one graph and CANNOT survive a rebuild. That is
# the point: reset used to replace only the root instance and leave the
# provider -- and every singleton in it -- standing, which a root-only check
# (a new Store, a fresh /health) cannot tell apart from a real rebuild. Against
# a root-only reset the third call answers 3, not 1.
api_service croot CsLib.ICounter Next >/dev/null
second=$(api_service croot CsLib.ICounter Next)
[ "$second" = '{"ok":true,"result":2}' ] \
  && ok "a singleton counts up within one graph" \
  || bad "a singleton counts up within one graph (got: $second)"

api croot /instance -X DELETE >/dev/null

after=$(api_service croot CsLib.ICounter Next)
[ "$after" = '{"ok":true,"result":1}' ] \
  && ok "reset rebuilt the provider, so the singleton is new" \
  || bad "reset rebuilt the provider, so the singleton is new (got: $after)"

echo "== a reset lands safely against calls already in flight =="
# Rebuilding DISPOSES the provider a call may still be running inside, so the
# hard part is not the rebuild, it is the overlap.
#
# The host is deliberately NOT croot. The disposal sentinel has to be something
# the PROVIDER DOES NOT OWN, or "disposed exactly once" cannot fail: a
# provider-owned singleton is disposed by ServiceProvider.Dispose(), which is
# idempotent (measured: three calls, one disposal), so a double
# HostedGraph.Dispose() reaches it exactly once either way. CsLib.DisposableRoot
# resolved by name from the graph the startup built, so
# HostedGraph disposes it EXPLICITLY -- and a second disposal appends a second
# line. LIB_REGISTRAR still runs, so ICounter is here too.
#
# Nothing below waits on a sleep and hopes: ICounter.HoldThenNextAsync parks
# INSIDE the service until /tmp/go appears, and ICounter.InFlight reports from
# the host's own memory how many calls are parked there right now -- the reset
# is fired only once the host itself says two are. A test that times two curl
# containers' start-up and assumes overlap is asserting against a guess, and the
# defect here is a race.
#
# The park loop awaits, repeatedly and for real. That is the condition that
# breaks a thread-affine gate: a lock entered before a call's first await is
# exited by whichever pool thread resumes the continuation. Measured against the
# ReaderWriterLockSlim version, this case produced SynchronizationLockException
# and then wedged every later call -- see task-5-report.md -- which is why
# InstanceHolder holds no lock across the await.
start_host inflight "${HERE}/publish/cslib" CsLib.dll CsLib.DisposableRootStartup.Configure \
  /tmp /tmp/disposed-inflight
wait_healthy inflight && ok "the in-flight host constructs" \
                      || bad "the in-flight host constructs"

# Resolve it once so the singleton actually EXISTS. Without this the graph is
# disposed with nothing in it and the sentinel stays empty -- the test would
# then report "disposed 0 times" for a host that behaved perfectly.
api_service inflight CsLib.DisposableRoot Ping >/dev/null

count_disposed() { # RAW Dispose() entries -- one line appended per call
  docker exec "inflight-${NET}" sh -c \
    'wc -l < /tmp/disposed-inflight 2>/dev/null || echo 0' 2>/dev/null | tr -d ' '
}

hold_a="$(mktemp)"; hold_b="$(mktemp)"
api_service inflight CsLib.ICounter HoldThenNextAsync '["/tmp/go"]' >"$hold_a" 2>&1 &
pid_a=$!
api_service inflight CsLib.ICounter HoldThenNextAsync '["/tmp/go"]' >"$hold_b" 2>&1 &
pid_b=$!

inflight_n=""
for _ in $(seq 1 40); do
  inflight_n=$(api inflight /invoke -X POST -H 'Content-Type: application/json' \
    -d '{"service":"CsLib.ICounter","method":"InFlight","args":[]}' \
    | grep -o '"result":[0-9]*' | grep -o '[0-9]*$' || true)
  [ "$inflight_n" = "2" ] && break
  sleep 1
done
[ "$inflight_n" = "2" ] \
  && ok "two calls are provably inside the graph at the same time" \
  || bad "two calls are provably inside the graph at the same time (in flight: ${inflight_n:-none})"

# Fired with both calls still parked. -m 20 so a reset that waits on them shows
# up as a failed assertion rather than a suite that hangs.
reset_code=$(api inflight /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$reset_code" = "204" ] \
  && ok "DELETE /instance answers while two calls are still in flight" \
  || bad "DELETE /instance answers while two calls are still in flight (code=${reset_code:-none})"

# The replacement is already serving -- a brand-new singleton counting from 1 --
# while the retired graph is still busy with the two parked calls.
fresh=$(api_service inflight CsLib.ICounter Next)
[ "$fresh" = '{"ok":true,"result":1}' ] \
  && ok "a call arriving after the reset reaches the NEW graph" \
  || bad "a call arriving after the reset reaches the NEW graph (got: $fresh)"

# Read BEFORE the parked calls are released. Compared as a DELTA, which is what
# makes the assertion below non-vacuous in BOTH directions: a reset that
# disposed eagerly would already have counted that line here, so the delta would
# be 0; a reset that disposed twice makes it 2. Both fail.
disposed_before=$(count_disposed)
disposed_before=${disposed_before:-0}
[ "$disposed_before" = "0" ] \
  && ok "the retired graph is NOT disposed while its calls are still inside it" \
  || bad "the retired graph is NOT disposed while its calls are still inside it (lines: $disposed_before)"

docker exec "inflight-${NET}" touch /tmp/go
wait "$pid_a" || true
wait "$pid_b" || true
a=$(cat "$hold_a"); b=$(cat "$hold_b"); rm -f "$hold_a" "$hold_b"

# WHOLE envelopes, sorted -- not grepped. Both calls were served by the same
# retired graph, whose counter was at 0 when they parked, so between them they
# must produce exactly 1 and 2. A call that was dropped, that faulted with an
# ObjectDisposedException from the provider going away underneath it, or that
# somehow reached the NEW graph could not produce that pair.
pair=$(printf '%s\n%s\n' "$a" "$b" | sort | tr '\n' '|')
[ "$pair" = '{"ok":true,"result":1}|{"ok":true,"result":2}|' ] \
  && ok "both in-flight calls completed against the graph they started on" \
  || bad "both in-flight calls completed against the graph they started on (got: $pair)"

# EXACTLY once, and this can now say so: DisposableRoot.Dispose() appends per
# entry and nothing deduplicates it, so a second HostedGraph.Dispose() shows up
# as a second line. Verified by forcing one -- see task-5-report.md.
disposed_after=""
for _ in $(seq 1 30); do
  disposed_after=$(count_disposed)
  [ "$disposed_after" = "1" ] && break
  sleep 1
done
[ "$disposed_after" = "1" ] \
  && ok "the retired graph is disposed exactly once, once its last call finished" \
  || bad "the retired graph is disposed exactly once, once its last call finished (lines: ${disposed_after:-none})"

docker exec "inflight-${NET}" rm -f /tmp/go >/dev/null 2>&1 || true

echo "== a plugin Dispose() that throws must not destroy an innocent caller's result =="
# On the deferred path the retired graph is disposed by whichever in-flight call
# finishes LAST, inside that call's finally. Its own work has already succeeded
# by then, so a propagating throw unwinds past the completed result and returns
# an empty HTTP 500 -- to a caller that did nothing but happen to be last, which
# depends on scheduling, so the same plugin defect would destroy a different
# arbitrary request every run.
start_host disposethrow "${HERE}/publish/cslib" CsLib.dll CsLib.ThrowingDisposableRootStartup.Configure \
  /tmp /tmp/throwing-disposed
wait_healthy disposethrow && ok "a root with a throwing Dispose() constructs" \
                          || bad "a root with a throwing Dispose() constructs"

api_service disposethrow CsLib.ThrowingDisposableRoot Ping >/dev/null

hold_c="$(mktemp)"; hold_d="$(mktemp)"
api_service disposethrow CsLib.ICounter HoldThenNextAsync '["/tmp/go"]' >"$hold_c" 2>&1 &
pid_c=$!
api_service disposethrow CsLib.ICounter HoldThenNextAsync '["/tmp/go"]' >"$hold_d" 2>&1 &
pid_d=$!

throw_n=""
for _ in $(seq 1 40); do
  throw_n=$(api disposethrow /invoke -X POST -H 'Content-Type: application/json' \
    -d '{"service":"CsLib.ICounter","method":"InFlight","args":[]}' \
    | grep -o '"result":[0-9]*' | grep -o '[0-9]*$' || true)
  [ "$throw_n" = "2" ] && break
  sleep 1
done
[ "$throw_n" = "2" ] \
  && ok "two calls are in flight against the throwing-Dispose host" \
  || bad "two calls are in flight against the throwing-Dispose host (in flight: ${throw_n:-none})"

# 204, not 500: nothing is disposed yet, so nothing has thrown yet.
throw_reset=$(api disposethrow /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$throw_reset" = "204" ] \
  && ok "the deferring reset answers 204 before any Dispose() has run" \
  || bad "the deferring reset answers 204 before any Dispose() has run (code=${throw_reset:-none})"

docker exec "disposethrow-${NET}" touch /tmp/go
wait "$pid_c" || true
wait "$pid_d" || true
c=$(cat "$hold_c"); d=$(cat "$hold_d"); rm -f "$hold_c" "$hold_d"

# The point of the case: whole, correct envelopes from BOTH callers -- including
# whichever one ran the throwing Dispose() in its finally. Without the guard the
# last one out returns an empty body and this pair cannot match.
throw_pair=$(printf '%s\n%s\n' "$c" "$d" | sort | tr '\n' '|')
[ "$throw_pair" = '{"ok":true,"result":1}|{"ok":true,"result":2}|' ] \
  && ok "a throwing Dispose() does not corrupt the in-flight caller's result" \
  || bad "a throwing Dispose() does not corrupt the in-flight caller's result (got: $throw_pair)"

# Non-vacuity for the case itself: Dispose() really did run and really did
# throw. A holder that silently skipped disposal would pass the assertion above
# while doing something entirely different.
throw_sentinel=$(docker exec "disposethrow-${NET}" sh -c \
  'wc -l < /tmp/throwing-disposed 2>/dev/null || echo 0' 2>/dev/null | tr -d ' ')
[ "$throw_sentinel" = "1" ] \
  && ok "the throwing Dispose() really ran, exactly once" \
  || bad "the throwing Dispose() really ran, exactly once (lines: ${throw_sentinel:-none})"

# Swallowed is not the same as hidden: it has to reach the container log.
docker logs "disposethrow-${NET}" 2>&1 | grep -q "\[InstanceHolder\]" \
  && ok "the swallowed Dispose() failure is reported out-of-band" \
  || bad "the swallowed Dispose() failure is reported out-of-band"

wait_healthy disposethrow && ok "the host still serves after a throwing Dispose()" \
                          || bad "the host still serves after a throwing Dispose()"

# The OTHER half of the asymmetry, and it must stay: with nothing in flight,
# Reset() disposes on the DELETE thread and does NOT swallow. The operator who
# asked for the reset is the right person to be told their plugin is broken --
# unlike the bystander above, they are actually acting on this graph.
docker exec "disposethrow-${NET}" rm -f /tmp/go >/dev/null 2>&1 || true

# Resolve it on the REBUILT graph. The reset above replaced the provider, and
# the replacement has never constructed its singleton -- a provider disposes
# only what it created, so without this the second reset would dispose an empty
# graph, return 204, and the test would read that as "the throw was swallowed"
# when nothing had thrown at all.
api_service disposethrow CsLib.ThrowingDisposableRoot Ping >/dev/null

idle_code=$(api disposethrow /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$idle_code" = "500" ] \
  && ok "with nothing in flight, a throwing Dispose() is reported to DELETE /instance" \
  || bad "with nothing in flight, a throwing Dispose() is reported to DELETE /instance (code=${idle_code:-none})"

echo "== an IAsyncDisposable-only singleton does not break DELETE /instance (review final, I2) =="
# ServiceProvider.Dispose() (synchronous) THROWS for a tracked singleton that
# implements only IAsyncDisposable, so once one had been resolved every
# DELETE /instance returned 500 and disposal aborted part-way through the
# tracked list. Impossible in v1.0, which never disposed the provider -- and
# invisible to test/baseline.sh, which never issues a DELETE.
#
# Resolution ORDER is deliberate: OwnedResource first, AsyncOnlyResource
# second. The container disposes in REVERSE creation order, so the async-only
# one is reached FIRST and, under synchronous disposal, throws before
# OwnedResource is ever reached -- which is why both sentinels being present
# is evidence disposal ran to completion rather than merely started.
start_host asyncdisp "${HERE}/publish/cslib" CsLib.dll CsLib.AsyncOnlyStartup.Configure
wait_healthy asyncdisp && ok "a host with an IAsyncDisposable-only singleton starts" \
                      || bad "a host with an IAsyncDisposable-only singleton starts"

api_service asyncdisp CsLib.OwnedResource Ping >/dev/null
[ "$(api_service asyncdisp CsLib.AsyncOnlyResource Ping)" = '{"ok":true,"result":"async-only"}' ] \
  && ok "the IAsyncDisposable-only singleton is resolvable and served a call" \
  || bad "the IAsyncDisposable-only singleton is resolvable and served a call"

async_code=$(api asyncdisp /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$async_code" = "204" ] \
  && ok "DELETE /instance succeeds after an IAsyncDisposable-only singleton was resolved" \
  || bad "DELETE /instance succeeds after an IAsyncDisposable-only singleton was resolved (code=${async_code:-none})"

async_sentinel=$(docker exec "asyncdisp-${NET}" sh -c \
  'cat /tmp/async-disposed 2>/dev/null' 2>/dev/null || true)
[ "$async_sentinel" = "disposed" ] \
  && ok "the IAsyncDisposable-only singleton was actually disposed" \
  || bad "the IAsyncDisposable-only singleton was actually disposed (got: ${async_sentinel:-absent})"

# The one disposed AFTER it in the list. Absent here is the signature of
# disposal aborting part-way, which the assertion above alone cannot see.
async_owned=$(docker exec "asyncdisp-${NET}" sh -c \
  'cat /tmp/owned-disposed 2>/dev/null' 2>/dev/null || true)
[ "$async_owned" = "disposed" ] \
  && ok "disposal ran to completion, not just up to the async-only service" \
  || bad "disposal ran to completion, not just up to the async-only service (got: ${async_owned:-absent})"

echo "== the typed client drives a composition root =="
# Reuses the still-running croot host from "composition-root mode" above,
# rather than starting a fresh one: RemoteHost.GetAsync/ResetAsync are
# exercised against the exact same container GraphStartup wired earlier in
# this suite.
GC_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/GraphClient/GraphClient.csproj -c Release 2>&1 \
  | grep '^RESULT:') || GC_OUT=""

# Review finding 1 (Critical): RemoteHost's constructor must be PRIVATE --
# At(string) the only supported way in. Pinned by reflection (see the
# fixture): a public constructor is indistinguishable from a private one to
# every OTHER assertion in this block, since both work identically from a
# caller who only ever uses At().
echo "$GC_OUT" | grep -q "RESULT: ctor-private yes" \
  && ok "RemoteHost's constructor is private -- At(string) is the only way in" \
  || bad "RemoteHost's constructor is private -- At(string) is the only way in"

# Review round 2 finding: RemoteHostEnvironment.For<TStartup>() (a generic
# type parameter) could never be called with a real startup, since a startup
# is a "static class" holder for a static Configure method and C# forbids a
# static type as a generic type argument (CS0718). The signature changed to
# For(Type, string?); this pins the WORKING call against CsLib.GraphStartup,
# a real "public static class" startup -- the exact shape every other
# composition-root example in this suite already uses -- so the previously
# uncallable shape cannot come back.
echo "$GC_OUT" | grep -q "RESULT: env-assembly CsLib.dll" \
  && ok "RemoteHostEnvironment.For(typeof(...)) derives LIB_ASSEMBLY from a real static startup" \
  || bad "RemoteHostEnvironment.For(typeof(...)) derives LIB_ASSEMBLY from a real static startup"
echo "$GC_OUT" | grep -q "RESULT: env-registrar CsLib.GraphStartup.Configure" \
  && ok "RemoteHostEnvironment.For(typeof(...)) derives LIB_REGISTRAR from a real static startup" \
  || bad "RemoteHostEnvironment.For(typeof(...)) derives LIB_REGISTRAR from a real static startup"

echo "$GC_OUT" | grep -q "RESULT: who root-facade" \
  && ok "GetAsync returned a working proxy" \
  || bad "GetAsync returned a working proxy"
echo "$GC_OUT" | grep -q "RESULT: after-reset 1" \
  && ok "ResetAsync rebuilt the graph and the proxy still works" \
  || bad "ResetAsync rebuilt the graph and the proxy still works"

# IScopedThing IS a registered service (GraphStartup adds it AddScoped), so
# GetAsync's registration check -- which only inspects GET /services -- must
# succeed. The rejection lives in HostedGraph.Resolve, consulted by /invoke
# per call, so it can only surface once a method is actually called on the
# proxy. Confirmed by reading HostedGraph.cs/Activation.cs, not assumed: the
# "an unregistered dependency" and "a scoped service is rejected" cases
# earlier in this suite already probe the same server-side logic directly
# via api_service.
echo "$GC_OUT" | grep -q "RESULT: scoped-get ok" \
  && ok "GetAsync<IScopedThing> succeeds -- a Scoped registration IS a registered service" \
  || bad "GetAsync<IScopedThing> succeeds -- a Scoped registration IS a registered service"

SCOPED_CALL_LINE=$(echo "$GC_OUT" | grep '^RESULT: scoped-call' || true)
if echo "$SCOPED_CALL_LINE" | grep -q "IScopedThing" \
    && echo "$SCOPED_CALL_LINE" | grep -qi "scope"; then
  ok "the scoped rejection happens at the first CALL, naming the service and the reason"
else
  bad "the scoped rejection happens at the first CALL, naming the service and the reason (got: $SCOPED_CALL_LINE)"
fi

echo "== native assets =="

# A plugin whose native half lives under runtimes/{rid}/native. Before this
# worked, a consumer had to compute the host image's RID themselves and pass
# LD_LIBRARY_PATH into the container; nothing below sets it, so if these pass
# the consumer genuinely needs no configuration.
start_host nat "${HERE}/publish/nativelib" NativeLib.dll NativeLib.NativeStartup.Configure
if wait_healthy nat; then
  NAT_OUT=$(api nat /invoke -X POST -H 'Content-Type: application/json' -d '{"service":"NativeLib.IGitProbe","method":"InitAndCommit","args":["/tmp/natrepo"]}')
  # A 40-char SHA, not merely ok:true. Producing one requires libgit2 to hash
  # and write real objects, so it cannot be faked by a managed code path.
  if echo "$NAT_OUT" | grep -qE '"result":"[0-9a-f]{40}"'; then
    ok "a plugin's native assets load with NO consumer configuration"
  else
    bad "a plugin's native assets load with NO consumer configuration (got: $NAT_OUT)"
  fi

  # The startup line is expected; a miss line is not. This is the guard for a
  # regression that made every container log two msquic misses per boot,
  # because the shared framework probes for optional native libraries and
  # those declines are normal.
  if docker logs "nat-${NET}" 2>&1 | grep -q "could not resolve"; then
    bad "a successful native load logs no resolver misses"
  else
    ok "a successful native load logs no resolver misses"
  fi
else
  bad "a plugin's native assets load with NO consumer configuration (never healthy)"
  bad "a successful native load logs no resolver misses (never healthy)"
fi

# Vacuity control. Without it, the case above could be passing because the
# library was found somewhere unrelated. Same plugin, native assets deleted:
# the call MUST fail, which is what makes the pass above mean something.
STRIP="$(mktemp -d)/nativelib"
cp -r "${HERE}/publish/nativelib" "$STRIP"
rm -rf "$STRIP/runtimes"
docker run -d --rm --name "natstrip-${NET}" --network "${NET}" --network-alias natstrip \
  -v "$STRIP:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=NativeLib.dll \
  -e LIB_REGISTRAR=NativeLib.NativeStartup.Configure \
  -e DOTNET_EnableDiagnostics=0 "$IMAGE" >/dev/null 2>&1
if wait_healthy natstrip; then
  STRIP_OUT=$(api natstrip /invoke -X POST -H 'Content-Type: application/json' -d '{"service":"NativeLib.IGitProbe","method":"InitAndCommit","args":["/tmp/natrepo"]}')
  echo "$STRIP_OUT" | grep -q '"ok":false' \
    && ok "with native assets removed the SAME call fails -- the case above is not vacuous" \
    || bad "with native assets removed the SAME call fails (got: $STRIP_OUT)"

  # The diagnostic is the deliverable here, not a nicety: the raw failure is
  # "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an
  # exception", which names neither the library nor the reason, and the real
  # cause sits two levels down an InnerException chain no test output shows.
  if echo "$STRIP_OUT" | grep -q "native library" \
     && echo "$STRIP_OUT" | grep -q "rid=" \
     && echo "$STRIP_OUT" | grep -q "searched"; then
    ok "the failure names the library, the host rid, and the directories searched"
  else
    bad "the failure names the library, the host rid, and the directories searched (got: $STRIP_OUT)"
  fi
else
  bad "with native assets removed the SAME call fails (never healthy)"
  bad "the failure names the library, the host rid, and the directories searched (never healthy)"
fi
docker rm -f "natstrip-${NET}" >/dev/null 2>&1 || true
rm -rf "$(dirname "$STRIP")"

# The managed resolver on its own, with the entrypoint script bypassed so
# nothing is on LD_LIBRARY_PATH. The two layers cover different failures --
# the script exists for native-to-native dlopen, which no managed hook sees --
# so proving only the combination works would leave either one free to rot.
docker run -d --rm --name "natmgd-${NET}" --network "${NET}" --network-alias natmgd \
  --entrypoint dotnet \
  -v "${HERE}/publish/nativelib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=NativeLib.dll \
  -e LIB_REGISTRAR=NativeLib.NativeStartup.Configure \
  -e DOTNET_EnableDiagnostics=0 "$IMAGE" \
  /app/RemoteFacadeHost.dll >/dev/null 2>&1
if wait_healthy natmgd; then
  MGD_OUT=$(api natmgd /invoke -X POST -H 'Content-Type: application/json' -d '{"service":"NativeLib.IGitProbe","method":"InitAndCommit","args":["/tmp/natrepo"]}')
  echo "$MGD_OUT" | grep -qE '"result":"[0-9a-f]{40}"' \
    && ok "the managed resolver alone resolves native assets, with no LD_LIBRARY_PATH" \
    || bad "the managed resolver alone resolves native assets (got: $MGD_OUT)"
else
  bad "the managed resolver alone resolves native assets (never healthy)"
fi
docker rm -f "natmgd-${NET}" >/dev/null 2>&1 || true

echo "== wire-format baseline vs the previous release =="
if sh "${HERE}/baseline.sh" "${IMAGE}"; then
  ok "responses are byte-identical to v2.1.0 for a composition configuration"
else
  bad "responses DIFFER from v2.1.0 for a composition configuration"
fi

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]

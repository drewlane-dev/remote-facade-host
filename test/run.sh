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

start_host() { # start_host <alias> <pluginDir> <assembly> <type> <optionsJson> [servicesJson] [registrar] [callbacksJson]
  # DOTNET_EnableDiagnostics=0: belt-and-suspenders alongside giving the
  # plugin its own private RootPath (below) -- without it the runtime drops
  # its own diagnostic pipe/socket files straight into /tmp, which would
  # again pollute any OTHER process's directory count if it ever pointed
  # back at /tmp directly.
  #
  # servicesJson/callbacksJson are each computed on their own line rather
  # than inline as "${6:-{}}": a bare "{" inside a ${...:-word} default is
  # not brace-matched against the "}" that closes the expansion, so the
  # parser closes the expansion at the FIRST "}" and leaves a stray "}"
  # behind in the shell word -- corrupting the value (to "...}}") even when
  # the positional argument IS set.
  servicesJson="${6:-}"
  [ -z "$servicesJson" ] && servicesJson='{}'
  callbacksJson="${8:-}"
  [ -z "$callbacksJson" ] && callbacksJson='{}'
  docker run -d --rm --name "$1-${NET}" --network "${NET}" --network-alias "$1" \
    -v "$2:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY="$3" -e LIB_TYPE="$4" -e LIB_OPTIONS="$5" \
    -e LIB_SERVICES="$servicesJson" -e LIB_REGISTRAR="${7:-}" \
    -e LIB_CALLBACKS="$callbacksJson" \
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
# it, which is exactly the gap that let "unknown LIB_TYPE exits non-zero"
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
start_host cs "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp/cslib-data"}' \
  '{"CsLib.IStamp":"CsLib.RealStamp"}'
if wait_healthy cs; then
  ok "host constructs the configured type"
else
  docker logs "cs-${NET}" 2>&1 | tail -4
  bad "host constructs the configured type"
fi

echo "== /types is a usable diagnostic =="
api cs /types | grep -q "CsLib.Store" \
  && ok "/types lists the assembly's types" || bad "/types lists the assembly's types"

echo "== a missing type fails fast =="
# Not start_host: that uses --rm, which would remove the container the
# instant it exits, before wait_stopped can read its real exit code. A
# not-healthy poll alone can't distinguish "exited non-zero" (what we want
# to prove) from "still running but slow" or "network alias flaked" — all
# three used to report the same "ok".
docker run -d --name "bad1-${NET}" --network "${NET}" --network-alias bad1 \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.NoSuchType -e LIB_OPTIONS='{}' \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "bad1-${NET}")" && [ "$code" -ne 0 ]; then
  ok "unknown LIB_TYPE exits non-zero"
else
  docker logs "bad1-${NET}" 2>&1 | tail -4
  bad "unknown LIB_TYPE exits non-zero"
fi
docker rm -f "bad1-${NET}" >/dev/null 2>&1 || true

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
  -d '{"method":"VtValueAsync","args":[]}')
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

echo "== the client checks HTTP status before deserializing =="
# The proxy is pointed at a real listener that does not serve /invoke, so the
# response is a genuine 404. Deserializing that as the {ok, result} envelope
# fails with "'<' is an invalid start of a value" (or, for an empty body, "the
# input does not contain any JSON tokens") -- naming neither the interface, the
# method, the URL nor the status. CallbackProxy already guards the other
# direction exactly this way; both must fail the same.
STATUS_LINE=$(echo "$CLIENT_OUT" | grep '^RESULT: status-guard' || true)
if echo "$STATUS_LINE" | grep -q "IStore.Count" \
    && echo "$STATUS_LINE" | grep -q "404" \
    && echo "$STATUS_LINE" | grep -q "9099"; then
  ok "a non-200 names the interface, the method, the URL and the status"
else
  bad "a non-200 names the interface, the method, the URL and the status (got: $STATUS_LINE)"
fi

echo "== failures come back as {ok:false}, not a raw 500 =="
# Finding 1: an async method's exception surfaces at `await task`, as the
# ORIGINAL exception type, never wrapped in TargetInvocationException -- a
# catch scoped only around the synchronous call (or around method.Invoke
# alone) lets it escape /invoke as an unhandled 500. FailAsync throws AFTER
# yielding, so its exception can only ever be observed this way.
code=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"FailAsync","args":[]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"FailAsync","args":[]}')
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
  -d '{"method":"Touch","args":[{"not":"a string"}]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Touch","args":[{"not":"a string"}]}')
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
  -d '{"method":"RefArg","args":[1]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"RefArg","args":[1]}')
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
  -d '{"method":"Echo","args":[1]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Echo","args":[1]}')
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
  -d '{"method":"BadReturn","args":[]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"BadReturn","args":[]}')
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
  -d '{"method":"StreamReturn","args":[]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"StreamReturn","args":[]}')
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
  -d '{"method":"TypeArg","args":["x"]}' -o /dev/null -w '%{http_code}')
body=$(api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"TypeArg","args":["x"]}')
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
  -d '{"method":"Lock","args":[]}' >/dev/null
before=$(invoke_result '{"method":"LockCount","args":[]}')
code=$(api cs /instance -X DELETE -o /dev/null -w '%{http_code}')
after=$(invoke_result '{"method":"LockCount","args":[]}')
if [ "$code" = "204" ] && [ "$before" = "1" ] && [ "$after" = "0" ]; then
  ok "DELETE /instance resets the object"
else
  bad "DELETE /instance resets the object (code=$code before=$before after=$after)"
fi
api cs /health | grep -q "CsLib.Store" \
  && ok "host still serves after a reset" || bad "host still serves after a reset"

echo "== the same image serves a VB library, unchanged =="
start_host vb "${HERE}/publish/vblib" VbLib.dll VbLib.VbStore '{"RootPath":"/tmp"}'
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
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store \
    -e LIB_OPTIONS='{"RootPath":"/mnt/share"}' \
    -e LIB_SERVICES='{"CsLib.IStamp":"CsLib.RealStamp"}' \
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
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store -e LIB_OPTIONS='{}' \
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
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store -e LIB_OPTIONS='{}' \
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
start_host stampreal "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' \
  '{"CsLib.IStamp":"CsLib.RealStamp"}'
wait_healthy stampreal \
  && ok "constructs a type with a registered dependency" \
  || bad "constructs a type with a registered dependency"

api stampreal /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"real"' \
  && ok "resolves the registered implementation" || bad "resolves the registered implementation"

# The same library, a different implementation, no rebuild: this is how you
# substitute a fake for a dependency.
start_host stampfake "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' \
  '{"CsLib.IStamp":"CsLib.FakeStamp"}'
wait_healthy stampfake >/dev/null 2>&1
api stampfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"fake"' \
  && ok "a fake can be substituted by configuration alone" \
  || bad "a fake can be substituted by configuration alone"

echo "== a concrete dependency needs no registration, and can still be swapped =="
# CsLib.Store takes IStamp (an interface), but a real library often takes a
# concrete class. Nested.Outer(Nested.Inner) proves both halves: Inner resolves
# with no config, and can be replaced by a subclass with one line.
start_host nested "${HERE}/publish/cslib" CsLib.dll CsLib.Outer '{}' \
  '{"CsLib.IStamp":"CsLib.RealStamp"}'
wait_healthy nested \
  && ok "concrete dependency resolves with no registration" \
  || bad "concrete dependency resolves with no registration"

api nested /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Describe","args":[]}' | grep -q '"result":"inner-real"' \
  && ok "the real concrete dependency was used" || bad "the real concrete dependency was used"

start_host nestedfake "${HERE}/publish/cslib" CsLib.dll CsLib.Outer '{}' \
  '{"CsLib.IStamp":"CsLib.RealStamp","CsLib.Inner":"CsLib.FakeInner"}'
wait_healthy nestedfake >/dev/null 2>&1
api nestedfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Describe","args":[]}' | grep -q '"result":"inner-fake"' \
  && ok "a concrete dependency can be replaced by a subclass" \
  || bad "a concrete dependency can be replaced by a subclass"

echo "== LIB_REGISTRAR wires the graph from the app's own code =="
start_host reg "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' '{}' \
  CsLib.Registration.AddCsLib
wait_healthy reg \
  && ok "registrar supplies dependencies with no LIB_SERVICES" \
  || bad "registrar supplies dependencies with no LIB_SERVICES"

api reg /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"real"' \
  && ok "registrar's registration is used" || bad "registrar's registration is used"

# The combination that matters: real wiring, one thing faked.
start_host regfake "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' \
  '{"CsLib.IStamp":"CsLib.FakeStamp"}' CsLib.Registration.AddCsLib
wait_healthy regfake >/dev/null 2>&1
api regfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"fake"' \
  && ok "LIB_SERVICES overrides the registrar" || bad "LIB_SERVICES overrides the registrar"

echo "== auto-registration does not clobber the registrar's own wiring =="
# Configured is registered by AddCsLib with a FACTORY (ConfiguredFromFactory),
# and also appears as NeedsConfigured's nested constructor parameter. Before
# the fix, the concrete-dependency walk didn't check whether the container
# already had a descriptor for a type -- only whether LIB_SERVICES named it --
# so it re-registered Configured as a plain AddSingleton(pt, pt), and since the
# container resolves the LAST matching descriptor, that silently threw away
# the registrar's factory in favour of Configured's own (trivial)
# construction. "factory" here, not "default", proves the registrar's
# descriptor survived the walk.
start_host regconfigured "${HERE}/publish/cslib" CsLib.dll CsLib.NeedsConfigured '{}' '{}' \
  CsLib.Registration.AddCsLib
wait_healthy regconfigured \
  && ok "constructs a type with a registrar-supplied concrete dependency" \
  || bad "constructs a type with a registrar-supplied concrete dependency"

api regconfigured /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Describe","args":[]}' | grep -q '"result":"factory"' \
  && ok "auto-registration does not clobber the registrar's factory wiring" \
  || bad "auto-registration does not clobber the registrar's factory wiring"

echo "== an unregistered dependency still fails fast =="
# Not start_host: --rm would delete the container the instant it exits,
# before wait_stopped could read its real exit code -- the same gap
# "unknown LIB_TYPE exits non-zero" and the mount-fatal cases below guard
# against. Started directly, without --rm, so both the exit code and the
# logs survive long enough to inspect.
docker run -d --name "stampmissing-${NET}" --network "${NET}" --network-alias stampmissing \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store -e LIB_OPTIONS='{"RootPath":"/tmp"}' \
  -e LIB_SERVICES='{}' \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "stampmissing-${NET}")" && [ "$code" -ne 0 ]; then
  ok "unregistered dependency exits non-zero"
else
  docker logs "stampmissing-${NET}" 2>&1 | tail -4
  bad "unregistered dependency exits non-zero"
fi
docker logs "stampmissing-${NET}" 2>&1 | grep -q "IStamp" \
  && ok "the failure names the unsatisfiable dependency" \
  || bad "the failure names the unsatisfiable dependency"
docker rm -f "stampmissing-${NET}" >/dev/null 2>&1 || true

echo "== callback proxies: a Moq mock serving a remote instance =="
start_host cb "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp/cb-data"}' '{}' '' \
  '{"CsLib.IStamp":"http://testrunner:9090"}'
wait_healthy cb && ok "host starts with a callback-backed dependency" \
              || bad "host starts with a callback-backed dependency"

CB_OUT=$(docker run --rm --network "${NET}" --network-alias testrunner \
  -v "${HERE}/..:/w" -w /w mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/CallbackClient/CallbackClient.csproj -c Release 2>&1 \
  | grep '^RESULT:') || CB_OUT=""

# The guard must fire at registration (synchronously, inside the try around
# Serve itself) and name both the offending type and that an interface is
# required -- not merely "it threw something". A bare "it threw" assertion
# would also pass for an unrelated NullReferenceException.
SERVE_GUARD_LINE=$(echo "$CB_OUT" | grep "^RESULT: serve-guard" || true)
if echo "$SERVE_GUARD_LINE" | grep -q "CsLib.FakeStamp" \
    && echo "$SERVE_GUARD_LINE" | grep -qi "interface"; then
  ok "Serve<T> rejects a concrete type at registration, naming it"
else
  bad "Serve<T> rejects a concrete type at registration, naming it (got: $SERVE_GUARD_LINE)"
fi

echo "$CB_OUT" | grep -q "RESULT: stamp from-moq" \
  && ok "the mock's configured value reached the remote instance (string shape)" \
  || bad "the mock's configured value reached the remote instance (string shape)"
echo "$CB_OUT" | grep -q "RESULT: count 42" \
  && ok "the mock's Task<int> value reached the remote instance" \
  || bad "the mock's Task<int> value reached the remote instance"
echo "$CB_OUT" | grep -q "RESULT: ping ok" \
  && ok "a void callback round-trips with no error" \
  || bad "a void callback round-trips with no error"
echo "$CB_OUT" | grep -q "RESULT: fail-message mock-says-no" \
  && ok "a throwing mock's own message survives both hops" \
  || bad "a throwing mock's own message survives both hops"
echo "$CB_OUT" | grep -q "RESULT: verify ok" \
  && ok "Moq Verify sees every call the container made" \
  || bad "Moq Verify sees every call the container made"

# I5: the callback listener binds 0.0.0.0, is unauthenticated, and runs inside
# the developer's OWN test process -- so it must expose only what the served
# interface declares. Dispatch used target.GetType(), which made every public
# method of the served object reachable: a non-interface Secret() and Object's
# own ToString() were both verified callable. SecretStamp really does have a
# public Secret(), so refusing it is not vacuous, and the positive control
# below rules out a "guard" that merely broke all dispatch.
echo "$CB_OUT" | grep -q "RESULT: guard-allowed .*secret-stamp" \
  && ok "an interface method still dispatches to the served object" \
  || bad "an interface method still dispatches to the served object"

GUARD_SECRET=$(echo "$CB_OUT" | grep '^RESULT: guard-secret' || true)
if echo "$GUARD_SECRET" | grep -q '"ok":false' && ! echo "$GUARD_SECRET" | grep -q "LEAKED"; then
  ok "a public method NOT on the served interface is unreachable"
else
  bad "a public method NOT on the served interface is unreachable (got: $GUARD_SECRET)"
fi

GUARD_TOSTRING=$(echo "$CB_OUT" | grep '^RESULT: guard-tostring' || true)
if echo "$GUARD_TOSTRING" | grep -q '"ok":false'; then
  ok "Object's own members are unreachable through the callback listener"
else
  bad "Object's own members are unreachable through the callback listener (got: $GUARD_TOSTRING)"
fi

# C1's twin, in the callback direction. The container's Store calls back into
# a real Moq mock whose interface method returns ValueTask<T>; CallbackHost
# used to test only `result is Task`, miss the struct, and serialize the
# AWAITABLE ITSELF, which CallbackProxy then deserialized into a DEFAULT
# ValueTask<T> -- the library received null with ok:true and no error on any of
# the three hops. Driven through a RAW /invoke from inside the fixture (the
# CallbackHost only exists while that process runs) so the WHOLE ENVELOPE can
# be compared. Not grepped: the broken payload is
# {"ok":true,"result":{"isCompleted":false,...,"result":"vt-from-moq"}}, which
# CONTAINS the substring a grep would look for. That is not hypothetical --
# it is what the forward direction's first assertion did, and it was caught
# only by deleting the fix and reading the output.
CB_VT_VALUE=$(echo "$CB_OUT" | sed -n 's/^RESULT: cb-vt-value //p')
if [ "$CB_VT_VALUE" = '{"ok":true,"result":"vt-from-moq"}' ]; then
  ok "a mock's ValueTask<T> value survives the callback leg intact"
else
  bad "a mock's ValueTask<T> value survives the callback leg intact (got: $CB_VT_VALUE)"
fi

# A non-generic ValueTask carries no value, so the /invoke envelope is
# {"ok":true,"result":null} whether or not it was ever awaited. Only the mock's
# own side effect separates the two, and VtPingImpl sets its flag 500ms in --
# orders of magnitude after the round trip that an un-awaited ValueTask would
# return in.
CB_VT_PINGED=$(echo "$CB_OUT" | sed -n 's/^RESULT: cb-vt-pinged //p')
if [ "$CB_VT_PINGED" = "True" ]; then
  ok "a mock's plain ValueTask completes before the callback answers"
else
  bad "a mock's plain ValueTask completes before the callback answers (got: $CB_VT_PINGED)"
fi

echo "== naming an interface in both mechanisms is a startup error =="
# wait_stopped, not wait_healthy: asserting only "! wait_healthy" is true for
# ANY startup failure -- a bad plugin dir, a typo'd type, an image bug -- and
# never proves the container failed for THIS reason. Same pattern as
# "an unregistered dependency still fails fast" above: start without --rm so
# the exit code survives, then grep the logs for the specific message. Also
# faster: wait_healthy on a host that will never become healthy always burns
# the full 30s poll, where wait_stopped returns as soon as the process exits.
docker run -d --name "cbdup-${NET}" --network "${NET}" --network-alias cbdup \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store \
  -e LIB_OPTIONS='{"RootPath":"/tmp/d"}' \
  -e LIB_SERVICES='{"CsLib.IStamp":"CsLib.FakeStamp"}' \
  -e LIB_CALLBACKS='{"CsLib.IStamp":"http://testrunner:9090"}' \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "cbdup-${NET}")" && [ "$code" -ne 0 ]; then
  ok "an interface in both LIB_SERVICES and LIB_CALLBACKS exits non-zero"
else
  docker logs "cbdup-${NET}" 2>&1 | tail -4
  bad "an interface in both LIB_SERVICES and LIB_CALLBACKS exits non-zero"
fi
docker logs "cbdup-${NET}" 2>&1 | grep -q "named in BOTH" \
  && ok "the failure names both mechanisms as the cause" \
  || bad "the failure names both mechanisms as the cause"
docker rm -f "cbdup-${NET}" >/dev/null 2>&1 || true

echo "== the provider builds the root when it knows how =="
start_host factoryroot "${HERE}/publish/cslib" CsLib.dll CsLib.StringRooted '{}' '{}' \
  CsLib.GraphStartup.Configure
wait_healthy factoryroot && ok "a factory-registered root type constructs" \
                         || bad "a factory-registered root type constructs"
body=$(api factoryroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Label","args":[]}')
[ "$body" = '{"ok":true,"result":"from-factory"}' ] \
  && ok "the factory's value reached the instance" \
  || bad "the factory's value reached the instance"

echo "== LIB_TYPE may name an interface =="
start_host ifaceroot "${HERE}/publish/cslib" CsLib.dll CsLib.IRootFacade '{}' '{}' \
  CsLib.GraphStartup.Configure
wait_healthy ifaceroot && ok "an interface as LIB_TYPE resolves" \
                      || bad "an interface as LIB_TYPE resolves"
body=$(api ifaceroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Who","args":[]}')
[ "$body" = '{"ok":true,"result":"root-facade"}' ] \
  && ok "the registered implementation served the call" \
  || bad "the registered implementation served the call"

echo "== an interface LIB_TYPE reaches members it INHERITS (review final, C1) =="
# Type.GetMethods() on an INTERFACE returns only what that interface itself
# DECLARES -- base-interface members are absent, unlike for a class. Both of
# v1.1's new dispatch paths target an interface, so IBaseFacade.FromBase()
# was unreachable through IDerivedFacade while FromDerived() worked.
# FromDerived is asserted too, as the paired positive control: without it a
# FromBase failure could not be told apart from the fixture not being
# reachable at all.
start_host derivedroot "${HERE}/publish/cslib" CsLib.dll CsLib.IDerivedFacade '{}' '{}' \
  CsLib.GraphStartup.Configure
wait_healthy derivedroot && ok "an interface WITH a base interface resolves as LIB_TYPE" \
                        || bad "an interface WITH a base interface resolves as LIB_TYPE"
body=$(api derivedroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"FromDerived","args":[]}')
[ "$body" = '{"ok":true,"result":"derived-method"}' ] \
  && ok "a DECLARED member of an interface LIB_TYPE is callable" \
  || bad "a DECLARED member of an interface LIB_TYPE is callable (got: $body)"
body=$(api derivedroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"FromBase","args":[]}')
[ "$body" = '{"ok":true,"result":"base-method"}' ] \
  && ok "an INHERITED member of an interface LIB_TYPE is callable" \
  || bad "an INHERITED member of an interface LIB_TYPE is callable (got: $body)"

echo "== an interface LIB_TYPE honours LIB_OPTIONS (review final, C2) =="
# An interface has no constructor, so binding IOptions<T> off the ROOT's
# constructor skipped the whole block and CsLib.Store received a
# DEFAULT-constructed StoreOptions -- ok:true, no warning, and every write
# landing in /tmp instead of the configured RootPath. Measured before the
# fix, same image and same LIB_OPTIONS: the concrete LIB_TYPE wrote to
# /tmp/opt-check, the interface one wrote to /tmp.
#
# Both halves are checked server-side, inside the container: the file IS at
# the configured path AND is NOT at the default. The second is what makes
# this non-vacuous -- StoreOptions' default is /tmp, so "the write succeeded"
# was true against the bug too.
start_host ifaceopts "${HERE}/publish/cslib" CsLib.dll CsLib.IStore \
  '{"RootPath":"/tmp/opt-check"}' '{"CsLib.IStore":"CsLib.Store","CsLib.IStamp":"CsLib.RealStamp"}'
wait_healthy ifaceopts && ok "an interface LIB_TYPE with LIB_OPTIONS starts" \
                      || bad "an interface LIB_TYPE with LIB_OPTIONS starts"
body=$(api ifaceopts /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"WriteAsync","args":["opt.txt","hello"]}')
[ "$body" = '{"ok":true,"result":null}' ] \
  && ok "the interface-hosted implementation served the write" \
  || bad "the interface-hosted implementation served the write (got: $body)"
docker exec "ifaceopts-${NET}" sh -c 'test -f /tmp/opt-check/opt.txt' \
  && ok "LIB_OPTIONS reached the implementation behind an interface LIB_TYPE" \
  || bad "LIB_OPTIONS reached the implementation behind an interface LIB_TYPE"
docker exec "ifaceopts-${NET}" sh -c 'test ! -f /tmp/opt.txt' \
  && ok "the write did NOT land at StoreOptions' own default path" \
  || bad "the write did NOT land at StoreOptions' own default path"

echo "== a LIB_OPTIONS that cannot be bound is fatal, not silently discarded =="
# The one shape left after the fix: an interface LIB_TYPE whose
# implementation is supplied ONLY by LIB_REGISTRAR. Finding it would mean
# inspecting ServiceDescriptors, which this codebase deliberately does not do
# (see Activation.cs's ownership comment), so the host says so at startup
# rather than quietly serving default options -- which is the failure the
# whole finding is about.
#
# CsLib.IStore via OptionsFacadeStartup, deliberately, NOT the IRootFacade
# this case first used: RootFacade takes no options at all, so that version
# could not tell "refused because no implementation was NAMED" (the only
# licensed reason) from "refused because nothing ASKED for options" (which
# fired on correct configurations too -- see the two cases below). Store
# genuinely takes IOptions<StoreOptions>, so this exercises the guard for the
# reason it exists.
docker run -d --name "optsblind-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.IStore \
  -e LIB_OPTIONS='{"RootPath":"/tmp/nowhere"}' \
  -e LIB_REGISTRAR=CsLib.OptionsFacadeStartup.Configure \
  -e DOTNET_EnableDiagnostics=0 \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "optsblind-${NET}")" && [ "$code" -ne 0 ]; then
  ok "an unbindable LIB_OPTIONS exits non-zero instead of using defaults"
else
  bad "an unbindable LIB_OPTIONS exits non-zero instead of using defaults"
fi
docker logs "optsblind-${NET}" 2>&1 | grep -q "LIB_OPTIONS" \
  && docker logs "optsblind-${NET}" 2>&1 | grep -q "LIB_SERVICES" \
  && ok "the unbindable-options failure names LIB_OPTIONS and the way out" \
  || bad "the unbindable-options failure names LIB_OPTIONS and the way out"
docker rm -f "optsblind-${NET}" >/dev/null 2>&1 || true

echo "== that guard does NOT fire when LIB_SERVICES really does name the implementation =="
# The guard's job is "no implementation is identifiable". Whether that
# implementation happens to want an IOptions<T> is a different question, and
# conflating the two made this exact configuration -- an interface root with
# a perfectly good LIB_SERVICES mapping -- die at startup telling the
# operator to add the mapping they had already added.
#
# RootFacade taking NO options is the point of the case, not an oversight:
# it is the only way to separate the two conditions. The ifaceopts case above
# already covers a mapped implementation that DOES take options, and passed
# either way, which is why it could not catch this.
start_host ifacemapped "${HERE}/publish/cslib" CsLib.dll CsLib.IRootFacade \
  '{"RootPath":"/tmp/unused"}' '{"CsLib.IRootFacade":"CsLib.RootFacade"}'
wait_healthy ifacemapped \
  && ok "an interface LIB_TYPE mapped in LIB_SERVICES starts despite a non-empty LIB_OPTIONS" \
  || bad "an interface LIB_TYPE mapped in LIB_SERVICES starts despite a non-empty LIB_OPTIONS"
# `|| true` on this one assignment, unlike its neighbours: this is the case
# whose container is ABSENT when the guard misfires (start_host uses --rm, so
# a host that dies at startup leaves nothing behind). Unguarded under
# `set -eu`, curl's connection failure aborts the whole suite here -- which,
# because `trap cleanup EXIT` ends in `|| true`, exits 0 and reports SUCCESS.
# Observed exactly that while verifying this case by reverting the fix.
body=$(api ifacemapped /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Who","args":[]}' || true)
[ "$body" = '{"ok":true,"result":"root-facade"}' ] \
  && ok "and it actually serves calls" \
  || bad "and it actually serves calls (got: $body)"

echo "== nor when the root is a CLASS that simply cannot be constructed =="
# A type with only a private constructor also has no public constructor, so a
# guard keyed on "has no constructor" swallowed it and reported an options
# problem instead of the real one. The accurate message names the type and
# the constructor; the wrong one names LIB_OPTIONS. Both halves are asserted,
# because "exits non-zero" is true either way -- that is exactly how the
# pre-existing privctor case above stayed green through this.
docker run -d --name "privctoropts-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.PrivateCtorRoot \
  -e LIB_OPTIONS='{"RootPath":"/tmp/nowhere"}' \
  -e DOTNET_EnableDiagnostics=0 \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "privctoropts-${NET}")" && [ "$code" -ne 0 ]; then
  ok "an unconstructible class with a non-empty LIB_OPTIONS still exits non-zero"
else
  bad "an unconstructible class with a non-empty LIB_OPTIONS still exits non-zero"
fi
docker logs "privctoropts-${NET}" 2>&1 | grep -qi "suitable constructor" \
  && ok "it fails with the CONSTRUCTOR message, which names the real mistake" \
  || bad "it fails with the CONSTRUCTOR message, which names the real mistake"
docker logs "privctoropts-${NET}" 2>&1 | grep -q "LIB_OPTIONS is set" \
  && bad "the options guard must not mask the constructor failure" \
  || ok "the options guard does not mask the constructor failure"
docker rm -f "privctoropts-${NET}" >/dev/null 2>&1 || true

echo "== DELETE /instance disposes an IDisposable root (review r1, finding 1) =="
# ActivatorUtilities-constructed (no LIB_REGISTRAR): nothing tracks this
# instance but HostedGraph itself, so IF it were not disposed there, it would
# never be disposed at all. The sentinel is a file INSIDE the container --
# checked via docker exec, the same server-side-truth technique the samba
# tests already use -- because that is where Dispose() actually runs.
start_host disposeau "${HERE}/publish/cslib" CsLib.dll CsLib.DisposableRoot \
  '{"SentinelPath":"/tmp/disposed-au"}' '{}'
wait_healthy disposeau && ok "a disposable root constructs" \
                       || bad "a disposable root constructs"

docker exec "disposeau-${NET}" sh -c 'test ! -f /tmp/disposed-au' \
  && ok "not disposed before DELETE /instance" \
  || bad "not disposed before DELETE /instance"

code=$(api disposeau /instance -X DELETE -o /dev/null -w '%{http_code}')
[ "$code" = "204" ] \
  && ok "DELETE /instance returns 204 for a disposable root" \
  || bad "DELETE /instance returns 204 for a disposable root (code=$code)"

# Compared as the WHOLE file content, not grepped for the substring
# "disposed": a bare grep would also pass if Dispose() ran TWICE, which is
# exactly what the next block exists to rule out.
sentinel=$(docker exec "disposeau-${NET}" sh -c 'cat /tmp/disposed-au 2>/dev/null' 2>/dev/null || true)
[ "$sentinel" = "disposed" ] \
  && ok "reset disposes an IDisposable root exactly once" \
  || bad "reset disposes an IDisposable root exactly once (got: $sentinel)"

echo "== a root resolved FROM the provider is not disposed a second time (review r1, finding 1) =="
# Same fixture, but registered with the container via LIB_REGISTRAR instead
# of left for ActivatorUtilities: the container itself tracks and disposes
# it. HostedGraph must not ALSO call Dispose() on it -- doing so would rely
# on the plugin's Dispose() being safe to call twice, which nothing
# guarantees. If it ran twice, the sentinel would show two lines instead of
# one.
start_host disposeprov "${HERE}/publish/cslib" CsLib.dll CsLib.DisposableRoot \
  '{"SentinelPath":"/tmp/disposed-prov"}' '{}' CsLib.DisposableRootStartup.Configure
wait_healthy disposeprov && ok "a provider-owned disposable root constructs" \
                         || bad "a provider-owned disposable root constructs"

code=$(api disposeprov /instance -X DELETE -o /dev/null -w '%{http_code}')
[ "$code" = "204" ] \
  && ok "DELETE /instance returns 204 for a provider-owned disposable root" \
  || bad "DELETE /instance returns 204 for a provider-owned disposable root (code=$code)"

sentinel=$(docker exec "disposeprov-${NET}" sh -c 'cat /tmp/disposed-prov 2>/dev/null' 2>/dev/null || true)
[ "$sentinel" = "disposed" ] \
  && ok "a provider-owned root is disposed exactly once, not twice" \
  || bad "a provider-owned root is disposed exactly once, not twice (got: $sentinel)"

# The double-dispose this guards against would otherwise surface as the
# container crashing on reset (an unhandled exception from a non-idempotent
# Dispose()) -- assert the host is still serving, not just that /instance
# returned 204 before any crash had a chance to happen.
wait_healthy disposeprov && ok "the host survives a provider-owned root's reset" \
                          || bad "the host survives a provider-owned root's reset"

echo "== an INSTANCE-registered root is NOT disposed on reset -- deliberate (review r3) =="
# services.AddSingleton<Root>(existingInstance) -- the ordinary LIB_REGISTRAR
# shape. Round 2 made HostedGraph dispose this itself, by inspecting the
# ServiceDescriptor that served the resolution. Round 3 deleted that scan: it
# threw on a KEYED registration for the root's type and mis-computed
# ownership for an open generic -- descriptor shapes are an open set no scan
# can enumerate completely. The rule that replaced it is exactly .NET's own:
# the provider disposes what it CREATED; fromProvider is not null covers
# every shape the CONTAINER can build (type, factory, open generic, ...).
# An instance registration is not one of those -- the container did not
# create this object, so it is not tracked for disposal by .NET's own rules
# either, and HostedGraph now (correctly) leaves it alone too.
#
# This is a DELIBERATE semantics change, not a regression: in any ordinary
# ASP.NET Core app the container does not dispose an instance it did not
# create -- the caller who built it owns its lifetime. Us disposing someone
# else's object on DELETE /instance would be the surprising behaviour. The
# cost is real: a root registered as an instance and holding an OS resource
# is NOT released on reset. Register a type or a factory instead if reset
# needs to release it.
start_host disposeinst "${HERE}/publish/cslib" CsLib.dll CsLib.DisposableRoot '{}' '{}' \
  CsLib.DisposableRootInstanceStartup.Configure
wait_healthy disposeinst && ok "an instance-registered disposable root constructs" \
                         || bad "an instance-registered disposable root constructs"

# POSITIVE CONTROL for the negative assertion at the end of this block.
# "$sentinel is empty" passes for every failure of the MEASUREMENT itself --
# a wrong path, a renamed fixture, docker exec failing -- and unlike the
# sibling disposeau block, nothing else here proves the sentinel mechanism
# works for THIS fixture and THIS path. So write a value through the exact
# path that assertion reads, read it back with the exact command it uses, and
# require it to come back. Run BEFORE the reset, deliberately: afterwards it
# would overwrite whatever the reset had written and mask the very regression
# the negative assertion exists to catch.
docker exec "disposeinst-${NET}" sh -c 'printf disposed > /tmp/disposed-instance' >/dev/null 2>&1 || true
control=$(docker exec "disposeinst-${NET}" sh -c 'cat /tmp/disposed-instance 2>/dev/null' 2>/dev/null || true)
[ "$control" = "disposed" ] \
  && ok "the instance-root sentinel WOULD be visible if it were written" \
  || bad "the instance-root sentinel WOULD be visible if it were written (got: ${control:-absent})"
docker exec "disposeinst-${NET}" sh -c 'rm -f /tmp/disposed-instance' >/dev/null 2>&1 || true
docker exec "disposeinst-${NET}" sh -c 'test ! -f /tmp/disposed-instance' \
  && ok "the control left the sentinel absent again before the reset" \
  || bad "the control left the sentinel absent again before the reset"

code=$(api disposeinst /instance -X DELETE -o /dev/null -w '%{http_code}')
[ "$code" = "204" ] \
  && ok "DELETE /instance returns 204 for an instance-registered disposable root" \
  || bad "DELETE /instance returns 204 for an instance-registered disposable root (code=$code)"

sentinel=$(docker exec "disposeinst-${NET}" sh -c 'cat /tmp/disposed-instance 2>/dev/null' 2>/dev/null || true)
[ -z "$sentinel" ] \
  && ok "reset does NOT dispose an instance-registered root (caller owns it)" \
  || bad "reset does NOT dispose an instance-registered root (caller owns it) (got: $sentinel)"

echo "== a KEYED registration for the root's type does not crash construction (review r3, critical) =="
# KeyedServiceStartup registers KeyedProbe BOTH unkeyed and keyed (unkeyed
# first). provider.GetService(typeof(KeyedProbe)) (unkeyed) resolves the
# unkeyed registration regardless of order, so this configuration works
# today, and must keep working. The finding this guards is that the earlier
# descriptor-scan approach to disposal ownership read
# ServiceDescriptor.ImplementationInstance on whatever descriptor a plain
# `LastOrDefault(d => d.ServiceType == rootType)` happened to match -- here,
# the KEYED one, since it was registered second. On
# Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0 that property
# THREW for a keyed descriptor (dotnet/runtime#95789); 9.0.0+ (what this
# project's net10.0 / package 10.0.0 actually restores) fixed it to return
# null instead, so this exact scan no longer crashes on THIS package
# version -- verified directly, not assumed (see task-2-report.md). The
# test stands regardless: it pins that a keyed registration for the root's
# own type never affects construction, and the scan this guards against
# is deleted, not merely lucky on the currently-restored package version.
start_host keyedroot "${HERE}/publish/cslib" CsLib.dll CsLib.KeyedProbe '{}' '{}' \
  CsLib.KeyedServiceStartup.Configure
wait_healthy keyedroot \
  && ok "a root type with an unrelated KEYED registration still constructs" \
  || bad "a root type with an unrelated KEYED registration still constructs"

body=$(api keyedroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Ping","args":[]}')
[ "$body" = '{"ok":true,"result":"keyed-probe-alive"}' ] \
  && ok "the root serves calls despite the keyed registration" \
  || bad "the root serves calls despite the keyed registration (got: $body)"

echo "== an OPEN-GENERIC root disposes exactly once (review r3) =="
# GenericDisposableRoot<> is registered as an OPEN generic mapping -- no
# descriptor's ServiceType equals the CLOSED root type LIB_TYPE names here,
# which is what made the deleted descriptor-scan compute ownership as FALSE
# and double-dispose it (once via HostedGraph's own explicit call, once via
# the provider, which DOES track and dispose what it creates by closing an
# open generic). fromProvider is not null covers this shape with no
# inspection: the container created this instance, so the provider disposes
# it, and HostedGraph correctly leaves it alone.
#
# LIB_TYPE names the CLOSED generic type via .NET's own reflection type-name
# syntax (verified to round-trip through Assembly.GetType exactly as
# PluginLoader.Load resolves it, via a standalone reflection check against
# the built CsLib.dll -- not guessed).
start_host opengen "${HERE}/publish/cslib" CsLib.dll \
  'CsLib.GenericDisposableRoot`1[[CsLib.OpenGenericArg, CsLib, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]]' \
  '{}' '{}' CsLib.OpenGenericStartup.Configure
wait_healthy opengen && ok "an open-generic root constructs" \
                     || bad "an open-generic root constructs"

code=$(api opengen /instance -X DELETE -o /dev/null -w '%{http_code}')
[ "$code" = "204" ] \
  && ok "DELETE /instance returns 204 for an open-generic root" \
  || bad "DELETE /instance returns 204 for an open-generic root (code=$code)"

sentinel=$(docker exec "opengen-${NET}" sh -c 'cat /tmp/disposed-generic 2>/dev/null' 2>/dev/null || true)
[ "$sentinel" = "disposed" ] \
  && ok "an open-generic root is disposed exactly once, not twice" \
  || bad "an open-generic root is disposed exactly once, not twice (got: $sentinel)"

echo "== a root with no public constructor for a reason OTHER than being an interface still fails fast (review r1, finding 3) =="
# Not start_host: --rm would remove the container before wait_stopped can
# read its real exit code, same as every other fatal-startup case above.
docker run -d --name "privctor-${NET}" --network "${NET}" --network-alias privctor \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.PrivateCtorRoot -e LIB_OPTIONS='{}' \
  "$IMAGE" >/dev/null 2>&1
if code="$(wait_stopped "privctor-${NET}")" && [ "$code" -ne 0 ]; then
  ok "a type with only a private constructor exits non-zero"
else
  docker logs "privctor-${NET}" 2>&1 | tail -4
  bad "a type with only a private constructor exits non-zero"
fi
docker logs "privctor-${NET}" 2>&1 | grep -q "CsLib.PrivateCtorRoot" \
  && ok "the failure names the type" \
  || bad "the failure names the type"
docker rm -f "privctor-${NET}" >/dev/null 2>&1 || true

echo "== a registered root's construction goes through the container's OWN constructor selection (review r1, finding 2) =="
# Registered via LIB_REGISTRAR, not left for ActivatorUtilities: the
# container's own activation picks the constructor, and it does not honour
# [ActivatorUtilitiesConstructor] -- that attribute is an ActivatorUtilities-
# only convention the container's own resolver never looks at. Pinned here as
# OBSERVED behaviour, not a requirement this host imposes: provider-first
# means honouring the registration, activator semantics included, even where
# they differ from ActivatorUtilities'.
start_host multictor "${HERE}/publish/cslib" CsLib.dll CsLib.MultiCtorRoot '{}' '{}' \
  CsLib.MultiCtorStartup.Configure
wait_healthy multictor && ok "a root with multiple constructors, registered, constructs" \
                       || bad "a root with multiple constructors, registered, constructs"
body=$(api multictor /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"WhichCtor","args":[]}')
[ "$body" = '{"ok":true,"result":"two"}' ] \
  && ok "the container's own constructor selection runs, not ActivatorUtilities'" \
  || bad "the container's own constructor selection runs, not ActivatorUtilities' (got: $body)"

echo "== composition-root mode: no LIB_TYPE =="
docker run -d --name "croot-${NET}" --network "${NET}" --network-alias croot \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e LIB_REGISTRAR=CsLib.GraphStartup.Configure \
  -e DOTNET_EnableDiagnostics=0 \
  "${IMAGE}" >/dev/null
wait_healthy croot && ok "the host starts with no LIB_TYPE" \
                  || bad "the host starts with no LIB_TYPE"

docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s -m 10 \
  http://croot:8080/services | grep -q "CsLib.IRootFacade" \
  && ok "GET /services lists a registered service" \
  || bad "GET /services lists a registered service"

echo "== neither LIB_TYPE nor LIB_REGISTRAR is fatal =="
docker run -d --name "noconfig-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e DOTNET_EnableDiagnostics=0 \
  "${IMAGE}" >/dev/null
if wait_stopped "noconfig-${NET}" \
   && [ "$(docker inspect -f '{{.State.ExitCode}}' "noconfig-${NET}")" != "0" ]; then
  ok "a host with neither LIB_TYPE nor LIB_REGISTRAR exits non-zero"
else
  bad "a host with neither LIB_TYPE nor LIB_REGISTRAR exits non-zero"
fi
docker logs "noconfig-${NET}" 2>&1 | grep -q "LIB_TYPE" \
  && docker logs "noconfig-${NET}" 2>&1 | grep -q "LIB_REGISTRAR" \
  && ok "the failure names both variables" \
  || bad "the failure names both variables"
docker rm -f "noconfig-${NET}" >/dev/null 2>&1 || true

out=$(api croot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Who","args":[]}')
echo "$out" | grep -qi "composition-root\|must name a service" \
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
# Same defect as the interface-LIB_TYPE case above, on the OTHER new v1.1
# path: /invoke's "service" field dispatches against the type Resolve found,
# which is the interface. CallbackHost.cs already ruled that a member
# inherited from a base interface IS part of the served contract; Invoker now
# agrees. FromDerived is the paired positive control.
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
# named as LIB_TYPE and registered by nothing is built by ActivatorUtilities, so
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
start_host inflight "${HERE}/publish/cslib" CsLib.dll CsLib.DisposableRoot \
  '{"SentinelPath":"/tmp/disposed-inflight"}' '{}' CsLib.GraphStartup.Configure
wait_healthy inflight && ok "the in-flight host constructs" \
                      || bad "the in-flight host constructs"

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
start_host disposethrow "${HERE}/publish/cslib" CsLib.dll CsLib.ThrowingDisposableRoot \
  '{"SentinelPath":"/tmp/throwing-disposed"}' '{}' CsLib.GraphStartup.Configure
wait_healthy disposethrow && ok "a root with a throwing Dispose() constructs" \
                          || bad "a root with a throwing Dispose() constructs"

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
idle_code=$(api disposethrow /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$idle_code" = "500" ] \
  && ok "with nothing in flight, a throwing Dispose() is reported to DELETE /instance" \
  || bad "with nothing in flight, a throwing Dispose() is reported to DELETE /instance (code=${idle_code:-none})"

echo "== a throwing root still lets the provider dispose its singletons (review final, I1) =="
# HostedGraph.Dispose() had no try/finally: the root's Dispose() threw and
# provider.Dispose() never ran, so EVERY provider-owned singleton survived the
# reset undisposed while the host went on reporting itself healthy. Each
# further reset leaked another whole graph. The suite already shipped a
# throwing-Dispose fixture and could not see this, because nothing GraphStartup
# registers is IDisposable -- OwnedResource is the missing instrument.
start_host ownedthrow "${HERE}/publish/cslib" CsLib.dll CsLib.ThrowingDisposableRoot \
  '{"SentinelPath":"/tmp/throwing-owned"}' '{}' CsLib.ThrowingRootOwnedStartup.Configure
wait_healthy ownedthrow && ok "a throwing root with an owned singleton constructs" \
                        || bad "a throwing root with an owned singleton constructs"

# Resolve OwnedResource so the provider actually CREATES and tracks it. The
# provider only disposes what it created, so without this the assertion below
# would pass against the bug.
api_service ownedthrow CsLib.OwnedResource Ping >/dev/null

owned_code=$(api ownedthrow /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$owned_code" = "500" ] \
  && ok "the throwing root is still reported to DELETE /instance" \
  || bad "the throwing root is still reported to DELETE /instance (code=${owned_code:-none})"

# Positive control for the case: the root's Dispose() really ran and really
# threw. Without it, a graph that skipped disposal entirely would satisfy
# nothing below and still look plausible.
root_lines=$(docker exec "ownedthrow-${NET}" sh -c \
  'wc -l < /tmp/throwing-owned 2>/dev/null || echo 0' 2>/dev/null | tr -d ' ')
[ "$root_lines" = "1" ] \
  && ok "the root's throwing Dispose() ran exactly once" \
  || bad "the root's throwing Dispose() ran exactly once (lines: ${root_lines:-none})"

owned_sentinel=$(docker exec "ownedthrow-${NET}" sh -c \
  'cat /tmp/owned-disposed 2>/dev/null' 2>/dev/null || true)
[ "$owned_sentinel" = "disposed" ] \
  && ok "a provider-owned singleton is disposed even though the root's Dispose() threw" \
  || bad "a provider-owned singleton is disposed even though the root's Dispose() threw (got: ${owned_sentinel:-absent})"

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
start_host asyncdisp "${HERE}/publish/cslib" CsLib.dll '' '{}' '{}' \
  CsLib.AsyncOnlyStartup.Configure
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

echo "== an IAsyncDisposable-ONLY root is disposed too =="
# LIB_TYPE, registered by nothing, so ActivatorUtilities builds it and
# HostedGraph is the only thing that can dispose it -- and it used to look for
# IDisposable alone, so a root of this shape was disposed by nobody at all.
start_host asyncroot "${HERE}/publish/cslib" CsLib.dll CsLib.AsyncOnlyRoot '{}' '{}'
wait_healthy asyncroot && ok "an IAsyncDisposable-only root constructs" \
                      || bad "an IAsyncDisposable-only root constructs"
docker exec "asyncroot-${NET}" sh -c 'test ! -f /tmp/async-root-disposed' \
  && ok "the async-only root is not disposed before DELETE /instance" \
  || bad "the async-only root is not disposed before DELETE /instance"
asyncroot_code=$(api asyncroot /instance -X DELETE -m 20 -o /dev/null -w '%{http_code}' || true)
[ "$asyncroot_code" = "204" ] \
  && ok "DELETE /instance returns 204 for an IAsyncDisposable-only root" \
  || bad "DELETE /instance returns 204 for an IAsyncDisposable-only root (code=${asyncroot_code:-none})"
asyncroot_sentinel=$(docker exec "asyncroot-${NET}" sh -c \
  'cat /tmp/async-root-disposed 2>/dev/null' 2>/dev/null || true)
[ "$asyncroot_sentinel" = "disposed" ] \
  && ok "an IAsyncDisposable-only root is disposed exactly once on reset" \
  || bad "an IAsyncDisposable-only root is disposed exactly once on reset (got: ${asyncroot_sentinel:-absent})"

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
start_host nat "${HERE}/publish/nativelib" NativeLib.dll NativeLib.GitProbe '{}'
if wait_healthy nat; then
  NAT_OUT=$(api nat /invoke -X POST -H 'Content-Type: application/json' -d '{"method":"InitAndCommit","args":["/tmp/natrepo"]}')
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
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=NativeLib.dll -e LIB_TYPE=NativeLib.GitProbe \
  -e LIB_OPTIONS='{}' -e DOTNET_EnableDiagnostics=0 "$IMAGE" >/dev/null 2>&1
if wait_healthy natstrip; then
  STRIP_OUT=$(api natstrip /invoke -X POST -H 'Content-Type: application/json' -d '{"method":"InitAndCommit","args":["/tmp/natrepo"]}')
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
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=NativeLib.dll -e LIB_TYPE=NativeLib.GitProbe \
  -e LIB_OPTIONS='{}' -e DOTNET_EnableDiagnostics=0 "$IMAGE" \
  /app/RemoteFacadeHost.dll >/dev/null 2>&1
if wait_healthy natmgd; then
  MGD_OUT=$(api natmgd /invoke -X POST -H 'Content-Type: application/json' -d '{"method":"InitAndCommit","args":["/tmp/natrepo"]}')
  echo "$MGD_OUT" | grep -qE '"result":"[0-9a-f]{40}"' \
    && ok "the managed resolver alone resolves native assets, with no LD_LIBRARY_PATH" \
    || bad "the managed resolver alone resolves native assets (got: $MGD_OUT)"
else
  bad "the managed resolver alone resolves native assets (never healthy)"
fi
docker rm -f "natmgd-${NET}" >/dev/null 2>&1 || true

echo "== wire-format baseline vs the previous release =="
if sh "${HERE}/baseline.sh" "${IMAGE}"; then
  ok "responses are byte-identical to v1.0.1 for a v1.0 configuration"
else
  bad "responses DIFFER from v1.0.1 for a v1.0 configuration"
fi

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]

#!/bin/sh
# Puts the plugin's native asset directories on the dynamic loader's search
# path, then starts the host.
#
# NativeResolver (src/RemoteFacadeHost/NativeResolver.cs) already handles the
# managed side: a P/Invoke from a plugin assembly that default probing cannot
# satisfy. This script exists for the case that hook CANNOT see -- one native
# library dlopen()ing a sibling native library directly, never passing through
# the CLR. SkiaSharp does this; so does anything shipping a split .so set. No
# managed hook fires for those, so the only fix is a real LD_LIBRARY_PATH.
#
# And it has to be set HERE rather than in C#, because the dynamic loader reads
# LD_LIBRARY_PATH once when the process starts. Setting it from inside the
# running host would affect child processes and nothing else.
set -eu

DIR="${LIB_DIR:-/plugin}"

# The image is Alpine, so musl -- but derive it rather than hardcode it, so
# this keeps working if the base image is ever changed to a glibc one.
machine="$(uname -m)"
case "$machine" in
  x86_64)  arch=x64   ;;
  aarch64) arch=arm64 ;;
  armv7l)  arch=arm   ;;
  *)       arch="$machine" ;;
esac
if [ -e "/lib/ld-musl-${machine}.so.1" ]; then
  own="linux-musl-${arch}"
else
  own="linux-${arch}"
fi

# This runtime's own RID first so it wins, then any other linux-* directory as
# a fallback. Non-Linux RIDs (win-*, osx-*) are skipped: the loader would
# reject them anyway and they only lengthen every miss.
paths=""
add() { [ -d "$1" ] && paths="${paths:+$paths:}$1" || true; }

add "${DIR}/runtimes/${own}/native"
for d in "${DIR}"/runtimes/linux-*/native; do
  [ -d "$d" ] || continue
  [ "$d" = "${DIR}/runtimes/${own}/native" ] && continue
  add "$d"
done
# A RID-specific `dotnet publish` flattens native assets into the output root
# instead of under runtimes/, so the plugin directory itself is a real case.
add "${DIR}"

if [ -n "$paths" ]; then
  export LD_LIBRARY_PATH="${paths}${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi

# exec, not a plain call: the host must be PID 1 so it receives the container's
# stop signal directly instead of waiting for this shell to forward it.
exec dotnet /app/RemoteFacadeHost.dll "$@"

#!/bin/sh
# Puts the plugin's native asset directories on the dynamic loader's search
# path, then starts the host.
#
# This is the ONLY thing that makes a plugin's native assets findable. A
# managed ResolvingUnmanagedDll hook used to sit alongside it and was removed:
# that hook fires only after default probing FAILS, default probing is dlopen,
# and dlopen reads LD_LIBRARY_PATH -- so whenever this script had run, the hook
# could not fire. Measured, not assumed.
#
# The reverse is not true, which is why this side is the one that survived. A
# native library with a sibling dependency in the same directory cannot be
# loaded by absolute path -- all a managed hook can do -- unless it sets an
# $ORIGIN RUNPATH, because the dynamic loader resolves DT_NEEDED against
# RUNPATH, LD_LIBRARY_PATH and the system paths, and never against the
# directory of the object being loaded. Measured on a purpose-built pair:
# without RUNPATH, dlopen("/abs/libparent.so") fails with "Error loading shared
# library libchild.so"; with the directory here, it succeeds.
#
# (An earlier version of this comment cited SkiaSharp as the example. That is
# wrong and was checked: libSkiaSharp.so needs libfontconfig.so.1, a SYSTEM
# library this path cannot supply and which the image does not install, plus
# libc -- no siblings at all. libgit2 has none either. The mechanism is real;
# neither package this repo tests exercises it.)
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

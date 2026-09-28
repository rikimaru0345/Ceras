#!/usr/bin/env bash
#
# Ceras trimming / NativeAOT smoke test.
#
# Builds samples/TrimAotSmoke four ways and runs each result, so a failure can
# be attributed to trimming, to NativeAOT, or to neither:
#
#   jit      framework-dependent, no trimmer            -> the control
#   sc       self-contained, no trimmer                 -> isolates "self-contained"
#   trim     self-contained + PublishTrimmed           -> isolates "trimming"
#   aot      PublishAot (implies trimming)             -> isolates "NativeAOT"
#
# Usage:
#   ./samples/TrimAotSmoke/run-smoke.sh            # all four
#   ./samples/TrimAotSmoke/run-smoke.sh trim aot   # only the named ones
#
# Requirements (all verified on linux-x64 / .NET SDK 10.0.111):
#   - a C++ toolchain for the NativeAOT link step (gcc/ld/objcopy are enough;
#     clang is NOT required). zlib development files are not needed for this
#     project.
#   - the ILCompiler + ILLink NuGet packages (restored on first run).
#
# This is intentionally NOT wired into Ceras.sln or into `dotnet test`: a
# PublishAot run takes minutes, and the ordinary build/test must stay fast.

set -u -o pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
proj="$here/TrimAotSmoke.csproj"
outroot="${TRIM_AOT_OUT:-$repo/samples/TrimAotSmoke/artifacts}"
rid="${TRIM_AOT_RID:-linux-x64}"

# PublishTrimmed / PublishAot are set inside the csproj and are NEVER set to
# `true` here, because a global property is inherited by every project in the
# graph and src/Ceras.SourceGenerator (netstandard2.0, no entry point) then fails
# the whole publish:
#
#   -p:PublishAot=true     -> error NETSDK1207: Ahead-of-time compilation is not
#                             supported for the target framework.
#   -p:PublishTrimmed=true -> error NETSDK1124: Trimming assemblies requires
#                             .NET Core 3.0 or higher.
#
# The csproj defaults both to true, so `trim` and `aot` only ever need to turn
# one of them OFF, and `false` is harmless to propagate.
modes=("$@")
[ ${#modes[@]} -eq 0 ] && modes=(jit sc trim aot)

declare -A extra=(
  [jit]="-p:SelfContained=false -p:PublishTrimmed=false -p:PublishAot=false"
  [sc]="-p:SelfContained=true -p:PublishTrimmed=false -p:PublishAot=false"
  [trim]="-p:SelfContained=true -p:PublishAot=false"
  [aot]="-p:SelfContained=true"
)

overall=0
declare -A result=()

for mode in "${modes[@]}"; do
  [ -n "${extra[$mode]:-}" ] || { echo "unknown mode: $mode" >&2; exit 2; }
  out="$outroot/$mode"
  log="$outroot/$mode.publish.log"

  echo
  echo "=============================================================="
  echo "== $mode   (rid=$rid)"
  echo "=============================================================="

  rm -rf "$out"
  mkdir -p "$outroot"

  # shellcheck disable=SC2086
  dotnet publish "$proj" -c Release -r "$rid" -o "$out" ${extra[$mode]} >"$log" 2>&1
  pub=$?

  if [ -f "$log" ]; then
    total=$(grep -c "analysis warning IL" "$log" || true)
    # ILLink reports every warning against this project even when the offending
    # line lives in a referenced library, so the two counts are reported
    # separately: the Ceras number is the one that matters.
    inceras=$(grep "analysis warning IL" "$log" | grep -c "/src/Ceras/" || true)
    aotwarn=$(grep -c "AOT analysis warning" "$log" || true)
    echo "publish exit=$pub   analysis warnings: $total total, $inceras in src/Ceras, $aotwarn AOT"
    echo "publish log: $log"
  fi

  if [ $pub -ne 0 ]; then
    echo "PUBLISH FAILED (exit $pub) -- last 20 lines:"
    tail -20 "$log"
    result[$mode]="publish-failed"
    overall=1
    continue
  fi

  exe="$out/TrimAotSmoke"
  [ -x "$exe" ] || exe="$out/TrimAotSmoke.dll"
  if [ ! -e "$exe" ]; then
    echo "NO RUNNABLE OUTPUT in $out"
    result[$mode]="no-output"
    overall=1
    continue
  fi

  echo "--- run: $exe"
  if [ "${exe##*.}" = "dll" ]; then
    dotnet "$exe" 2>&1 | tee "$outroot/$mode.run.log"
  else
    "$exe" 2>&1 | tee "$outroot/$mode.run.log"
  fi
  run=${PIPESTATUS[0]}
  echo "--- run exit=$run   (log: $outroot/$mode.run.log)"
  if [ $run -ne 0 ]; then overall=1; fi
  result[$mode]="$run"
done

echo
echo "=============================================================="
echo "== summary"
echo "=============================================================="
for mode in "${modes[@]}"; do
  printf '  %-5s -> %s\n' "$mode" "${result[$mode]:-not-run}"
done
echo
echo "0 = every selected mode passed. Any non-zero = see the run log above."
exit $overall

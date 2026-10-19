#!/usr/bin/env bash
set -euo pipefail

output_name=$1
shift
# Assemble on the container filesystem, then copy the finished output to the host.
# Arguments are WiX preprocessor definitions; installer behavior lives in .wxs.
cp -r /input/payload /tmp/payload
wix build Package.wxs Components.wxs Client.wxs \
    -d 'PayloadDir=Z:\tmp\payload' -arch x64 -ext WixToolset.Util.wixext -intermediateFolder 'Z:\tmp\wix' \
    -o 'Z:\tmp\package.msi' "$@"
cp /tmp/package.msi "/out/$output_name.msi"
cp /tmp/package.wixpdb "/out/$output_name.wixpdb"

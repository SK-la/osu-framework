#!/bin/bash

# ensure we're running from the correct directory (location of this file).
cd "$(dirname "$0")"

set -euo pipefail

curl -Lso bassasio.zip https://www.un4seen.com/stuff/bassasio.zip
unzip -qjo bassasio.zip x64/bassasio.dll -d runtimes/win-x64/native/
unzip -qjo bassasio.zip bassasio.dll -d runtimes/win-x86/native/

rm bassasio.zip

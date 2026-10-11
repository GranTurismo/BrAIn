#!/bin/bash
# Double-click: run BrAIn exactly like `cd BrAIn && dotnet run` (output saved to scripts/run-dev.log).
cd "$(dirname "$0")/.." || exit 1
exec > >(tee scripts/run-dev.log) 2>&1
pkill -x BrAIn 2>/dev/null
dotnet run

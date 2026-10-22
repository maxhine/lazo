#!/bin/bash
# render-outfits.sh — compile and run the Nudo outfit planche renderer
# NOT in CI. Run manually: bash scripts/render-outfits.sh
# Output: /tmp/lazo-outfits.png

set -e
cd "$(dirname "$0")/.."

SDK=$(xcrun --sdk macosx --show-sdk-path)

echo "Compiling renderer..."
swiftc \
  -parse-as-library \
  -sdk "$SDK" \
  -target arm64-apple-macosx15.0 \
  NotchBuddy/Sources/LazoKit/IslandScreenGeometry.swift \
  NotchBuddy/Sources/LazoKit/IslandTypes.swift \
  NotchBuddy/Sources/LazoKit/NudoWardrobe.swift \
  NotchBuddy/Sources/LazoKit/BotEngine.swift \
  NotchBuddy/Sources/LazoKit/NudoOutfitDrawing.swift \
  scripts/RenderOutfits.swift \
  -framework AppKit \
  -framework SwiftUI \
  -o /tmp/lazo-render-outfits \
  2>&1

echo "Running renderer..."
/tmp/lazo-render-outfits
echo "Opening..."
open /tmp/lazo-outfits.png

#!/bin/zsh
# Compila Lazo para macOS (Apple Silicon) y crea dist/Lazo-<versión>-mac.dmg.
# Intermedios en ~/Library/Caches/lazo-build (fuera de OneDrive). Firma ad hoc: sin Developer ID,
# así que en otros Mac habrá que abrirlo con clic derecho › Abrir la primera vez.
set -e
ROOT=${0:A:h:h}
MAC="$ROOT/mac"
BUILD="$HOME/Library/Caches/lazo-build"
VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$MAC/Resources/Info.plist")
swift build --package-path "$MAC" --scratch-path "$BUILD" -c release --arch arm64 --product Lazo
STAGE="$BUILD/stage"; rm -rf "$STAGE"; mkdir -p "$STAGE"
APP="$STAGE/Lazo.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BUILD/arm64-apple-macosx/release/Lazo" "$APP/Contents/MacOS/Lazo"
cp "$MAC/Resources/Info.plist" "$APP/Contents/Info.plist"
swift "$MAC/scripts/make-icon.swift" "$BUILD/Lazo.iconset"
iconutil -c icns "$BUILD/Lazo.iconset" -o "$APP/Contents/Resources/Lazo.icns"
codesign --force --sign - --options runtime --identifier com.jhonandrew.lazo "$APP"
codesign --verify --strict "$APP"
ln -s /Applications "$STAGE/Aplicaciones"
mkdir -p "$ROOT/dist"
DMG="$ROOT/dist/Lazo-$VERSION-mac.dmg"
rm -f "$DMG"
hdiutil create -volname "Lazo $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
echo "Instalador: $DMG"

#!/bin/zsh
# Interoperabilidad Windows ↔ Mac en un solo Mac: el motor de red de Windows (C#, .NET) contra el de macOS (Swift).
# Requiere: dotnet (brew install dotnet) y Swift. Uso: scripts/test-interop.sh
set -e
ROOT=${0:A:h:h}
BUILD="$HOME/Library/Caches/lazo-build"
swift build --package-path "$ROOT/mac" --scratch-path "$BUILD" --product lazo-interop -q
dotnet build "$ROOT/tests/interop/WinPeer.csproj" -nologo -v q -o "$BUILD/winpeer" >/dev/null
MAC="$BUILD/debug/lazo-interop"; WIN="$BUILD/winpeer/WinPeer.dll"
D=$(mktemp -d /tmp/lazo-interop.XXXX); IP=$(ipconfig getifaddr en0 || ipconfig getifaddr en1)
head -c 5000017 /dev/urandom > $D/desde-windows.bin; head -c 4000003 /dev/urandom > $D/desde-mac.bin
"$MAC" serve $D/mac $D/mac-in 48551 48552 > $D/mac.log 2>&1 & MP=$!; sleep 2
read MID MFP <<< "$(grep LISTO $D/mac.log | cut -d' ' -f2,3)"
dotnet "$WIN" send $D/win $IP 48552 $MID $MFP $D/desde-windows.bin "hola desde Windows ñ ✓"
sleep 1; kill $MP
cmp -s $D/desde-windows.bin $D/mac-in/desde-windows.bin && echo "OK    Windows → Mac: archivo idéntico" || { echo "FALLO Windows → Mac"; exit 1; }
grep -q "CHAT .*hola desde Windows ñ ✓" $D/mac.log && echo "OK    Windows → Mac: chat" || { echo "FALLO chat Windows → Mac"; exit 1; }
dotnet "$WIN" serve $D/win $D/win-in 48561 48562 15 > $D/win.log 2>&1 & WP=$!; sleep 3
read WID WFP <<< "$(grep LISTO $D/win.log | cut -d' ' -f2,3)"
"$MAC" send $D/mac $IP 48562 $WID $WFP $D/desde-mac.bin "hola desde el Mac"
sleep 1; kill $WP 2>/dev/null || true
cmp -s $D/desde-mac.bin $D/win-in/desde-mac.bin && echo "OK    Mac → Windows: archivo idéntico" || { echo "FALLO Mac → Windows"; exit 1; }
grep -q "CHAT .*hola desde el Mac" $D/win.log && echo "OK    Mac → Windows: chat" || { echo "FALLO chat Mac → Windows"; exit 1; }
rm -rf $D

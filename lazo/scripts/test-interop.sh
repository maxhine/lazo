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
grep -q "^UNIRSE " $D/mac.log && grep -q "^COMPARTIR " $D/mac.log && echo "OK    Windows → Mac: el Mac recibió unirse y compartir" || { echo "FALLO peticiones Windows → Mac"; exit 1; }
dotnet "$WIN" serve $D/win $D/win-in 48561 48562 15 > $D/win.log 2>&1 & WP=$!; sleep 3
read WID WFP <<< "$(grep LISTO $D/win.log | cut -d' ' -f2,3)"
"$MAC" send $D/mac $IP 48562 $WID $WFP $D/desde-mac.bin "hola desde el Mac"
sleep 1; kill $WP 2>/dev/null || true
cmp -s $D/desde-mac.bin $D/win-in/desde-mac.bin && echo "OK    Mac → Windows: archivo idéntico" || { echo "FALLO Mac → Windows"; exit 1; }
grep -q "CHAT .*hola desde el Mac" $D/win.log && echo "OK    Mac → Windows: chat" || { echo "FALLO chat Mac → Windows"; exit 1; }
grep -q "^UNIRSE " $D/win.log && grep -q "^COMPARTIR " $D/win.log && grep -q "^SENAL 5 a1b2c3d4" $D/win.log && echo "OK    Mac → Windows: Windows recibió unirse, compartir y borrado" || { echo "FALLO peticiones Mac → Windows"; exit 1; }
# Formatos del chat de 0.5.3 (identificador de mensaje escondido y lista de grupo) en los dos sentidos.
swift build --package-path "$ROOT/mac" --scratch-path "$BUILD" -c release --arch arm64 --product Lazo -q
APP="$BUILD/arm64-apple-macosx/release/Lazo"
VEC=$(dotnet "$WIN" vectors)
OUT=$("$APP" --chat-vectors "$(echo "$VEC" | grep ^WIRE | cut -d' ' -f2)" "$(echo "$VEC" | grep ^GROUP | cut -d' ' -f2)") || { echo "$OUT"; exit 1; }
echo "$OUT" | grep -E "^(OK|FALLO)"
dotnet "$WIN" vectors "$(echo "$OUT" | grep ^WIRE | cut -d' ' -f2)" "$(echo "$OUT" | grep ^GROUP | cut -d' ' -f2)"
# Control remoto con contraseña (docs/control-remoto.md): visor y anfitrión de cada plataforma contra la otra.
export LAZO_REMOTE_PASSWORD="claveSegura-123"
"$MAC" serve $D/mac2 $D/mac2-in 48571 48572 > $D/mac2.log 2>&1 & MP2=$!; sleep 2
read MID2 MFP2 <<< "$(grep LISTO $D/mac2.log | cut -d' ' -f2,3)"
dotnet "$WIN" remote $D/winv $IP 48572 $MID2 $MFP2 "claveSegura-123" || { kill $MP2; exit 1; }
dotnet "$WIN" remote $D/winv $IP 48572 $MID2 $MFP2 "incorrecta-999" falla || { kill $MP2; exit 1; }
sleep 1; kill $MP2 2>/dev/null || true
grep -q "^REMOTO " $D/mac2.log && echo "OK    Mac como anfitrión: recibió la sesión remota de Windows" || { echo "FALLO el Mac no recibió la sesión remota"; exit 1; }
dotnet "$WIN" serve $D/win2 $D/win2-in 48581 48582 12 > $D/win2.log 2>&1 & WP2=$!; sleep 3
read WID2 WFP2 <<< "$(grep LISTO $D/win2.log | cut -d' ' -f2,3)"
"$MAC" remote $D/macv $IP 48582 $WID2 $WFP2 "claveSegura-123" || { kill $WP2; exit 1; }
"$MAC" remote $D/macv $IP 48582 $WID2 $WFP2 "incorrecta-999" falla || { kill $WP2; exit 1; }
sleep 1; kill $WP2 2>/dev/null || true
grep -q "^REMOTO " $D/win2.log && echo "OK    Windows como anfitrión: recibió la sesión remota del Mac" || { echo "FALLO Windows no recibió la sesión remota"; exit 1; }
unset LAZO_REMOTE_PASSWORD
rm -rf $D

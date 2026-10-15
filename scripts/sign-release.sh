#!/bin/sh
# Firma los instaladores de una versión para que el actualizador de Lazo los acepte.
# Uso: scripts/sign-release.sh dist/Lazo-Setup-0.5.1.exe dist/Lazo-0.5.0-mac.dmg
# La clave privada vive solo en el equipo que publica: ~/.lazo/release-signing-key.pem
# (nunca en el repositorio ni en OneDrive). Publica cada archivo junto con su «.sig».
set -e
KEY="${LAZO_SIGNING_KEY:-$HOME/.lazo/release-signing-key.pem}"
[ -f "$KEY" ] || { echo "No se encontró la clave de firma en $KEY" >&2; exit 1; }
for file in "$@"; do
  openssl dgst -sha256 -sign "$KEY" "$file" | base64 | tr -d '\n' > "$file.sig"
  echo "Firmado: $file.sig"
done

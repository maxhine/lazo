# Motor de Lazo para la isla (protocolo 1)

El motor de Lazo en Windows es Rust y vive dentro de la isla (`windows/src-tauri/src/lazo_net`): descubrimiento, canal cifrado LAZO3, archivos y chat, con el mismo protocolo de red que el Lazo clásico y el Mac (`lazo/docs/protocolo-seguro.md`). Comparte con el clásico los archivos de `%APPDATA%\Lazo` (`device.id`, `identity.key`, `paired.txt`, `name.txt`, `receive-folder.txt`), así que un equipo que tenía Lazo clásico no tiene que emparejarse otra vez.

La página y el motor hablan con mensajes JSON, un texto por mensaje. En la isla viajan por el canal de Tauri: el motor los emite como el evento `lazo-engine` y la página manda órdenes con el comando `lazo_engine_send`. Mientras el motor no está (por ejemplo, con el Lazo clásico abierto, que usa los mismos puertos), el comando responde con el motivo y el cliente (`windows/src/lazo/engine.ts`) lo vuelve a intentar.

`engine/Lazo.Engine.exe` es el motor del clásico sin ventanas, con los mismos mensajes sobre un WebSocket local con token. Ya no se lanza desde la isla: queda como referencia para comprobar que el motor Rust y el C# se entienden (ver Pruebas).

## Argumentos del motor C# (referencia)

`--info <archivo>` · `--port <n>` · `--profile <carpeta>` · `--receive <carpeta>` · `--ports <udp>,<tcp>` y `--allow-parallel` (pruebas) · `--exit-on-stdin-close` (termina cuando quien lo lanzó cierra su entrada).

## Motor → isla

| `type` | Campos |
|---|---|
| `hello` | `version`, `selfId`, `name`, `fingerprint`, `receiveDir` |
| `peers` | `peers[]`: `id`, `name`, `address`, `paired`, `verified`, `identityChanged`, `sharing`, `hasPhoto` |
| `offer` | `id`, `peerId`, `sender`, `fileName`, `size`, `preview` (JPEG en base64 o `""`) |
| `receiveProgress` | `id`, `value` (0…1) |
| `receiveFinished` | `id`, `message`, `path` (vacío si falló) |
| `sendProgress` | `token`, `value` |
| `sendDone` | `token`, `peerId`, `ok`, `message` |
| `chat` | `peerId`, `name`, `text` |
| `chatSent` | `peerId`, `ok`, `message` |
| `photo` | `peerId`, `data` (base64) |
| `pong`, `error` | `message` |

## Isla → motor

| `cmd` | Campos |
|---|---|
| `ping`, `state` | — |
| `send` | `token` (lo elige la isla), `peerId`, `path` |
| `chat` | `peerId`, `text` |
| `offerDecision` | `id`, `accept` |
| `photo` | `peerId` |
| `addPeer` | solo con `--allow-parallel`, para pruebas |

## Pruebas

- `cargo test -p lazo lazo_net` (en `windows/`): las piezas del protocolo (HKDF contra el vector de la RFC 5869, firmas, registros cifrados alterados o repetidos, identidad guardada con DPAPI, `paired.txt`), reglas de nombres y archivos, anuncios de descubrimiento, dos motores Rust que chatean y se pasan archivos (aceptar, rechazar, mismo nombre, marca de descarga de Windows) y **el motor Rust contra el C# real** (apretón de manos, chat y archivos en los dos sentidos, con la misma clave guardada en `paired.txt`). La prueba contra C# necesita `engine/bin/Lazo.Engine.exe` (`engine/build.ps1`); sin él se omite.
- `node engine/test-engine.mjs`: lo mismo para el motor C#, por su WebSocket.

La difusión UDP entre dos copias de un mismo equipo no se puede probar (el anuncio exige el mismo puerto de transferencia), así que un motor registra al otro con `addPeer`, que solo existe en modo de pruebas.

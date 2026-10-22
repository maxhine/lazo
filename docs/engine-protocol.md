# Motor de Lazo para la isla (protocolo 1)

`engine/Lazo.Engine.exe` es el motor del Lazo clásico sin ventanas: compila tal cual los archivos de `lazo/src` que no dependen de WPF (red, cifrado LAZO3, chat, nubes de puntos, Xpace). La isla lo lanza y habla con él por un WebSocket local con mensajes JSON, una línea de texto por mensaje.

## Seguridad

- Solo escucha en `127.0.0.1`. Puerto: el primero libre desde 48360.
- Exige el token de la sesión en `?t=` (24 bytes aleatorios en hex), que el motor escribe en `engine.json` (`%APPDATA%\Lazo\engine.json`: `version`, `port`, `token`, `pid`). Lo borra al salir.
- Si la petición trae `Origin`, debe ser el de la isla (`tauri://localhost`, `http(s)://tauri.localhost` o el servidor de desarrollo en el puerto 1420); otro origen recibe 403. Un sitio web abierto en el navegador no puede hablar con el motor aunque conozca el puerto.
- Mismo cerrojo que el Lazo clásico: los dos usan los puertos 48351/48352, así que solo uno puede estar abierto.
- Sin una isla conectada, las ofertas de archivo se rechazan; con una, esperan 60 s su decisión.

## Argumentos

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

`node engine/test-engine.mjs` compila el motor y comprueba: rechazo sin token y con un origen ajeno, saludo y lista de personas, errores, y dos motores en este equipo que se encuentran, chatean y transfieren 3 MB cifrados (aceptar guarda el archivo idéntico, rechazar no guarda nada), más el cliente TypeScript (`windows/src/lazo/engine.ts`) contra el motor real. La difusión UDP entre dos copias de un mismo equipo no se puede probar (el anuncio exige el mismo puerto de transferencia), así que un motor descubre al otro por un anuncio unicast y el otro lo registra con `addPeer`.

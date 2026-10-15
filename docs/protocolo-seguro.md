# Protocolo seguro LAZO3

Contrato común de Lazo para Windows y macOS desde la versión 0.5.0. Sustituye a `LAZO1`, `LAZO2`, `LAZOA`, `LAZOC`, `LAZOD` y `LAZOS` en claro: un equipo 0.5 no habla con uno 0.4.x.

Todos los enteros van en *little endian*, salvo donde se indique. `texto` es `u16` con la longitud en bytes seguida de UTF‑8.

## Identidad

- Cada equipo tiene un identificador (`device.id`, GUID en formato `D`, minúsculas, 36 caracteres ASCII) y una clave de identidad ECDSA P‑256 que no sale del equipo.
  - Windows: `%AppData%\Lazo\identity.key`, protegida con DPAPI del usuario.
  - macOS: `~/Library/Application Support/Lazo/identity.key`, permisos `0600` (como las claves de SSH).
- Clave pública: 64 bytes `X || Y` (big endian, sin el prefijo `0x04`).
- Huella: SHA‑256 de la clave pública, en hexadecimal minúscula.
- Equipos conocidos: identificador → huella, nombre y si está verificado, en `paired.txt` (Windows, `%AppData%\Lazo`) o `paired.json` (macOS, `~/Library/Application Support/Lazo`). Se guardan solos en el primer contacto (confianza en el primer uso, como SSH). Verificar con el código de 6 cifras es opcional.

## Descubrimiento (UDP 48351, difusión)

```
LAZO3|<id>|<nombre>|<puerto TCP>|<huella>
LAZO3A|<id>|<foto JPEG en base64, ≤ 12 000 bytes>
LAZO3P|<id>|<qué comparte, ≤ 120>      (vacío: dejó de compartir)
```

Cada 2,5 s por cada dirección de difusión privada. Se descarta todo lo que no venga de la subred IPv4 privada local. El anuncio no está firmado: solo sirve para listar equipos. La identidad se comprueba al conectar. Si la huella anunciada de un equipo conocido no coincide con la guardada, se muestra como **identidad cambiada** y no se le puede enviar nada.

## Conexión (TCP 48352)

Quien conecta es el *cliente*.

1. Cliente → servidor: `"LAZO3"` · `u8 versión = 1` · `e_c` (64, clave efímera ECDH P‑256) · `n_c` (32 aleatorios).
2. Servidor → cliente: `u8 versión` · `e_s` (64) · `n_s` (32) · `id_s` (36 ASCII) · `k_s` (64, clave de identidad) · `firma_s` (64).
   - `th1 = SHA256("LAZO3/1" || e_c || n_c || e_s || n_s || id_s || k_s)`
   - `firma_s` = ECDSA P‑256 sobre el resumen `th1`, en formato `r || s` (32 + 32, big endian).
3. Cliente → servidor: `id_c` (36) · `k_c` (64) · `firma_c` (64).
   - `th2 = SHA256(th1 || id_c || k_c)`; `firma_c` sobre `th2`.
4. Cada lado comprueba la firma del otro. Si falla, cierra.
5. Claves: `Z` = secreto ECDH (coordenada X, 32 bytes); `ikm = SHA256(Z)`.
   `HKDF-SHA256(salt = th2, ikm, info = "lazo3 keys", 128 bytes)` = `enc_cs` · `mac_cs` · `enc_sc` · `mac_sc` (32 cada una; *cs* = cliente → servidor).

Tiempo máximo del saludo: 10 s.

### Registros cifrados

Todo lo que sigue va en registros:

```
u32 longitud del cifrado (múltiplo de 16, ≤ 65 552)
16 bytes IV aleatorio
cifrado AES‑256‑CBC con relleno PKCS#7 (texto ≤ 65 536 bytes)
32 bytes HMAC‑SHA256(mac, u64 secuencia || u32 longitud || IV || cifrado)
```

La secuencia empieza en 0 en cada sentido y sube en 1 por registro. Se comprueba la etiqueta (comparación en tiempo constante) antes de descifrar; cualquier error cierra la conexión.

### Identidad conocida

Primer registro, servidor → cliente: `u8` estado.

- `1`: identidad aceptada. Si el servidor no conocía al cliente, puede haber guardado su clave (ver abajo).
- `2`: el servidor tiene ese identificador con otra clave, o el cliente usa el identificador de un equipo que se anuncia con otra huella. Cierra.

Primer contacto en el servidor (Windows desde 0.5.0): se contrasta con el último anuncio UDP de ese identificador.

- Huella anunciada distinta de la del saludo: suplantación, estado `2`.
- Misma huella y la conexión llega desde la dirección del anuncio: se guarda la clave.
- Sin anuncio, o desde otra dirección: se atiende la operación sin guardar la clave.

Así nadie puede adelantarse con el identificador de un compañero (que se ve en los anuncios) y quedar guardado en su lugar.

El cliente aplica la misma regla: si conocía al servidor y la clave es otra, cierra sin enviar nada; si no lo conocía, la clave del saludo debe coincidir con la huella anunciada, y solo entonces se guarda. Ninguna operación pide código.

## Operaciones (cliente → servidor, dentro del canal)

| Byte | Operación | Cuerpo | Respuesta |
|---|---|---|---|
| `P` | Verificar (opcional) | `texto` nombre (≤ 80) | Ver abajo; la conexión termina después |
| `F` | Archivo | `texto` nombre de quien envía (≤ 80) · `texto` nombre del archivo (≤ 255) · `i64` tamaño · `i32` previo · previo JPEG (≤ 48 000) | `u8` 1 acepta / 0 rechaza; si acepta: contenido, SHA‑256 (32), `u8` 1 guardado |
| `C` | Chat | `texto` nombre · `texto` mensaje (≤ 4 000 bytes, ≤ 2 000 caracteres) | `u8` 1 |
| `D` | Señal | `texto` nombre · `u8` tipo (2 escribiendo, 3 presencia, 4 zumbido) · `texto` cuerpo | `u8` 1 |
| `S` | Pantalla | `texto` nombre · `texto` origen (≤ 400) | `u8` 1 acepta / 0; si acepta, sigue el flujo de pantalla |
| `J` | Pedir unirse (posterior a 0.5.0) | `texto` nombre (≤ 80) | `u8` 1 se atiende (está compartiendo) / 0. Si quien comparte acepta, envía después una operación `S` normal |

`LAZO3P` se anuncia junto con los demás mientras se comparte, y una vez vacío al dejar de hacerlo; quien deja de recibirlo 8 s retira a ese equipo de «Compartiendo ahora». Solo sirve para listar: unirse pasa por `J`, autenticado, y lo decide quien comparte. Los equipos 0.5.0 lo ignoran; si reciben `J`, cierran sin responder.

Una conexión lleva una sola operación.

### Verificar (`P`, opcional)

Los dos lados calculan el código `SAS = (u32 big endian de SHA256("LAZO3 SAS" || th2)[0..4]) mod 1 000 000`, con 6 cifras. Cada persona ve el mismo código y elige **Coincide** o **Cancelar** en un máximo de 90 s. Cada lado envía su decisión (`u8` 1/0) en cuanto la tiene y lee la del otro. Solo si las dos son 1 el equipo queda marcado como **verificado** en los dos lados. Sirve para descartar que alguien se interpusiera en la red durante el primer contacto. El servidor rechaza (`0`) si ya hay una petición abierta de ese identificador o más de 3 en 5 minutos.

## Pantalla compartida

Tras `S` aceptado, quien comparte (cliente) envía:

- `S` `i32` ancho `i32` alto
- `F` `i32` cursorX `i32` cursorY `i32` n, y n bloques `u16` x `u16` y `u16` w `u16` h `i32` longitud + JPEG
- `K` (sigue viva) · `E` (fin)
- `H` `u8` versión · `P` `u8` permisos · `D` `u8` permiso rechazado · `A` `u16` longitud + trazo

Quien mira envía: `H` `u8` versión · `R` `u8` petición (con `0x80` = renunciar) · `A` `u16` longitud + trazo · `I` evento de entrada · `M` puntero (versión ≥ 2).

Versiones del canal de retorno: `1` (0.5.0) y `2` (punteros colaborativos). Los paquetes `M` solo se cruzan si los dos lados saludaron con `2` o más; un lado de versión 1 nunca los recibe.

- Quien mira → quien comparte: `M` `u8` visible · `u16` x · `u16` y (normalizadas). Solo mientras dibuja o controla; sin esos permisos se ignora.
- Quien comparte → los demás que miran: `M` `u16` longitud + [`i32` id del visor · `u8` visible · `u16` x · `u16` y · `u8` color · nombre UTF‑8 (≤ 180 bytes)].

Permisos: `1` dibujar · `2` control · `4` control total del equipo (solo junto con `2`; sin él, **Ventana activa**).

Evento `I`: `u8` tipo; `1` mover `u16 x` `u16 y`; `2`/`3` botón abajo/arriba `u8` botón (1 izq, 2 der, 3 centro) `u16 x` `u16 y`; `4` rueda `i16` delta `u16 x` `u16 y`; `5`/`6` tecla abajo/arriba `u16` código virtual de Windows. Coordenadas normalizadas 0–65535 sobre lo compartido. En macOS, ⌘ y ⌃ se envían como `VK_CONTROL` y ⌥ como `VK_MENU`; al recibir, `VK_CONTROL` se reproduce como ⌘.

Trazos: `u8` tipo (1 empieza, 2 puntos, 3 borra, 4 borra todo) · `i32` id · [empieza: `u8` herramienta (1 lápiz, 2 marcador, 3 flecha, 4 recuadro) · `u32` color ARGB] · [empieza/puntos: `u16` n · n × (`u16` x · `u16` y)].

### Modos de control

- **Ventana activa** (predeterminado): solo la ventana compartida o, si se comparte una pantalla, la ventana que estaba al frente al conceder el control. Los clics fuera de esa aplicación y las teclas con otra aplicación al frente se descartan. Se bloquean las teclas Windows (`VK_LWIN`, `VK_RWIN`), `Alt+Tab`, `Alt+Esc`, `Ctrl+Esc` y `Ctrl+Mayús+Esc`; en un Mac que comparte, además `⌘Tab`, `⌘Espacio` y `⌃` con flechas.
- **Control total del equipo**: mouse dentro de lo compartido y teclado sin restricciones.

En los dos modos la entrada inyectada no activa el atajo de Lazo (doble Alt / doble ⌥).

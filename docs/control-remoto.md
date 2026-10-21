# Control remoto con contraseña y ajuste de resolución (Lazo 0.6.0)

Dos funciones nuevas sobre el canal cifrado LAZO3 (`protocolo-seguro.md`). Este documento es la especificación común de Windows (C#) y macOS (Swift).

## 1. Control remoto con contraseña

Una persona entra al equipo destino (el «anfitrión», por ejemplo ATLAS) desde otro equipo (el «visor», por ejemplo el Mac mini) sin que nadie acepte en el anfitrión. Lo autoriza una contraseña que el dueño del anfitrión define en **Ajustes › Control remoto**.

### Anfitrión

- Ajuste «Permitir control remoto con contraseña» (apagado por defecto) y la contraseña (mínimo 8 caracteres).
- Se guarda **solo un hash**: `PBKDF2-HMAC-SHA256`, sal de 16 bytes aleatorios, 150 000 iteraciones, 32 bytes. Archivo `remote.txt` en el perfil de Lazo (`%APPDATA%\\Lazo` / `~/Library/Application Support/Lazo`), una línea: `v1|<iteraciones>|<sal en base64>|<hash en base64>|<activo: 1 o 0>`.
- Bloqueo: 5 contraseñas erróneas del mismo equipo en 10 minutos bloquean los intentos de ese equipo durante 10 minutos. Un acierto reinicia la cuenta.
- Mientras hay una sesión remota activa, el anfitrión muestra un aviso permanente («Control remoto activo: <nombre>») con un botón **Terminar**, y los atajos de emergencia (Ctrl/⌃+Alt/⌥+X deja de compartir, +Q quita permisos) siguen funcionando. Al conectar avisa con una notificación.
- Una sola sesión remota a la vez; si ya hay una, la nueva se rechaza (estado 0).
- La sesión comparte la **pantalla principal** y concede al visor, sin preguntar, `Dibujar | Control | Control total` (`P` = `0x07`; ver `SharePermission`).

### Mensajes (después del saludo LAZO3, igual que `F`, `C`, `D`, `S`…)

Operación nueva, byte `'W'` (0x57).

```
visor  → anfitrión : 'W' · text(nombre del visor, ≤ 80)
anfitrión → visor  : u8 estado
                       0 = desactivado, bloqueado u ocupado (se cierra)
                       1 = reto a continuación
si estado == 1:
anfitrión → visor  : 16 bytes sal · i32 iteraciones (little endian) · 16 bytes nonce aleatorio
visor  → anfitrión : 32 bytes prueba
anfitrión → visor  : u8 resultado  (1 = correcto, 0 = contraseña incorrecta; tras 0 se cierra)
```

- `text` es el mismo formato de siempre (`WriteText`: longitud + UTF-8).
- Prueba: `HMAC-SHA256(clave = PBKDF2-HMAC-SHA256(contraseña UTF-8, sal, iteraciones, 32 bytes), mensaje = nonce ‖ UTF-8("<id visor>|<id anfitrión>"))`, con los identificadores en minúscula y con guiones (`xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`), tal como los conoce cada lado por el saludo cifrado. Atar los identificadores impide reutilizar la prueba con otro equipo.
- El anfitrión compara en tiempo constante y espera 1 s antes de contestar un fallo.
- El visor rechaza `iteraciones` fuera de 50 000…1 000 000.
- Tras `resultado == 1` **la misma conexión pasa a ser la transmisión de pantalla** con el anfitrión como emisor y el visor como receptor: exactamente el flujo de `S`/`F`/`K`/`E`/`H`/`P`/`D`/`A`/`M` del documento de pantalla compartida (el visor envía su saludo `H` con su versión del canal de retorno y el emisor responde, como siempre). La diferencia es que el emisor ya trata a ese visor como `Granted = 0x07` y no pide permiso.
- Versiones antiguas del anfitrión no conocen `'W'`: cierran la conexión; el visor lo muestra como «Este equipo tiene una versión de Lazo sin control remoto».

### Visor

- Entrada **Control remoto** (Windows: tarjeta en Utilidades o botón en la barra; Mac: botón en la cabecera de Lazo): elegir equipo y escribir la contraseña. No se guarda.
- Al conectar se abre la ventana de pantalla compartida (en Mac, un `ViewerPane` en `StageWindow`) ya en **modo control** y con «Ajustar resolución» disponible.

## 2. Ajuste de resolución (canal de retorno versión 3)

`BackChannelVersion` pasa de 2 a 3. Quien mira puede pedir que la pantalla compartida **se ajuste al tamaño de su ventana**, como el escritorio remoto.

```
visor → emisor : 'Z' · u16 ancho · u16 alto   (píxeles que quiere ver, little endian; 0,0 = restaurar)
```

- Solo se envía si el emisor saludó con versión ≥ 3 y el visor tiene el permiso de control.
- Emisor Windows: elige, entre los modos de pantalla que ofrece el monitor compartido, el de **mayor área que no supere lo pedido** y cuya proporción se parezca más a la pedida (tolerancia 6 %); si no hay ninguno, el más cercano por arriba. Lo aplica con `ChangeDisplaySettingsEx(..., CDS_FULLSCREEN)`: el cambio es temporal y Windows lo revierte si Lazo se cierra. Guarda el modo original y lo restaura cuando: llega `Z 0 0`, el visor suelta el control, el visor se va, o la transmisión termina. Un cambio de modo hace que el emisor envíe `S` nuevo con el tamaño nuevo (la captura ya reacciona a los cambios de tamaño).
- Emisor Mac: ignora `Z` (no cambia la resolución del Mac).
- Si el visor cambia el tamaño de su ventana, repite `Z` tras 500 ms sin cambios.
- Solo se permite cambiar la pantalla que se comparte, nunca otra, y no más de un cambio por segundo.

## 3. Interfaz

- Windows, Ajustes: sección **Control remoto** (interruptor, contraseña, «Cambiar contraseña»), y el estado de la sesión activa con **Terminar**.
- Mac: lo mismo en Ajustes › Control remoto (anfitrión) y botón «Control remoto» (visor).
- Visor, durante el control: botón «Ajustar resolución» (interruptor) en la barra.
- En modo control solo se ve **un** puntero: el del sistema local del visor. El cursor remoto que viaja en los paquetes `F` se oculta mientras se controla.

## 4. Pruebas

- `tests/interop` (C# ↔ Swift): reto/prueba en los dos sentidos, contraseña errónea, bloqueo tras 5 fallos.
- `Z`: solo el emisor de Windows lo atiende; se prueba el cálculo del modo elegido con una lista de modos sintética.

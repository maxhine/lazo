# Integración de Lazo en la isla

La isla (Mac `NotchBuddy/`, Windows `windows/`) es la interfaz; Lazo es la herramienta. El motor de Lazo se reutiliza sin reescribirlo; el Lazo clásico (`lazo/`) sigue intacto.

## Hecho

- Base de interfaz en la raíz y Lazo clásico en `lazo/`. Sin nombre, personaje, íconos ni sonidos del proyecto base: personaje propio (un moño), 29 sonidos sintetizados (`NotchBuddy/Resources/sounds`) e íconos propios. Aviso MIT en `THIRD_PARTY_NOTICES.md`.
- Sin actualizador, relay, donaciones ni equipo de firma del autor original.
- Motor de red de Lazo en Rust dentro de la isla de Windows (`windows/src-tauri/src/lazo_net`, protocolo en `engine-protocol.md`), compatible con el Lazo clásico y el Mac, probado contra el motor C# real. El motor C# (`engine/`) queda como referencia de pruebas.

## Windows

- La app Tauri ejecuta el motor Rust en su mismo proceso (sin programa aparte, sin .NET): descubrimiento, canal cifrado, archivos y chat. La página lo usa por un evento y un comando de Tauri.
- Pill **Lazo** (`core/lazo.ts`, `island/lazo.ts`, `views/lazo.ts`), activa por defecto: personas en la red, ofertas de archivo con aceptar/rechazar y progreso, y envío (suelta un archivo en la isla con la pill de Lazo al frente y elige a quién). Un archivo ofrecido abre la isla.
- Falta, en este orden: verificación con código, chat en la isla (el motor ya envía y recibe mensajes y señales), pantalla compartida y control, Xpace, conversor de nubes de puntos y Descanso Visual. Hoy viven en WPF (`ScreenShare`, `XpaceView`, `EyeCare`…); se portan al motor Rust y la vista a la isla, una a una.
- El Lazo clásico y la isla no pueden estar abiertos a la vez (mismo cerrojo y mismos puertos): la tarjeta lo dice.

## Dos núcleos, y las herramientas dentro de la isla

Quien use el núcleo nuevo lo usa todo dentro de la isla, que crece al tamaño de cada herramienta (`VIEW_LAYOUTS[view].width/height`; la ventana, transparente, mide 1000×640). Quien prefiera el viejo lo elige en los ajustes de la isla («Use classic Lazo»): se anota en `%APPDATA%Lazocore.txt`, la isla se cierra y se abre el Lazo clásico. Falta el camino de vuelta desde el clásico.

La lógica de las herramientas es la del Lazo clásico, sin reescribirla: `engine/Lazo.Engine.exe --tools-only` (sin red) compila `XpaceScanner`, `XpaceActions`… tal cual y las sirve con órdenes `herramienta.orden` por el mismo JSON de `engine-protocol.md`. La isla lo arranca al primer uso (`windows/src-tauri/src/lazo_tools.rs`, que retransmite los mensajes como el evento `lazo-tools`) y dibuja la interfaz con TypeScript.

- **Xpace**: dentro de la isla (mapa de rectángulos, lista, archivos grandes, tipos y qué limpiar, con papelera o borrado definitivo con confirmación). Pruebas: `node engine/test-tools.mjs`, `cargo test -p lazo lazo_tools` (contra el programa real) y `windows/tests/xpace.test.mjs`.
- **Nubes de puntos y Descanso Visual**: por ahora se abren en la ventana del Lazo clásico (`Lazo.exe --tool=cloud|eyecare`, modo aditivo en `lazo/src`), sin red ni bandeja. Siguen funcionando como siempre; el siguiente paso es dibujarlas dentro de la isla sobre la misma lógica.
- **Pantalla compartida y control remoto**: dependen de la red; pendientes.

## Siguiente (Mac)

El motor de Mac ya existe como Swift (`lazo/mac/Sources/LazoCore`, interoperable con el de C#). Se integra en `NotchBuddy/` como un módulo más, con los mismos pills. Requiere Xcode (no hay en el Mac de desarrollo todavía).

## Pendientes de la base

- Swift no se ha compilado en esta rama. En Windows se comprobaron TypeScript (tipos, 461 pruebas, build de Vite), Rust (`cargo check` y pruebas) y el motor C#.
- Firma propia: `DEVELOPMENT_TEAM`, perfil de aprovisionamiento y `MacUpdate.teamId`; canal de actualizaciones propio.
- El vestuario del personaje (sombreros, bufanda) se dibujó para el cuerpo redondo; con el moño hay que redibujarlo o quitarlo.
- Los nombres de pantalla del personaje (`Nudo`) son provisionales.

## Probar la interfaz sin otro equipo

Con `npx vite` en `windows/` y la página abierta en un navegador, `window.__island` (solo en desarrollo) deja abrir vistas desde la consola: `__island.alert("overview")`, `__island.expand("prompt")`. Un motor simulado se monta con `LazoEngine` y un socket falso (ver `windows/tests/lazo-engine.test.mjs`), importado con `import('/src/lazo/engine.ts')` y asignado a `Lazo.engine` de `/src/core/lazo.ts`.

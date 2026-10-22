# Integración de Lazo en la isla

La isla (Mac `NotchBuddy/`, Windows `windows/`) es la interfaz; Lazo es la herramienta. El motor de Lazo se reutiliza sin reescribirlo; el Lazo clásico (`lazo/`) sigue intacto.

## Hecho

- Base de interfaz en la raíz y Lazo clásico en `lazo/`. Sin nombre, personaje, íconos ni sonidos del proyecto base: personaje propio (un moño), 29 sonidos sintetizados (`NotchBuddy/Resources/sounds`) e íconos propios. Aviso MIT en `THIRD_PARTY_NOTICES.md`.
- Sin actualizador, relay, donaciones ni equipo de firma del autor original.
- Motor de Windows sin ventanas (`engine/`, protocolo en `engine-protocol.md`) y su cliente TypeScript (`windows/src/lazo/engine.ts`), con pruebas de extremo a extremo.

## Windows

- La app Tauri arranca `Lazo.Engine.exe` (`src-tauri/src/lazo_engine.rs`) y el comando `lazo_engine_info` entrega puerto y token a la página. El motor se compila antes de `npm run dev` y `npm run build` (`scripts/build-engine.mjs`) y se empaqueta como recurso.
- Pill **Lazo** (`core/lazo.ts`, `island/lazo.ts`, `views/lazo.ts`), activa por defecto: personas en la red, ofertas de archivo con aceptar/rechazar y progreso, y envío (suelta un archivo en la isla con la pill de Lazo al frente y elige a quién). Un archivo ofrecido abre la isla.
- Falta, en este orden: chat en la isla (el motor ya lo expone), pantalla compartida y control, Xpace, conversor de nubes de puntos y Descanso Visual. Hoy viven en WPF (`ScreenShare`, `XpaceView`, `EyeCare`…); su lógica pasa al motor y la vista a la isla, una a una.
- El Lazo clásico y la isla no pueden estar abiertos a la vez (mismo cerrojo y mismos puertos): la tarjeta lo dice.

## Siguiente (Mac)

El motor de Mac ya existe como Swift (`lazo/mac/Sources/LazoCore`, interoperable con el de C#). Se integra en `NotchBuddy/` como un módulo más, con los mismos pills. Requiere Xcode (no hay en el Mac de desarrollo todavía).

## Pendientes de la base

- Swift no se ha compilado en esta rama. En Windows se comprobaron TypeScript (tipos, 461 pruebas, build de Vite), Rust (`cargo check` y pruebas) y el motor C#.
- Firma propia: `DEVELOPMENT_TEAM`, perfil de aprovisionamiento y `MacUpdate.teamId`; canal de actualizaciones propio.
- El vestuario del personaje (sombreros, bufanda) se dibujó para el cuerpo redondo; con el moño hay que redibujarlo o quitarlo.
- Los nombres de pantalla del personaje (`Nudo`) son provisionales.

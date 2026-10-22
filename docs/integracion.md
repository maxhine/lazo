# Integración de Lazo en la isla

La isla (Mac `NotchBuddy/`, Windows `windows/`) es la interfaz; Lazo es la herramienta. El motor de Lazo se reutiliza sin reescribirlo; el Lazo clásico (`lazo/`) sigue intacto.

## Hecho

- Base de interfaz en la raíz y Lazo clásico en `lazo/`. Sin nombre, personaje, íconos ni sonidos del proyecto base: personaje propio (un moño), 29 sonidos sintetizados (`NotchBuddy/Resources/sounds`) e íconos propios. Aviso MIT en `THIRD_PARTY_NOTICES.md`.
- Sin actualizador, relay, donaciones ni equipo de firma del autor original.
- Motor de Windows sin ventanas (`engine/`, protocolo en `engine-protocol.md`) y su cliente TypeScript (`windows/src/lazo/engine.ts`), con pruebas de extremo a extremo.

## Siguiente (Windows)

1. Lanzar el motor desde la app Tauri (Rust): arrancar `Lazo.Engine.exe` junto a la app con `--exit-on-stdin-close`, esperar `engine.json` y entregar puerto y token a la ventana por un comando de Tauri. Empaquetarlo como recurso del instalador.
2. Pills de Lazo en la isla: «Personas» (equipos cercanos, soltar un archivo sobre alguien), «Recibidos» (ofertas con aceptar/rechazar y progreso), «Chat».
3. Después: pantalla compartida y control, Xpace, conversor de nubes de puntos y Descanso Visual. Estas partes hoy viven en WPF (`ScreenShare`, `XpaceView`, `EyeCare`…); su lógica pasa al motor y la vista a la isla, una a una.

## Siguiente (Mac)

El motor de Mac ya existe como Swift (`lazo/mac/Sources/LazoCore`, interoperable con el de C#). Se integra en `NotchBuddy/` como un módulo más, con los mismos pills. Requiere Xcode (no hay en el Mac de desarrollo todavía).

## Pendientes de la base

- Rust y Swift no se han compilado en esta rama: solo se comprobó TypeScript (tipos, 461 pruebas, build de Vite) y el motor C#.
- Firma propia: `DEVELOPMENT_TEAM`, perfil de aprovisionamiento y `MacUpdate.teamId`; canal de actualizaciones propio.
- El vestuario del personaje (sombreros, bufanda) se dibujó para el cuerpo redondo; con el moño hay que redibujarlo o quitarlo.
- Los nombres de pantalla del personaje (`Nudo`) son provisionales.

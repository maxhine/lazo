# Lazo Isla

Rama de Lazo que integra Lazo en una interfaz de «isla» (muesca del Mac y píldora en Windows) con personaje animado, avisos y herramientas.

- **Lazo es la herramienta.** La isla aporta interfaz, animación y herramientas; Lazo aporta archivos, personas, chat, pantalla compartida y control, Xpace, conversor de nubes de puntos y Descanso Visual.
- **Lazo clásico se conserva** en [`lazo/`](lazo/) (C#/WPF en Windows, Swift en Mac) y sigue siendo la versión publicada hasta que la isla lo reemplace.

## Estructura

| Ruta | Contenido |
|---|---|
| `NotchBuddy/` | App de Mac (Swift 6, SwiftUI + AppKit). Proyecto con XcodeGen (`project.yml`). |
| `windows/` | App de Windows (Tauri 2: Rust en `src-tauri/`, TypeScript en `src/`). |
| `relay/` | Retransmisor sin estado (Cloudflare Worker) para el enlace con el teléfono; sin dirección por defecto. |
| `lazo/` | Lazo clásico. |
| `THIRD_PARTY_NOTICES.md` | Aviso de licencia del código incorporado. |

## Compilar

```
cd NotchBuddy && xcodegen && xcodebuild -scheme NotchBuddy -configuration Debug build
cd windows && npm install && npm run tauri dev
```

Pendiente: firma propia (`DEVELOPMENT_TEAM`, perfil, `MacUpdate.teamId`) y canal de actualizaciones propio. Hasta entonces no hay actualización automática.

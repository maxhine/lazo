# Traspaso: Lazo Isla (estado al 2026-10-23)

Para quien continúe (otro agente u otra persona). Léelo junto con `CLAUDE.md`, `docs/integracion.md` y `docs/engine-protocol.md`.

## Qué es

Rama `lazo-isla` del repositorio de Lazo (`maxhine/lazo`, remoto local `upstream-lazo`): Lazo (la herramienta de SARCA) dentro de una isla tipo notch. La interfaz, la animación y la estructura vienen de una app de notch MIT (código reutilizado, **sin** su nombre, personaje, íconos ni sonidos: el personaje es «Nudo», todo lo visible es propio; ver `THIRD_PARTY_NOTICES.md`). **Lazo es la herramienta; la isla es la interfaz**, nunca al revés. El Lazo clásico (WPF) sigue intacto en `lazo/` y se puede elegir en lugar de la isla (y volver).

Dueño: Jhon Andrew (`jhxnandrxw@gmail.com`). Habla español, quiere respuestas cortas y honestas sobre lo no probado, y que se pruebe antes de publicar.

## Dónde está cada cosa

- `windows/` Tauri 2: `src/` TypeScript (la interfaz), `src-tauri/src/` Rust (motor de red, ajustes, bandeja, actualizador, ventana).
  - `src/core/` modelos y lógica sin DOM (probados): `cloud.ts`, `tps.ts`, `eyecare.ts`, `progress.ts`, `greeting-text.ts`, `tabs.ts`, `drag-flow.ts`, `cloud-formats.ts`, `accent.ts`…
  - `src/views/` pantallas; `src/island/island.ts` es la isla (estados, arrastre, saludo, avisos).
  - `src-tauri/src/lazo_net/` motor LAZO3 en Rust (compatible con el clásico y el Mac).
- `engine/` programa C# `Lazo.Engine.exe` (`--tools-only`, sin red): reutiliza **tal cual** la lógica del clásico (Xpace, conversor de nubes de puntos, Descanso Visual) y sirve órdenes `herramienta.orden` por WebSocket local. `engine/tps2rin/tps2rin.exe` es de Topcon (incluido a pedido de Jhon).
- `lazo/` Lazo clásico (no tocar salvo cambios aditivos: modo `--tool=`, botón «Usar la isla», `EyeCare.cs` con el estilo «Notch» y `Preview`).
- `NotchBuddy/` (Mac, Swift) **no se ha compilado nunca** en esta rama; la sesión del Mac (`/Users/jhonandrew/Developer/lazo-isla`) tiene trabajo propio.

## Hecho (todo publicado en la versión 0.7.1)

Red y personas (pill de Lazo, personas en cuadritos con foto y punto, chat con personas, zumbido, envío por arrastre con animación y «Enviando X a Ana»); herramientas dentro de la isla (Xpace, nubes de puntos, Descanso Visual, TPS a RINEX con línea de tiempo); progreso en el notch; saludo por hora y nombre; ícono y tray de Nudo blanco; pestañas arrastrables; pills apagados por defecto (solo Lazo); español por defecto; avisos de descanso y pausa activa **en el notch** con cuenta regresiva y «probar el estilo»; nube de puntos arrastrada pregunta convertir/compartir; arrastre estable y que despierta la isla oculta; instalador propio con el aspecto del clásico; actualizaciones desde `maxhine/lazo` (etiqueta `windows-vX.Y.Z`); novedades al actualizar (0.7.0).

Publicado: rama `lazo-isla` en `maxhine/lazo` hasta `801e49d` y las releases **`windows-v0.7.0`** y **`windows-v0.7.1`** (asset `Lazo-Windows-0.7.1-setup.exe`, SHA-256 `cdcd8005…3d7e`; ninguna es «Latest», `--latest=false`: la «Latest» sigue siendo la v0.6.1 del clásico, cuyo actualizador lee `/releases/latest`). Después de `801e49d` solo hay commits de documentación.

## Cómo trabajar

- Tipos y pruebas (desde `windows/`): `npx tsc --noEmit`; `npm test` (533); `cd src-tauri && cargo test` (510); `node engine/test-tools.mjs` (20; usa tu `eyecare.txt` real, cada ajuste se cambia y se devuelve; nunca prueba avisos con ventana); `lazo/scripts/test-islandcore.ps1`.
- Programa de herramientas: `pwsh -File engine/build.ps1` (necesario antes de `cargo` si cambió `engine/` o `lazo/`, porque el build de Tauri lo empaqueta como recurso).
- Ejecutar la isla para probar: con `$env:PATH` incluyendo `$env:USERPROFILE\.cargo\bin`, `Start-Process cmd /c "npx tauri dev"` con salida redirigida y ventana oculta (nunca `| Select-Object -First N`: mata la app). Los cambios solo de TypeScript/CSS se recargan solos; los de Rust o `engine/` piden reiniciar. Cierra antes el Lazo clásico (mismos puertos 48351/48352).
- Ver la interfaz sin Tauri: el `tauri dev` ya sirve `http://localhost:1420`; `window.__island` y `window.__lazoTools` (solo desarrollo) dejan manejar la isla y poner motores falsos desde el navegador del panel (capturar a los ~3-5 s: la animación va lenta ahí; fijar `__island.fsm.homeToPetitDelay = 9999` para que no se pliegue).
- Instalador: `windows/scripts/build-installer.ps1` → `windows/release/Lazo-Windows-<versión>-setup.exe` (instala en `Program Files\Lazo Isla`, pide administrador; **nunca se ha ejecutado una instalación real**). El `.msi`/NSIS de Tauri ya no se publica (pisaría el `Lazo.exe` del clásico).

## Cómo sacar una versión (ej. 0.7.1)

1. Subir la versión en `windows/package.json`, `windows/package-lock.json` (2 sitios), `windows/src-tauri/tauri.conf.json`, `windows/Cargo.toml` (`[workspace.package]`, es la que lee el binario) y `windows/installer/Setup.cs` (`Version` y los 2 `AssemblyVersion`). Ese instalador es una copia adaptada de `lazo/installer/Setup.cs` (mismo aspecto): si el del clásico cambia de aspecto, hay que llevar el cambio a mano.
2. Añadir la entrada en `windows/src/whats-new/whats-new.json` en los **12 idiomas** (un test lo exige).
3. `npm test`, `cargo test`, `node engine/test-tools.mjs`, luego `pwsh -File windows/scripts/build-installer.ps1`.
4. Verificar contenido (se puede extraer el zip embebido) y, si es posible, instalar en un equipo limpio.
5. `git push upstream-lazo lazo-isla` y `gh release create windows-vX.Y.Z windows/release/Lazo-Windows-X.Y.Z-setup.exe --repo maxhine/lazo --target lazo-isla --latest=false --title … --notes-file …`; bajar el asset y comparar el SHA-256. **El nombre del asset y la etiqueta son los que busca el actualizador de la isla.** Sin firma (el actualizador solo comprueba el SHA-256 de GitHub). Un clasificador automático bloqueó una vez el `git push`: se hizo cuando Jhon lo pidió explícitamente por segunda vez.

## Pendiente (en el orden que pidió Jhon)

1. **Probar el instalador 0.7.1 en ATLAS** (el PC de casa de Jhon) y en otro equipo limpio: nunca se ha ejecutado una instalación real. Si falla algo, arreglarlo y sacar 0.7.2 con la receta de abajo.
2. **Probar a mano, con equipos reales** (nada de esto se ha visto fuera de simulaciones/navegador): arrastrar un archivo del Explorador a una persona y a una isla oculta (el arrastre se arregló por lectura del registro y simulación); zumbido entre dos equipos; envío real entre PCs; aviso de descanso/pausa real en el notch; instalar/desinstalar/actualizar con el instalador; el botón «Usar la isla» del clásico con la isla instalada.
3. **Pantalla compartida y control remoto** en la isla (dependen de la red; hoy la ficha dice «Coming soon»).
4. Verificación con código al emparejar; chat guardado en disco (compatible con `ChatStore` del clásico); ajustes de nombre, foto y carpeta de recepción dentro de la isla; sus textos que vienen del programa C# (progreso/errores) están en español sin `t()`.
5. Mac (Xcode/Swift sin compilar). Decidir si aplastar el historial git (845 commits, incluye los del proyecto original con su nombre) antes de abrir la rama más; confirmar con Topcon que se puede redistribuir `tps2rin.exe` en el repo y el instalador **públicos**; firma del instalador.
6. Ideas no hechas: sacar el pill de Lazo de la lista de pills sin romper el arrastre a personas (`lazoReady()` exige que el pill esté declarado); los demás pills (n8n, Vercel, GitHub…) se encienden en Ajustes → Active pills; la imagen que sigue al puntero al arrastrar es de Windows y no se puede cambiar.

## Trampas conocidas (resumen; detalle en la memoria de Claude `lazo-isla-lessons`)

- Bash en este entorno come las barras invertidas y falla con apóstrofes dentro de heredocs: para archivos con rutas `C:\…` o texto largo usar la herramienta de escritura de archivos; con rutas en pruebas, barras normales.
- `extra.json` (traducciones) no puede repetir claves del catálogo del Mac (`strings.json`); cada `t()`/`tl()` nuevo necesita las 11 lenguas; no hay literales ingleses de palabras ya traducidas fuera de `t()` en las vistas.
- Pruebas TS corren con Node «strip-only»: sin propiedades en parámetros del constructor ni enums.
- No parar procesos `Lazo.Engine` ni `node` a ciegas: matan la isla de Jhon. No capturar toda su pantalla (privacidad): solo ventanas (`lazo/scripts/capture-window.ps1`) o el navegador del panel.
- Su `%APPDATA%\Lazo\settings.json` conserva sus pills de antes aunque el valor por defecto cambió (se le dejó solo el de Lazo; copia en `settings.json.bak-pills`).

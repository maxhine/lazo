# Correcciones visuales y funcionales de Lazo

## Objetivo y alcance autorizado
Corregir fallos reproducibles de la última versión publicada, 0.4.14 (base `96820f3b38b88154937d529eccda322bf9fe7e6c`), empezando por el cierre al cambiar de reloj análogo a digital, la pérdida de borradores de chat y el desbordamiento de equipos en Standard. Entregar una compilación local lista para revisión. No publicar, instalar ni interactuar con otros equipos de la red.

## Política y restricciones
- TDD activado por confirmación del usuario el 2026-10-01: RED observado antes de modificar producción, GREEN y REFACTOR.
- Runner de regresiones WPF: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1`, precedido por `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Release`.
- Las pruebas deben aislar preferencias, perfil y chat reales; artefactos temporales en `bin`.
- Preservar estilos, idioma existente y cambios ajenos; no añadir atribuciones de IA.
- Ruta: delegada por tarea; lectura preparatoria y cambios con pruebas requieren trabajador. Un solo escritor.
- Rama: `codex/visual-functional-fixes`. Cada tarea termina con commit Conventional Commit y evidencia.
- RDD: activado por defecto; frontera inicial de revisión = commit base. Consentimiento por candidato cuando el proveedor lo requiera; no cambiar el modo.
- Estrategia de entrega: `ask-on-risk`; previsión aproximada de 350 líneas propias agregadas + eliminadas, incluidos pruebas y documento. No hay PR ni publicación autorizados. Si supera 400, resolver estrategia antes del siguiente commit.

## Tareas y aceptación
- [x] T01 — Reproducir y corregir Digital → Análogo → Digital, también cambios repetidos. Regresión aislada demuestra la excepción inicial y ausencia posterior; comprueba relación padre/hijo WPF y render de ambos modos. Ruta delegada: `EyeCare.cs` y `VisualSmoke.cs`. Ver evidencia T01.
- [ ] T02 — Reproducir y corregir pérdida de borrador cuando falla envío de chat, sin sobrescribir texto nuevo ni otra conversación. Prueba de fallo de transporte aislado, sin LAN ni perfil real. Ruta delegada: `ChatWindow.cs` y pruebas focalizadas.
- [ ] T03 — Reproducir y corregir equipos inaccesibles en Standard con muchos pares y nombres largos. Verificar desplazamiento y límites de pantalla; mantener clic/arrastre existentes. Ruta delegada: `MainWindow.cs` y pruebas visuales.
- [ ] T04 — Verificación integrada y entrega local versionada: conservar pruebas de reloj/chat/layout; compilar, Everything y visual; preparar instalador sin ejecutarlo y verificar su payload contra el mismo binario usado al empaquetarlo. Documentar política de pruebas y limitaciones reales. Ruta delegada por preparación, ejecución y varios archivos.

## Evidencia inicial
- `build.ps1 -Release`: PASS; advertencia CS0414 preexistente.
- `test-everything.ps1`: PASS.
- `test-visual.ps1`: PASS, sin cobertura de reloj/chat/Standard numerosos.
- `test-installer.ps1`: FAIL preexistente al comparar instalador publicado con recompilación no determinista; no acredita daño del instalador.
- `test-network.ps1`: omitida porque anuncia UDP a toda subred; ninguna transferencia entre equipos físicos verificada.
- Revisión estática inicial del reloj: `RebuildClockHost` reutilizaba un control cuyo padre anterior persiste; causa confirmada por RED en T01 (ver evidencia).

## Progreso y revisión
T01 terminado y verificado; T02–T04 pendientes. Conteo T01: 48 líneas añadidas (8 en producción + 40 en prueba), más la actualización documental. Commit de unidad pendiente del orquestador. Revisión: pendiente por unidad. Mirror Engram: guardado y leído de vuelta completo.

## Siguiente paso
T02: reproducir el fallo de transporte del chat sin LAN ni perfil real. El cierre de cada tarea registra comandos, resultados, escenario de ejecución y límite de rollback.

## Evidencia T01 — Reloj digital/análogo
- RED (antes de cambiar producción): `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` falló al reproducir Digital → Análogo → Digital con `InvalidOperationException`: «El elemento especificado ya es el elemento secundario lógico de otro elemento. Desconéctelo primero.» El stack confirma `EyeCareCardView.RebuildClockHost()`; después de reparar esa transición, la secuencia repetida también reveló la reutilización análoga con el mismo problema de parentesco.
- Causa y corrección: `RebuildClockHost` agregaba instancias reutilizadas (`_digitalClockText` y `_analogClock`) a nuevos `StackPanel` sin separarlas del padre lógico anterior. `DetachClockElement` las retira del `Panel` anterior antes de agregarlas.
- GREEN: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Release` PASS (advertencia preexistente CS0414 en `MainWindow._holding`); `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` PASS; `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-everything.ps1` PASS (Everything IPC consulta/respuesta Unicode).
- Escenario aislado: `VisualSmoke.VerifyClockRebuilds` crea una tarjeta sin constructor, cambia el backing field de modo sin llamar `SetClockMode` ni persistir preferencias, y ejecuta Digital → Análogo → Digital → Análogo → Digital. Comprueba padre WPF y render en cada estado. No se modificaron preferencias del perfil real.
- Capturas: `C:\Users\JQUIN\OneDrive\Cowork\05 Development\Lazo\bin\visual-check\clock-digital.png` y `C:\Users\JQUIN\OneDrive\Cowork\05 Development\Lazo\bin\visual-check\clock-analog.png`.
- Archivos/numstat de producción y prueba: `src/EyeCare.cs` +8/-0; `tests/VisualSmoke.cs` +40/-0. El documento añade evidencia T01. `git diff --check`: PASS.
- Límite de rollback: retirar únicamente el helper de desvinculación y sus dos llamadas en `src/EyeCare.cs` junto con `VerifyClockRebuilds` en `tests/VisualSmoke.cs`; no afecta T02–T04.
- Commit: pendiente del orquestador; sin commit ni revisión iniciados por esta tarea.

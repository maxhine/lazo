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
- [x] T02 — Reproducir y corregir pérdida de borrador cuando falla envío de chat, sin sobrescribir texto nuevo ni otra conversación. Prueba de fallo asíncrono aislado, sin LAN ni perfil real; los borradores quedan asociados a cada conversación. Ruta delegada: `ChatWindow.cs` y `VisualSmoke.cs`. Ver evidencia T02.
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
  T01–T02 terminados y verificados; T03–T04 pendientes. T02 agrega 123 líneas modificadas entre fuente y prueba, más evidencia documental; acumulado aproximado: 215 líneas más documentación. Commit T01: `e2cc089`; commit T02 pendiente del orquestador. Evaluación nativa de T01: medium, `review_due=false`, `under_budget`; frontera de revisión permanece en la base. Mirror Engram: T02 guardado y leído de vuelta completo.

## Siguiente paso
T03: reproducir el desbordamiento de equipos Standard con muchos pares y nombres largos. El cierre de cada tarea registra comandos, resultados, escenario de ejecución y límite de rollback.

## Evidencia T01 — Reloj digital/análogo
- RED (antes de cambiar producción): `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` falló al reproducir Digital → Análogo → Digital con `InvalidOperationException`: «El elemento especificado ya es el elemento secundario lógico de otro elemento. Desconéctelo primero.» El stack confirma `EyeCareCardView.RebuildClockHost()`; después de reparar esa transición, la secuencia repetida también reveló la reutilización análoga con el mismo problema de parentesco.
- Causa y corrección: `RebuildClockHost` agregaba instancias reutilizadas (`_digitalClockText` y `_analogClock`) a nuevos `StackPanel` sin separarlas del padre lógico anterior. `DetachClockElement` las retira del `Panel` anterior antes de agregarlas.
- GREEN: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Release` PASS (advertencia preexistente CS0414 en `MainWindow._holding`); `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` PASS; `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-everything.ps1` PASS (Everything IPC consulta/respuesta Unicode).
- Escenario aislado: `VisualSmoke.VerifyClockRebuilds` crea una tarjeta sin constructor, cambia el backing field de modo sin llamar `SetClockMode` ni persistir preferencias, y ejecuta Digital → Análogo → Digital → Análogo → Digital. Comprueba padre WPF y render en cada estado. No se modificaron preferencias del perfil real.
- Capturas: `C:\Users\JQUIN\OneDrive\Cowork\05 Development\Lazo\bin\visual-check\clock-digital.png` y `C:\Users\JQUIN\OneDrive\Cowork\05 Development\Lazo\bin\visual-check\clock-analog.png`.
- Archivos/numstat de producción y prueba: `src/EyeCare.cs` +8/-0; `tests/VisualSmoke.cs` +40/-0. El documento añade evidencia T01. `git diff --check`: PASS.
- Límite de rollback: retirar únicamente el helper de desvinculación y sus dos llamadas en `src/EyeCare.cs` junto con `VerifyClockRebuilds` en `tests/VisualSmoke.cs`; no afecta T02–T04.
- Spot-check independiente del padre: `test-visual.ps1` PASS, cinco transiciones con comprobación de padres y render; inspección estructural y visual PASS. El escenario no cubre el clic real de ajustes.
- Commit: `e2cc089` — `fix(clock): prevent crashes when toggling clock modes`.
- RDD: riesgo medium; `review_due=false`, `review_due_reason=under_budget`. Sin consentimiento ni revisión iniciados; rango pendiente en la frontera base.

## Evidencia T02 — Borradores de chat
- RED (antes de cambiar producción): `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` falló en la regresión nueva con `Failed chat send lost or changed the original draft`, después de pasar el smoke del reloj. La ruta real `ChatPanel.SendCurrent` vaciaba `_composer.Text` antes del `await`, y el `catch` solo actualizaba `_state`.
- Causa y corrección: un único `TextBox` no conservaba borradores por hilo, y el envío borraba el texto antes de conocer el resultado. Ahora `SelectDraft` guarda/carga texto por ID de compañero, `SendCurrent` lo conserva durante el envío y solo limpia el borrador original si el envío tuvo éxito, sigue seleccionada esa conversación y el contenido no cambió. El refresco posterior tampoco cambia de conversación.
- GREEN: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Release` PASS (advertencia preexistente CS0414 en `MainWindow._holding`); `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` PASS; `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-everything.ps1` PASS (Everything IPC consulta/respuesta Unicode); `git diff --check` PASS.
  - Escenario aislado: `VisualSmoke.VerifyFailedChatSendKeepsDraft` invoca el envío asíncrono real de `SendCurrent` con un transporte privado inyectado respaldado por `TaskCompletionSource`, y simula su fallo sin socket. Comprueba espacios originales, edición durante el envío, cambio a otra conversación y recuperación de ambos borradores al volver. No ejecuta `ChatStore` en la ruta fallida ni modifica perfil o archivos reales. La ruta de éxito se inspeccionó estáticamente, pero no se ejecutó porque escribe en `ChatStore`; queda pendiente una prueba de éxito que evite esa escritura. Sin captura visual: no añade valor para una prueba de estado del compositor.
  - Aislamiento de transporte: el motor de prueba no está inicializado y `203.0.113.7` es TEST-NET-3; `NetworkEngine.IsLocalSubnet` retorna antes de consultar interfaces porque `IsPrivate` excluye el rango `203.0.113.0/24`. El delegado de envío intercepta el chat; señales sin sockets, LAN ni transferencia.
  - Guardia de espera: `PumpUntil` tiene un límite de cinco segundos y detiene el `DispatcherTimer` dentro de `finally`; el vencimiento produce `TimeoutException` en vez de dejar colgado el smoke.
  - Archivos/numstat T02: `src/ChatWindow.cs` +35/-5; `tests/VisualSmoke.cs` +83/-0 (incluye el límite de espera). Evidencia añadida a este documento.
- Límite de rollback: revertir solo los cambios T02 de borradores/selector/delegado en `src/ChatWindow.cs` y `VerifyFailedChatSendKeepsDraft`/auxiliares en `tests/VisualSmoke.cs`; preservar los hunks T01 ya confirmados.
- Commit T02: pendiente del orquestador; no se inició revisión ni commit en esta tarea.

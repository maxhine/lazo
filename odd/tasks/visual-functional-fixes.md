# Correcciones visuales y funcionales de Lazo

## Objetivo y alcance autorizado
Corregir fallos reproducibles de la última versión publicada, 0.4.14 (base `96820f3b38b88154937d529eccda322bf9fe7e6c`), empezando por el cierre al cambiar de reloj análogo a digital, la pérdida de borradores de chat y el desbordamiento de equipos en Standard. Entregar una compilación local lista para revisión. No publicar, instalar ni interactuar con otros equipos de la red.

## Alcance inmediato actualizado
El usuario pidió implementar únicamente el tema y terminar esta entrega, sin más espera de revisión externa. Conservar T01/T02. T03/T04 se mantienen pendientes para otra entrega, no se ejecutan ahora. Agregar T05 como apariencia seleccionable inspirada en la referencia (marfil, concreto, terracota y oliva), sin reemplazar las existentes. No se autorizó envío de código a servicios externos; las comprobaciones de esta entrega son locales, sin aprobación nativa inventada. La revisión original conserva su candidato inmutable en Git, sin nuevos intentos de captura.

## Política y restricciones
- TDD activado por confirmación del usuario el 2026-10-01: RED observado antes de modificar producción, GREEN y REFACTOR.
- Runner de regresiones WPF: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1`, precedido por `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Release`.
- Las pruebas deben aislar preferencias, perfil y chat reales; artefactos temporales en `bin`.
- Preservar estilos, idioma existente y cambios ajenos; no añadir atribuciones de IA.
- Ruta: delegada por tarea; lectura preparatoria y cambios con pruebas requieren trabajador. Un solo escritor.
- Rama: `codex/visual-functional-fixes`. Cada tarea termina con commit Conventional Commit y evidencia.
- RDD: activado por defecto; frontera inicial de revisión = commit base. Consentimiento por candidato cuando el proveedor lo requiera; no cambiar el modo.
- Estrategia de entrega: `ask-on-risk`; alcance activo T01/T02/T05, previsión aproximada de 390 líneas propias agregadas + eliminadas, incluidos pruebas y documento. T03/T04 excluidos de esta entrega por instrucción del usuario. No hay PR ni publicación autorizados. Si supera 400, resolver estrategia antes del siguiente commit.

## Tareas y aceptación
- [x] T01 — Reproducir y corregir Digital → Análogo → Digital, también cambios repetidos. Regresión aislada demuestra la excepción inicial y ausencia posterior; comprueba relación padre/hijo WPF y render de ambos modos. Ruta delegada: `EyeCare.cs` y `VisualSmoke.cs`. Ver evidencia T01.
- [x] T02 — Reproducir y corregir pérdida de borrador cuando falla envío de chat, sin sobrescribir texto nuevo ni otra conversación. Prueba de fallo asíncrono aislado, sin LAN ni perfil real; los borradores quedan asociados a cada conversación. Ruta delegada: `ChatWindow.cs` y `VisualSmoke.cs`. Ver evidencia T02.
- [ ] T03 — Reproducir y corregir equipos inaccesibles en Standard con muchos pares y nombres largos. Verificar desplazamiento y límites de pantalla; mantener clic/arrastre existentes. Ruta delegada: `MainWindow.cs` y pruebas visuales.
- [ ] T04 — Verificación integrada y entrega local versionada: conservar pruebas de reloj/chat/layout; compilar, Everything y visual; preparar instalador sin ejecutarlo y verificar su payload contra el mismo binario usado al empaquetarlo. Documentar política de pruebas y limitaciones reales. Ruta delegada por preparación, ejecución y varios archivos.
- [x] T05 — Implementar apariencia Cálido: paleta de la referencia (#A7A29D, #6B6763, #C97F63, #8A8F7A, #F5EFE6), selector en Ajustes, token persistente y tipografía disponible con fallback. Conservar Plano/Vidrio/Oscuro. RED antes de producción; comprobar parser/serialización, contraste, selector y renders aislados Minimal/Standard/ajustes, más smoke existente. Ruta delegada: Theme.cs, MainWindow.cs, VisualSmoke.cs y documentación; trigger: varios archivos y lectura preparatoria. Ver evidencia T05.

## Evidencia inicial
- `build.ps1 -Release`: PASS; advertencia CS0414 preexistente.
- `test-everything.ps1`: PASS.
- `test-visual.ps1`: PASS, sin cobertura de reloj/chat/Standard numerosos.
- `test-installer.ps1`: FAIL preexistente al comparar instalador publicado con recompilación no determinista; no acredita daño del instalador.
- `test-network.ps1`: omitida porque anuncia UDP a toda subred; ninguna transferencia entre equipos físicos verificada.
- Revisión estática inicial del reloj: `RebuildClockHost` reutilizaba un control cuyo padre anterior persiste; causa confirmada por RED en T01 (ver evidencia).

## Progreso y revisión
  T01/T02/T05 terminados y verificados; T03–T04 pendientes fuera de esta entrega. T05 agrega 113 líneas y elimina 22 entre fuente y prueba; el alcance activo se mantiene dentro del pronóstico aproximado de 390 líneas. Commits previos: T01 `e2cc089`, T02 `3fd9539`; T05 pendiente del orquestador. Evaluación nativa de T01: medium, `review_due=false`, `under_budget`; frontera original permanece sin nuevos intentos de revisión externa. Mirror Engram: T05 guardado y leído de vuelta completo.

## Siguiente paso
T03/T04 quedan pendientes fuera de esta entrega. Commit T02 real: `3fd9539`; revisión externa sin veredicto por bloqueo de red y rechazo de egress, no repetirla en esta entrega. T05 no se ha confirmado en un commit; el orquestador cierra ese work unit según su flujo local. El cierre de cada tarea registra comandos, resultados, escenario de ejecución y límite de rollback.

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
- Commit T02: `3fd9539` — commit cerrado por el orquestador; no se inició revisión en T02.

## Evidencia T05 — Apariencia Cálido
- RED (antes de producción): `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` compiló el harness y ejecutó el binario previo al cambio. Reloj y borrador pasaron; el smoke terminó en `ArgumentException` porque el enum `ThemeKind` no contenía `Warm`.
- Implementación: se agregó `Warm` sin cambiar el fallback/default Plano; `SerializeTheme` (usado por `SetAppearance`) y `ParseTheme` conservan tokens previos y leen/escriben `warm`. La paleta aplica concreto a divisores, mushroom a texto muted, terracota a acciones seleccionadas con tinta oscura, oliva a superficies suaves, e ivory a paneles. Encabezados de 18 pt o más usan Georgia con Segoe UI como fallback; controles siguen con Bahnschrift/Segoe UI.
- GREEN: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Release` PASS (advertencia preexistente CS0414 en `MainWindow._holding`); `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-visual.ps1` PASS (reloj, chat, paleta/tokens/contraste/selector/renders warm, fotos y animación); `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-everything.ps1` PASS (Everything IPC); `git diff --check` PASS.
- Escenario aislado: `VisualSmoke.VerifyWarmTheme` prueba los tokens Raycast/Glass/Dark/Warm mediante las funciones de serialización/parsing utilizadas en producción, sin escribir AppData; valida roles exactos de paleta, contraste ≥4.5:1 para texto principal en terracota y texto muted en ivory, familia de encabezado con fallback, y selecciona Cálido en Ajustes. Genera previews Minimal y Standard con identidad temporal (`Tema de prueba`) y sin modificar el perfil.
- Capturas: `C:\Users\JQUIN\OneDrive\Cowork\05 Development\Lazo\bin\visual-check\theme-warm-minimal.png` y `C:\Users\JQUIN\OneDrive\Cowork\05 Development\Lazo\bin\visual-check\theme-warm-standard.png`.
- Archivos/numstat T05: `src/Theme.cs` +31/-19; `src/MainWindow.cs` +3/-3; `tests/VisualSmoke.cs` +79/-0. No se tocaron `EyeCare.cs` ni la distribución de paneles. `git diff --check`: PASS.
- Límite de rollback: retirar únicamente el enum, parser/serializador, paleta y tipografía warm de `src/Theme.cs`, la opción Cálido en `src/MainWindow.cs`, `VerifyWarmTheme`/`FindLogicalButton` en `tests/VisualSmoke.cs` y esta evidencia; conservar T01/T02.
- Spot-check funcional independiente: `test-visual.ps1` PASS (seis comprobaciones); renders Minimal/Standard legibles y parser/serializador usados por persistencia confirmados. Sin escritura de preferencias del usuario ni revisión externa.
- Commit T05: `57db4a2` — `feat(theme): add warm concrete and terracotta appearance`. Sin publicación ni instalación. Comprobaciones locales completas; revisión externa no disponible ni autorizada para esta entrega.
- Entrega local: `bin/Lazo.exe`, con T01/T02 y tema Cálido; activar en Ajustes → Apariencia → Cálido. T03/T04 permanecen pendientes fuera del alcance inmediato.

# Lazo Isla — guía para agentes

Rama de Lazo: interfaz de isla (Mac `NotchBuddy/`, Windows `windows/`) con el motor de Lazo integrado. Lazo clásico vive en `lazo/` y no se toca desde aquí salvo para extraer su motor.

## Reglas
- No usar ni reintroducir el nombre, personaje, íconos, sonidos o medios del proyecto base: todo lo visible es propio (personaje «Nudo» provisional, sonidos sintetizados, íconos propios). Ver `THIRD_PARTY_NOTICES.md`.
- No hay telemetría ni servicios por defecto: sin actualizador, relay ni donaciones del autor original. Llamadas de red solo a servicios que el usuario configure.
- Secretos en Keychain, nunca en disco ni en git. Nunca pisar `~/.claude/settings.json` sin copia con fecha y confirmación.
- No bloquear al agente: si la app no responde, el hook sale de inmediato.
- Los IDs de pills son contrato estable (Keychain, UserDefaults, hooks): no renombrarlos.
- `NotchBuddy.xcodeproj` se genera con XcodeGen y no se versiona.
- Bundle id: `com.sarca.lazo.isla`.

## Dónde está cada cosa
- `NotchBuddy/Sources/App/` Swift solo Mac; `NotchBuddy/Sources/LazoKit/` motor del personaje, pills (`PillCatalog.swift`), modelos.
- `NotchBuddy/Resources/sounds/` sonidos propios (sintetizados).
- `windows/src/` TypeScript; `windows/src-tauri/src/` Rust.

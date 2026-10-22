#!/usr/bin/swift
// HermesConfigMergerTests.swift — standalone test script for mergedHermesConfig.
// Run from repo root:
//   swift NotchBuddy/Tests/HermesConfigMergerTests.swift
//
// Requires pyyaml: pip3 install pyyaml
// Each merged output is validated as valid YAML via python3.

import Foundation

// ── Copy of mergedHermesConfig from HookServer.swift ─────────────────────────
// Keep in sync with HookServer.swift mergedHermesConfig(_:enableApprovals:).
func mergedHermesConfig(_ base: String, enableApprovals: Bool) -> String? {
    let unsafePatterns = ["{", " &", "\n---"]
    for p in unsafePatterns where base.contains(p) {
        return nil
    }

    var lines = base.components(separatedBy: "\n")

    let indent: Int = {
        for line in lines {
            let leading = line.prefix(while: { $0 == " " }).count
            if leading > 0 && leading <= 8 { return leading }
        }
        return 2
    }()
    let ind  = String(repeating: " ", count: indent)
    let ind2 = String(repeating: " ", count: indent * 2)

    func topLevelIndex(key: String) -> Int? {
        lines.firstIndex { line in
            let t = line.trimmingCharacters(in: .whitespaces)
            guard !line.hasPrefix(" ") && !line.hasPrefix("\t") else { return false }
            return t == "\(key):" || t.hasPrefix("\(key):")
        }
    }

    func sectionRange(from sectionIdx: Int) -> Range<Int> {
        var end = sectionIdx + 1
        while end < lines.count {
            let l = lines[end]
            if !l.isEmpty && !l.hasPrefix("#") && !l.hasPrefix(" ") && !l.hasPrefix("\t") {
                break
            }
            end += 1
        }
        return sectionIdx ..< end
    }

    let transportLine = "\(ind2)transport: lazo"
    let fallbackLine  = "\(ind2)transport_fallback: builtin"

    func ensureApprovalTransport() {
        if let secIdx = topLevelIndex(key: "security") {
            let secRange = sectionRange(from: secIdx)
            if let approvalIdx = (secRange.lowerBound + 1 ..< secRange.upperBound)
                .first(where: { lines[$0].trimmingCharacters(in: .whitespaces).hasPrefix("approval:") }) {
                let approvalRange = sectionRange(from: approvalIdx)
                var hasTransport = false
                var hasFallback  = false
                for i in (approvalRange.lowerBound + 1 ..< approvalRange.upperBound) {
                    let t = lines[i].trimmingCharacters(in: .whitespaces)
                    if t.hasPrefix("transport:") && !t.hasPrefix("transport_fallback") {
                        lines[i] = transportLine; hasTransport = true
                    } else if t.hasPrefix("transport_fallback:") {
                        lines[i] = fallbackLine; hasFallback = true
                    }
                }
                let insertAt = approvalRange.lowerBound + 1
                if !hasFallback  { lines.insert(fallbackLine,  at: insertAt) }
                if !hasTransport { lines.insert(transportLine, at: insertAt) }
            } else {
                let insertAt = secIdx + 1
                lines.insert("\(ind)approval:", at: insertAt)
                lines.insert(transportLine,     at: insertAt + 1)
                lines.insert(fallbackLine,      at: insertAt + 2)
            }
        } else {
            if lines.last != "" { lines.append("") }
            lines.append("security:")
            lines.append("\(ind)approval:")
            lines.append(transportLine)
            lines.append(fallbackLine)
        }
    }

    func removeApprovalTransport() {
        lines.removeAll { line in
            let t = line.trimmingCharacters(in: .whitespaces)
            return t == "transport: lazo" || t == "transport_fallback: builtin"
        }
    }

    func ensureLazoPlugin() {
        if let pluginsIdx = topLevelIndex(key: "plugins") {
            let pluginsRange = sectionRange(from: pluginsIdx)
            if let enabledIdx = (pluginsRange.lowerBound + 1 ..< pluginsRange.upperBound)
                .first(where: { lines[$0].trimmingCharacters(in: .whitespaces).hasPrefix("enabled:") }) {
                let enabledLine = lines[enabledIdx]
                let trimmed = enabledLine.trimmingCharacters(in: .whitespaces)
                if trimmed.contains("[") && trimmed.contains("]") {
                    if trimmed.contains("lazo") { return }
                    if trimmed == "enabled: []" || trimmed == "enabled:[]" {
                        let prefix = enabledLine.prefix(while: { $0 == " " })
                        lines[enabledIdx] = "\(prefix)enabled:"
                        lines.insert("\(prefix)\(ind)- lazo", at: enabledIdx + 1)
                    } else {
                        lines[enabledIdx] = enabledLine.replacingOccurrences(of: "]", with: ", lazo]")
                    }
                } else {
                    let enabledRange = sectionRange(from: enabledIdx)
                    let alreadyPresent = (enabledRange.lowerBound + 1 ..< enabledRange.upperBound)
                        .contains { lines[$0].trimmingCharacters(in: .whitespaces) == "- lazo" }
                    if alreadyPresent { return }
                    let prefix = enabledLine.prefix(while: { $0 == " " })
                    lines.insert("\(prefix)\(ind)- lazo", at: enabledIdx + 1)
                }
            } else {
                lines.insert("\(ind)enabled:", at: pluginsIdx + 1)
                lines.insert("\(ind)\(ind)- lazo", at: pluginsIdx + 2)
            }
        } else {
            if lines.last != "" { lines.append("") }
            lines.append("plugins:")
            lines.append("\(ind)enabled:")
            lines.append("\(ind)\(ind)- lazo")
        }
    }

    func removeLazoPlugin() {
        guard let pluginsIdx = topLevelIndex(key: "plugins") else { return }
        let pluginsRange = sectionRange(from: pluginsIdx)
        guard let enabledIdx = (pluginsRange.lowerBound + 1 ..< pluginsRange.upperBound)
            .first(where: { lines[$0].trimmingCharacters(in: .whitespaces).hasPrefix("enabled:") })
        else { return }
        let enabledLine = lines[enabledIdx]
        let trimmed = enabledLine.trimmingCharacters(in: .whitespaces)
        if trimmed.contains("[") && trimmed.contains("]") {
            let cleaned = trimmed
                .replacingOccurrences(of: ", lazo", with: "")
                .replacingOccurrences(of: "lazo, ", with: "")
                .replacingOccurrences(of: "lazo",   with: "")
            let prefix = enabledLine.prefix(while: { $0 == " " })
            lines[enabledIdx] = "\(prefix)\(cleaned)"
        } else {
            let enabledRange = sectionRange(from: enabledIdx)
            let toRemove = (enabledRange.lowerBound + 1 ..< enabledRange.upperBound)
                .filter { lines[$0].trimmingCharacters(in: .whitespaces) == "- lazo" }
            for i in toRemove.reversed() { lines.remove(at: i) }
        }
    }

    ensureLazoPlugin()
    if enableApprovals { ensureApprovalTransport() } else { removeApprovalTransport() }
    return lines.joined(separator: "\n")
}

// ── Test harness ─────────────────────────────────────────────────────────────

var passed = 0
var failed = 0

/// Find a Python3 that has pyyaml installed.
func findPython3() -> String {
    let candidates = [
        "/opt/homebrew/bin/python3",
        "/usr/local/bin/python3",
        "/usr/bin/python3",
    ]
    for p in candidates {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: p)
        proc.arguments = ["-c", "import yaml"]
        proc.standardOutput = Pipe(); proc.standardError = Pipe()
        if (try? proc.run()) != nil {
            proc.waitUntilExit()
            if proc.terminationStatus == 0 { return p }
        }
    }
    return "/usr/bin/python3"   // fallback
}

let pythonBin = findPython3()

func isValidYAML(_ s: String) -> Bool {
    let proc = Process()
    proc.executableURL = URL(fileURLWithPath: pythonBin)
    proc.arguments = ["-c", "import yaml,sys; yaml.safe_load(sys.stdin)"]
    let inPipe = Pipe(); let errPipe = Pipe()
    proc.standardInput  = inPipe
    proc.standardError  = errPipe
    proc.standardOutput = Pipe()
    try? proc.run()
    inPipe.fileHandleForWriting.write(s.data(using: .utf8)!)
    inPipe.fileHandleForWriting.closeFile()
    proc.waitUntilExit()
    return proc.terminationStatus == 0
}

func check(_ name: String, _ result: String?, contains: [String] = [], notContains: [String] = [],
           expectNil: Bool = false) {
    if expectNil {
        if result == nil {
            print("  PASS \(name)")
            passed += 1
        } else {
            print("  FAIL \(name): expected nil but got a string")
            failed += 1
        }
        return
    }
    guard let r = result else {
        print("  FAIL \(name): got nil unexpectedly")
        failed += 1
        return
    }
    guard isValidYAML(r) else {
        print("  FAIL \(name): result is not valid YAML\n---\n\(r)\n---")
        failed += 1
        return
    }
    for c in contains {
        guard r.contains(c) else {
            print("  FAIL \(name): missing \(c.debugDescription)\n---\n\(r)\n---")
            failed += 1
            return
        }
    }
    for nc in notContains {
        guard !r.contains(nc) else {
            print("  FAIL \(name): should not contain \(nc.debugDescription)\n---\n\(r)\n---")
            failed += 1
            return
        }
    }
    print("  PASS \(name)")
    passed += 1
}

print("HermesConfigMergerTests")
print("=======================")

// 1. Empty file → adds plugins.enabled.lazo
check("1. empty file → adds plugins/enabled/lazo",
      mergedHermesConfig("", enableApprovals: false),
      contains: ["plugins:", "enabled:", "- lazo"])

// 2. plugins: exists but no enabled: → adds enabled with lazo
check("2. plugins without enabled → adds enabled/lazo",
      mergedHermesConfig("plugins:\n  disabled: []\n", enableApprovals: false),
      contains: ["enabled:", "- lazo"])

// 3. plugins.enabled with other item → adds lazo
check("3. plugins.enabled other items → adds lazo",
      mergedHermesConfig("plugins:\n  enabled:\n    - other\n", enableApprovals: false),
      contains: ["- other", "- lazo"])

// 4. lazo already in plugins.enabled → no duplicate
let case4 = mergedHermesConfig("plugins:\n  enabled:\n    - lazo\n", enableApprovals: false)
check("4. lazo already present → no duplicate",
      case4,
      contains: ["- lazo"],
      notContains: [])
// Verify only one occurrence of "- lazo"
if let r4 = case4 {
    let count = r4.components(separatedBy: "- lazo").count - 1
    if count == 1 { print("  PASS 4b. no duplicate lazo entry"); passed += 1 }
    else { print("  FAIL 4b. got \(count) lazo entries"); failed += 1 }
}

// 5. enabled: [] (empty inline list) → expands to block list with lazo
check("5. enabled: [] → expands to block with lazo",
      mergedHermesConfig("plugins:\n  enabled: []\n", enableApprovals: false),
      contains: ["- lazo"],
      notContains: ["enabled: []"])

// 6. enabled: [other] inline list → adds lazo to inline list
check("6. enabled: [other] inline → adds lazo",
      mergedHermesConfig("plugins:\n  enabled: [other]\n", enableApprovals: false),
      contains: ["lazo"])

// 7. Another section with its own enabled: key → only modifies under plugins:
let case7 = """
features:
  enabled: [x]
plugins:
  enabled:
    - existing
"""
check("7. another section's enabled: not modified",
      mergedHermesConfig(case7, enableApprovals: false),
      contains: ["features:", "enabled: [x]", "- lazo"])

// 8. lazo in disabled: → still adds to enabled:
check("8. lazo in disabled → also adds to enabled",
      mergedHermesConfig("plugins:\n  disabled:\n    - lazo\n  enabled:\n    - other\n", enableApprovals: false),
      contains: ["- lazo", "- other"])

// 9. security: without approval: → adds approval with transport keys
check("9. security without approval → adds transport keys",
      mergedHermesConfig("security:\n  allow_private_urls: false\n", enableApprovals: true),
      contains: ["approval:", "transport: lazo", "transport_fallback: builtin"])

// 10a. Uninstall: removes - lazo from enabled
let case10a = """
plugins:
  enabled:
    - lazo
    - other
security:
  approval:
    transport: lazo
    transport_fallback: builtin
"""
check("10a. uninstall: removes lazo from enabled, transport keys",
      mergedHermesConfig(case10a, enableApprovals: false),
      notContains: ["transport: lazo", "transport_fallback: builtin"])

// 10b. After uninstall (disable approvals), transport keys removed
check("10b. disable approvals: transport keys removed",
      mergedHermesConfig("security:\n  approval:\n    transport: lazo\n    transport_fallback: builtin\n",
                         enableApprovals: false),
      notContains: ["transport: lazo", "transport_fallback: builtin"])

// 11. Flow map → returns nil
check("11. flow map { } → returns nil",
      mergedHermesConfig("{plugins: {enabled: [lazo]}}", enableApprovals: false),
      expectNil: true)

// 12. 4-space indentation → uses 4 spaces
let base12 = """
model: gpt-4
plugins:
    enabled:
        - other
"""
check("12. 4-space indent → uses 4 spaces",
      mergedHermesConfig(base12, enableApprovals: false),
      contains: ["        - lazo"])   // 8 spaces = 2 levels of 4

// 13. Enable approvals: adds both transport keys
check("13. enableApprovals=true adds both transport keys",
      mergedHermesConfig("", enableApprovals: true),
      contains: ["transport: lazo", "transport_fallback: builtin"])

// 14. security.approval already has transport → update, not duplicate
let case14 = """
security:
  approval:
    transport: terminal
    transport_fallback: deny
"""
check("14. existing transport → updated, no duplicate",
      mergedHermesConfig(case14, enableApprovals: true),
      contains: ["transport: lazo", "transport_fallback: builtin"],
      notContains: ["transport: terminal", "transport_fallback: deny"])

print("")
print("Results: \(passed) passed, \(failed) failed")
exit(failed > 0 ? 1 : 0)

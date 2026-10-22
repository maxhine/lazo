import Foundation

// MARK: - WakePhrase tests (pure functions, no concurrency)

@main
enum WakePhraseTests {

    static var failures = 0

    static func main() {
        // MARK: split — should match

        // Basic wake phrases (no command)
        checkMatch("ok lazo",              WakePhrase.split("ok lazo"),              cmd: "")
        checkMatch("OK LAZO",              WakePhrase.split("OK LAZO"),              cmd: "")
        checkMatch("okay lazo",            WakePhrase.split("okay lazo"),            cmd: "")
        checkMatch("ok kuku",                WakePhrase.split("ok kuku"),                cmd: "")
        checkMatch("ok cou cou",             WakePhrase.split("ok cou cou"),             cmd: "")
        checkMatch("hey lazo",             WakePhrase.split("hey lazo"),             cmd: "")
        checkMatch("Hey Lazo",             WakePhrase.split("Hey Lazo"),             cmd: "")

        // Command extracted from original text (casing/punctuation preserved)
        checkMatch("Okay, Lazo, ajoute GitHub",
                   WakePhrase.split("Okay, Lazo, ajoute GitHub"),             cmd: "ajoute GitHub")
        checkMatch("ok cuckoo add github",
                   WakePhrase.split("ok cuckoo add github"),                    cmd: "add github")
        checkMatch("ok lazo ouvre figma",
                   WakePhrase.split("ok lazo ouvre figma"),                   cmd: "ouvre figma")

        // Apostrophes, hyphens and accents preserved in command
        checkMatch("OK Lazo, mets l'album de Daft Punk",
                   WakePhrase.split("OK Lazo, mets l'album de Daft Punk"),   cmd: "mets l'album de Daft Punk")
        checkMatch("ok lazo qu'est-ce que fait Claude ?",
                   WakePhrase.split("ok lazo qu'est-ce que fait Claude ?"),  cmd: "qu'est-ce que fait Claude ?")

        // Wake anywhere in transcript (word-boundary search)
        checkMatch("euh ok lazo mets de la musique",
                   WakePhrase.split("euh ok lazo mets de la musique"),        cmd: "mets de la musique")
        checkMatch("la chanson dit ok lazo",
                   WakePhrase.split("la chanson dit ok lazo"),                cmd: "") // accepted false positive

        // LAST occurrence wins
        checkMatch("ok lazo search ok lazo play music",
                   WakePhrase.split("ok lazo search ok lazo play music"),   cmd: "play music")

        // Partial word-by-word delivery
        checkNoMatch("ok alone",             WakePhrase.split("ok"))
        checkMatch("ok lazo partial",      WakePhrase.split("ok lazo"),              cmd: "")
        checkMatch("ok lazo with more",    WakePhrase.split("ok lazo mets"),         cmd: "mets")

        // MARK: split — should NOT match
        checkNoMatch("standalone lazo",    WakePhrase.split("lazo"))
        checkNoMatch("standalone LAZO",    WakePhrase.split("LAZO"))
        checkNoMatch("partial lazo ça va", WakePhrase.split("lazo ça va"))
        checkNoMatch("lazo comment",       WakePhrase.split("lazo comment tu vas"))
        checkNoMatch("ok google",            WakePhrase.split("ok google"))
        checkNoMatch("hey siri",             WakePhrase.split("hey siri"))
        checkNoMatch("okkkk lazo",         WakePhrase.split("okkkk lazo"))
        checkNoMatch("empty",                WakePhrase.split(""))

        // Summary
        if failures == 0 { print("\n\(total)/\(total) tests passed.") }
        else { print("\n\(failures) test(s) FAILED."); exit(1) }
    }

    static var total = 0

    static func checkMatch(_ label: String, _ r: WakePhrase.SplitResult, cmd: String) {
        total += 1
        if !r.matched {
            print("✗  \(label) — expected match, got no match"); failures += 1; return
        }
        if r.command != cmd {
            print("✗  \(label) — matched ✓ but command \"\(r.command)\" ≠ \"\(cmd)\""); failures += 1; return
        }
        print("✓  \(label)")
    }

    static func checkNoMatch(_ label: String, _ r: WakePhrase.SplitResult) {
        total += 1
        if r.matched { print("✗  \(label) — expected no match, got matched (cmd: \"\(r.command)\")"); failures += 1 }
        else         { print("✓  \(label)") }
    }
}

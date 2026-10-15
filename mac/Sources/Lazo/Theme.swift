import AppKit
import SwiftUI

enum ThemeKind: String, CaseIterable, Identifiable {
    case wine, flat, glass, dark, warm, meet, image
    var id: String { rawValue }
    var label: String {
        switch self {
        case .wine: return "Vino"
        case .flat: return "Plano"
        case .glass: return "Vidrio"
        case .dark: return "Oscuro"
        case .warm: return "Cálido"
        case .meet: return "Meet"
        case .image: return "Imagen"
        }
    }
}

enum InterfaceKind: String { case minimal, standard }

/// Las mismas paletas que la versión de Windows (src/Theme.cs).
struct Palette {
    var dark: Bool
    var shell0, shell1, shell2, surface, soft, track, ink, muted, line, chip, chipText, avatar, online, accent: Color

    static func make(_ dark: Bool, _ v: [String]) -> Palette {
        let c = v.map { Color(hex: $0) }
        return Palette(dark: dark, shell0: c[0], shell1: c[1], shell2: c[2], surface: c[3], soft: c[4], track: c[5], ink: c[6],
                       muted: c[7], line: c[8], chip: c[9], chipText: c[10], avatar: c[11], online: c[12], accent: c[13])
    }

    static let wine = make(true, ["#632026", "#551A20", "#4A161B", "#47161B", "#5E2329", "#3B1115", "#FFF6F1", "#C99B9B", "#24FFFFFF", "#FCEBD2", "#4A1519", "#6A272D", "#7FD97A", "#EE5A45"])
    static let darkP = make(true, ["#262626", "#1C1C1C", "#141414", "#242424", "#2E2E2E", "#171717", "#E6E6E6", "#8E8E8E", "#3A3A3A", "#E6E6E6", "#161616", "#2A2A2A", "#34C759", "#E6E6E6"])
    static let flat = make(false, ["#F8F8FA", "#F0F1F4", "#E8E9EE", "#FFFFFF", "#ECEDF1", "#E3E4E9", "#1E1F24", "#666771", "#16000000", "#1E1F24", "#FFFFFF", "#E3E4E9", "#2FB45A", "#2E2F35"])
    static let glass = make(false, ["#F4F7FC", "#ECF0F7", "#E4E9F2", "#FFFFFF", "#E8ECF4", "#DDE3EE", "#1B2130", "#5F6676", "#1A1B2130", "#1B2130", "#FFFFFF", "#DDE3EE", "#2FB45A", "#3B82F6"])
    static let warm = make(false, ["#F7F1E8", "#F1E8DC", "#EADFD0", "#FCF8F2", "#EEE4D7", "#E6D9C8", "#211915", "#6B6560", "#1F3B2A1E", "#211915", "#F7F1E8", "#E2D4C1", "#5F9A57", "#C97F63"])
    static let meet = make(true, ["#1E1F20", "#171718", "#131314", "#1E1F20", "#333537", "#1E1F20", "#E3E3E3", "#9AA0A6", "#3C4043", "#A8C7FA", "#062E6F", "#3C4043", "#34A853", "#FBBC04"])
}

let accentChoices = ["#EE5A45", "#F2547D", "#8B6CF6", "#3B82F6", "#14B8A6", "#22A55B", "#F2A33A", "#2E2F35"]

extension Color {
    /// #RRGGBB o #AARRGGBB, como en WPF.
    init(hex: String) {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        let value = UInt64(text, radix: 16) ?? 0
        let a, r, g, b: Double
        if text.count == 8 {
            a = Double(value >> 24 & 255) / 255; r = Double(value >> 16 & 255) / 255; g = Double(value >> 8 & 255) / 255; b = Double(value & 255) / 255
        } else {
            a = 1; r = Double(value >> 16 & 255) / 255; g = Double(value >> 8 & 255) / 255; b = Double(value & 255) / 255
        }
        self.init(.sRGB, red: r, green: g, blue: b, opacity: a)
    }

    var hexString: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .black
        return String(format: "#%02X%02X%02X", Int(c.redComponent * 255), Int(c.greenComponent * 255), Int(c.blueComponent * 255))
    }
}

/// Tipografía: Bahnschrift no existe en Mac; se usa la redondeada del sistema, cercana en tono.
extension Font {
    static func lazo(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight, design: .rounded)
    }
}

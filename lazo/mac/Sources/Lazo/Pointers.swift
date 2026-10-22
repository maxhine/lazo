import AppKit

/// Punteros con nombre, como en una pizarra colaborativa (equivale a `PointerLayer` de Windows). Cada uno se desvanece
/// si deja de moverse. La vista no recibe clics: solo dibuja encima de la imagen o de los trazos.
final class PointerView: NSView {
    private struct Item {
        var name: String
        var color: UInt8
        var x: Double, y: Double
        var moved: Date
        var shown: Bool
        var alpha: CGFloat
    }

    private static let idle: TimeInterval = 5
    private var items: [Int32: Item] = [:]
    private var timer: Timer?

    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    var count: Int { items.values.filter(\.shown).count }

    func apply(_ update: PointerUpdate) {
        if update.visible {
            let name = update.name.trimmingCharacters(in: .whitespaces).isEmpty ? "Invitado" : update.name
            items[update.id] = Item(name: name, color: update.color, x: Double(update.x) / 65535, y: Double(update.y) / 65535,
                                    moved: Date(), shown: true, alpha: items[update.id]?.alpha ?? 0)
            startTimer()
        } else { hide(update.id) }
        needsDisplay = true
    }

    func hide(_ id: Int32) {
        guard items[id]?.shown == true else { return }
        items[id]?.shown = false
        startTimer()
        needsDisplay = true
    }

    func hideAll() { for id in Array(items.keys) { hide(id) } }

    private func startTimer() {
        guard timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in self?.tick() }
    }

    /// Desvanecimiento suave al aparecer, al ocultarse y al quedar inmóvil.
    private func tick() {
        var busy = false
        for (id, var item) in items {
            if item.shown, Date().timeIntervalSince(item.moved) > Self.idle { item.shown = false }
            let target: CGFloat = item.shown ? 1 : 0
            if abs(item.alpha - target) > 0.01 { item.alpha += (target - item.alpha) * 0.35; busy = true }
            else { item.alpha = target; if item.shown { busy = true } }
            if item.alpha == 0 && !item.shown { items.removeValue(forKey: id) } else { items[id] = item }
        }
        needsDisplay = true
        if !busy { timer?.invalidate(); timer = nil }
    }

    private var scale: CGFloat { max(0.8, min(1.6, bounds.width / 1280)) }

    override func draw(_ dirtyRect: NSRect) {
        guard let g = NSGraphicsContext.current?.cgContext else { return }
        for item in items.values where item.alpha > 0.01 {
            let color = PointerPalette[Int(item.color) % PointerPalette.count]
            let fill = NSColor(red: CGFloat(color >> 16 & 255) / 255, green: CGFloat(color >> 8 & 255) / 255, blue: CGFloat(color & 255) / 255, alpha: 1)
            let origin = CGPoint(x: item.x * bounds.width, y: item.y * bounds.height)
            g.saveGState()
            g.setAlpha(item.alpha)
            g.translateBy(x: origin.x, y: origin.y)
            g.scaleBy(x: scale, y: scale)
            // Flecha (misma forma que en Windows).
            let arrow = CGMutablePath()
            let points: [(CGFloat, CGFloat)] = [(0, 0), (0, 17), (4.5, 12.8), (7.6, 19.6), (10.3, 18.4), (7.3, 11.8), (13, 11.8)]
            arrow.move(to: CGPoint(x: points[0].0, y: points[0].1))
            for p in points.dropFirst() { arrow.addLine(to: CGPoint(x: p.0, y: p.1)) }
            arrow.closeSubpath()
            g.setShadow(offset: CGSize(width: 0, height: -1), blur: 5, color: CGColor(gray: 0, alpha: 0.35))
            g.addPath(arrow); g.setFillColor(fill.cgColor); g.fillPath()
            g.setShadow(offset: .zero, blur: 0)
            g.addPath(arrow); g.setStrokeColor(NSColor.white.cgColor); g.setLineWidth(1.4); g.setLineJoin(.round); g.strokePath()
            // Etiqueta con el nombre.
            let attributes: [NSAttributedString.Key: Any] = [.font: NSFont(name: "DINAlternate-Bold", size: 12.5) ?? NSFont.boldSystemFont(ofSize: 12.5), .foregroundColor: NSColor.white]
            let text = NSAttributedString(string: item.name, attributes: attributes)
            let size = text.size()
            let tag = CGRect(x: 13, y: 19, width: size.width + 18, height: size.height + 7)
            g.setShadow(offset: CGSize(width: 0, height: -1), blur: 6, color: CGColor(gray: 0, alpha: 0.3))
            g.addPath(CGPath(roundedRect: tag, cornerWidth: 10, cornerHeight: 10, transform: nil))
            g.setFillColor(fill.cgColor); g.fillPath()
            g.setShadow(offset: .zero, blur: 0)
            g.addPath(CGPath(roundedRect: tag, cornerWidth: 10, cornerHeight: 10, transform: nil))
            g.setStrokeColor(NSColor.white.cgColor); g.setLineWidth(1); g.strokePath()
            text.draw(at: CGPoint(x: tag.minX + 9, y: tag.minY + 3.5))
            g.restoreGState()
        }
    }
}

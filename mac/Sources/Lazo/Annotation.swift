import AppKit
import LazoCore
import SwiftUI

enum AnnotationTool: UInt8 { case pen = 1, marker = 2, arrow = 3, box = 4, eraser = 100 }

/// Cambio sobre los trazos, con coordenadas normalizadas 0–65535 (mismo formato que src/ScreenAnnotation.cs).
struct AnnotationOp {
    static let begin: UInt8 = 1, points: UInt8 = 2, erase: UInt8 = 3, clear: UInt8 = 4
    static let maxPoints = 4096
    var kind: UInt8
    var id: Int32
    var tool: AnnotationTool = .pen
    var color: UInt32 = 0
    var xs: [UInt16] = []
    var ys: [UInt16] = []

    func bytes() -> Data {
        var out = BinaryOut()
        out.u8(kind)
        out.i32(id)
        if kind == Self.begin { out.u8(tool.rawValue); out.u32(color) }
        if kind == Self.begin || kind == Self.points {
            out.u16(UInt16(xs.count))
            for i in 0..<xs.count { out.u16(xs[i]); out.u16(ys[i]) }
        }
        return out.data
    }

    static func parse(_ data: Data) -> AnnotationOp? {
        var i = data.startIndex
        func u8() -> UInt8? { guard i < data.endIndex else { return nil }; defer { i += 1 }; return data[i] }
        func le<T: FixedWidthInteger>(_ t: T.Type) -> T? {
            let n = MemoryLayout<T>.size
            guard i + n <= data.endIndex else { return nil }
            defer { i += n }
            return T(littleEndian: data[i..<i + n].withUnsafeBytes { $0.loadUnaligned(as: T.self) })
        }
        guard let kind = u8(), (begin...clear).contains(kind), let id = le(Int32.self) else { return nil }
        var op = AnnotationOp(kind: kind, id: id)
        if kind == begin {
            guard let raw = u8(), let tool = AnnotationTool(rawValue: raw), tool != .eraser, let color = le(UInt32.self) else { return nil }
            op.tool = tool
            op.color = color
        }
        if kind == begin || kind == points {
            guard let count = le(UInt16.self), Int(count) <= maxPoints, !(kind == begin && count == 0) else { return nil }
            for _ in 0..<count {
                guard let x = le(UInt16.self), let y = le(UInt16.self) else { return nil }
                op.xs.append(x); op.ys.append(y)
            }
        }
        return op
    }

    static func norm(_ value: Double) -> UInt16 { UInt16((max(0, min(1, value)) * 65535).rounded()) }
}

/// Estado de los trazos; quien comparte lo conserva para enviarlo a quien se une tarde.
final class AnnotationModel {
    final class Stroke {
        let id: Int32
        let tool: AnnotationTool
        let color: UInt32
        var xs: [UInt16] = []
        var ys: [UInt16] = []
        init(id: Int32, tool: AnnotationTool, color: UInt32) { self.id = id; self.tool = tool; self.color = color }
        var isShape: Bool { tool == .arrow || tool == .box }
    }

    static let maxStrokes = 2000, maxStrokePoints = 20000
    private(set) var order: [Stroke] = []
    private var byId: [Int32: Stroke] = [:]

    func find(_ id: Int32) -> Stroke? { byId[id] }

    @discardableResult func apply(_ op: AnnotationOp) -> Bool {
        switch op.kind {
        case AnnotationOp.begin:
            guard byId[op.id] == nil, order.count < Self.maxStrokes else { return false }
            let stroke = Stroke(id: op.id, tool: op.tool, color: op.color)
            order.append(stroke)
            byId[op.id] = stroke
            return append(stroke, op)
        case AnnotationOp.points:
            guard let stroke = byId[op.id], !op.xs.isEmpty else { return false }
            return append(stroke, op)
        case AnnotationOp.erase:
            guard let stroke = byId.removeValue(forKey: op.id) else { return false }
            order.removeAll { $0 === stroke }
            return true
        case AnnotationOp.clear:
            guard !order.isEmpty else { return false }
            order.removeAll()
            byId.removeAll()
            return true
        default: return false
        }
    }

    private func append(_ stroke: Stroke, _ op: AnnotationOp) -> Bool {
        for i in 0..<op.xs.count {
            if stroke.isShape && stroke.xs.count >= 2 { stroke.xs[1] = op.xs[i]; stroke.ys[1] = op.ys[i]; continue }
            if stroke.xs.count >= Self.maxStrokePoints { return false }
            stroke.xs.append(op.xs[i]); stroke.ys.append(op.ys[i])
        }
        return true
    }

    func snapshot() -> [AnnotationOp] {
        var ops: [AnnotationOp] = []
        for stroke in order {
            var start = 0
            while start < stroke.xs.count {
                let end = min(stroke.xs.count, start + AnnotationOp.maxPoints)
                ops.append(AnnotationOp(kind: start == 0 ? AnnotationOp.begin : AnnotationOp.points, id: stroke.id, tool: stroke.tool,
                                        color: stroke.color, xs: Array(stroke.xs[start..<end]), ys: Array(stroke.ys[start..<end])))
                start = end
            }
        }
        return ops
    }
}

/// Capa transparente que pinta los trazos y, con `drawing`, los dibuja con el mouse.
final class AnnotationView: NSView {
    static let colors: [UInt32] = [0xFFF25C45, 0xFFF2C94C, 0xFF27AE60, 0xFF3B82F6, 0xFFFFFFFF, 0xFF111111]
    let model = AnnotationModel()
    var tool: AnnotationTool = .pen
    var color: UInt32 = AnnotationView.colors[0]
    /// Visor: el borrador solo quita los trazos hechos en este equipo.
    var onlyMine = false
    var local: ((AnnotationOp) -> Void)?
    private(set) var mine = Set<Int32>()
    private var current: Int32 = 0
    private var batchX: [UInt16] = [], batchY: [UInt16] = []
    private var batchTimer: Timer?

    var drawing = false {
        didSet {
            if !drawing { flushBatch(); current = 0 }
            window?.invalidateCursorRects(for: self)
        }
    }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { drawing }
    override func hitTest(_ point: NSPoint) -> NSView? { drawing ? super.hitTest(point) : nil }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func resetCursorRects() {
        if drawing { addCursorRect(bounds, cursor: tool == .eraser ? .pointingHand : .crosshair) }
    }

    /// Cambio llegado de la red.
    func apply(_ op: AnnotationOp) {
        if model.apply(op) {
            if op.kind == AnnotationOp.clear { mine.removeAll() }
            if op.kind == AnnotationOp.erase { mine.remove(op.id) }
            needsDisplay = true
        }
    }

    /// Cambio propio: se aplica y se anuncia.
    @discardableResult func emit(_ op: AnnotationOp) -> Bool {
        guard model.apply(op) else { return false }
        if op.kind == AnnotationOp.begin { mine.insert(op.id) }
        if op.kind == AnnotationOp.clear { mine.removeAll() }
        if op.kind == AnnotationOp.erase { mine.remove(op.id) }
        needsDisplay = true
        local?(op)
        return true
    }

    func eraseMine() { for id in Array(mine) { emit(AnnotationOp(kind: AnnotationOp.erase, id: id)) } }
    func clearAll() { emit(AnnotationOp(kind: AnnotationOp.clear, id: 0)) }

    private func normalized(_ event: NSEvent) -> (UInt16, UInt16) {
        let p = convert(event.locationInWindow, from: nil)
        return (AnnotationOp.norm(p.x / max(1, bounds.width)), AnnotationOp.norm(p.y / max(1, bounds.height)))
    }

    override func mouseDown(with event: NSEvent) {
        guard drawing, bounds.width >= 1 else { return }
        let (x, y) = normalized(event)
        if tool == .eraser { current = -1; eraseAt(event); return }
        var id: Int32
        repeat { id = Int32.random(in: 1...Int32.max) } while model.find(id) != nil
        current = id
        emit(AnnotationOp(kind: AnnotationOp.begin, id: id, tool: tool, color: color, xs: [x], ys: [y]))
    }

    override func mouseDragged(with event: NSEvent) {
        guard drawing, current != 0 else { return }
        if current == -1 { eraseAt(event); return }
        let (x, y) = normalized(event)
        batchX.append(x); batchY.append(y)
        if batchX.count >= AnnotationOp.maxPoints { flushBatch() }
        else if batchTimer == nil {
            batchTimer = Timer.scheduledTimer(withTimeInterval: 0.016, repeats: false) { [weak self] _ in self?.flushBatch() }
        }
    }

    override func mouseUp(with event: NSEvent) {
        flushBatch()
        current = 0
    }

    /// Los puntos del trazo en curso salen agrupados: un paquete y un redibujado por fotograma.
    private func flushBatch() {
        batchTimer?.invalidate()
        batchTimer = nil
        guard !batchX.isEmpty, current > 0 else { batchX.removeAll(); batchY.removeAll(); return }
        let op = AnnotationOp(kind: AnnotationOp.points, id: current, xs: batchX, ys: batchY)
        batchX.removeAll(); batchY.removeAll()
        emit(op)
    }

    private func at(_ x: UInt16, _ y: UInt16) -> CGPoint {
        CGPoint(x: Double(x) / 65535 * bounds.width, y: Double(y) / 65535 * bounds.height)
    }

    private var scale: CGFloat { max(0.6, bounds.width / 1280) }

    private func width(_ tool: AnnotationTool) -> CGFloat {
        (tool == .marker ? 16 : tool == .pen ? 3.5 : 4) * scale
    }

    private func eraseAt(_ event: NSEvent) {
        let p = convert(event.locationInWindow, from: nil)
        for stroke in model.order.reversed() {
            if onlyMine && !mine.contains(stroke.id) { continue }
            if hits(stroke, p, 8 * scale + width(stroke.tool) / 2) {
                emit(AnnotationOp(kind: AnnotationOp.erase, id: stroke.id))
                return
            }
        }
    }

    private func hits(_ stroke: AnnotationModel.Stroke, _ p: CGPoint, _ reach: CGFloat) -> Bool {
        let first = at(stroke.xs[0], stroke.ys[0]), last = at(stroke.xs.last!, stroke.ys.last!)
        func near(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
            let dx = b.x - a.x, dy = b.y - a.y, len = dx * dx + dy * dy
            if len < 0.0001 { return hypot(p.x - a.x, p.y - a.y) }
            let t = max(0, min(1, ((p.x - a.x) * dx + (p.y - a.y) * dy) / len))
            return hypot(p.x - (a.x + dx * t), p.y - (a.y + dy * t))
        }
        if stroke.tool == .box {
            let a = CGPoint(x: first.x, y: last.y), b = CGPoint(x: last.x, y: first.y)
            return [near(first, a), near(a, last), near(last, b), near(b, first)].min()! <= reach
        }
        if stroke.tool == .arrow || stroke.xs.count == 1 { return near(first, last) <= reach }
        var previous = first
        for i in 1..<stroke.xs.count {
            let next = at(stroke.xs[i], stroke.ys[i])
            if near(previous, next) <= reach { return true }
            previous = next
        }
        return false
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let g = NSGraphicsContext.current?.cgContext else { return }
        g.setLineCap(.round)
        g.setLineJoin(.round)
        for stroke in model.order where !stroke.xs.isEmpty {
            let c = stroke.color
            let alpha = stroke.tool == .marker ? 0x70 : (c >> 24)
            g.setStrokeColor(red: CGFloat(c >> 16 & 255) / 255, green: CGFloat(c >> 8 & 255) / 255, blue: CGFloat(c & 255) / 255, alpha: CGFloat(alpha) / 255)
            let w = width(stroke.tool)
            g.setLineWidth(w)
            let first = at(stroke.xs[0], stroke.ys[0]), last = at(stroke.xs.last!, stroke.ys.last!)
            switch stroke.tool {
            case .box:
                g.stroke(CGRect(x: min(first.x, last.x), y: min(first.y, last.y), width: abs(last.x - first.x), height: abs(last.y - first.y)))
            case .arrow:
                g.move(to: first); g.addLine(to: last)
                let dx = last.x - first.x, dy = last.y - first.y, length = hypot(dx, dy)
                if length > 0.5 {
                    let ux = dx / length, uy = dy / length, head = max(12 * scale, w * 4)
                    g.move(to: CGPoint(x: last.x - ux * head - uy * head * 0.55, y: last.y - uy * head + ux * head * 0.55))
                    g.addLine(to: last)
                    g.addLine(to: CGPoint(x: last.x - ux * head + uy * head * 0.55, y: last.y - uy * head - ux * head * 0.55))
                }
                g.strokePath()
            default:
                g.move(to: first)
                if stroke.xs.count == 1 { g.addLine(to: CGPoint(x: first.x + 0.01, y: first.y)) }
                for i in 1..<stroke.xs.count { g.addLine(to: at(stroke.xs[i], stroke.ys[i])) }
                g.strokePath()
            }
        }
    }
}

/// Herramientas y colores en una fila para las barras de quien comparte y de quien mira.
struct AnnotationToolbar: View {
    let view: AnnotationView
    let clearTip: String
    let clear: () -> Void
    @State private var tool: AnnotationTool = .pen
    @State private var color: UInt32 = AnnotationView.colors[0]

    var body: some View {
        HStack(spacing: 2) {
            toolButton(.pen, "pencil", "Lápiz")
            toolButton(.marker, "highlighter", "Marcador")
            toolButton(.arrow, "arrow.up.right", "Flecha")
            toolButton(.box, "rectangle", "Recuadro")
            toolButton(.eraser, "eraser", "Borrador: pulsa un trazo para quitarlo")
            Rectangle().fill(Color.white.opacity(0.25)).frame(width: 1, height: 18).padding(.horizontal, 6)
            ForEach(AnnotationView.colors, id: \.self) { value in
                Button {
                    color = value; view.color = value
                    if tool == .eraser { tool = .pen; view.tool = .pen }
                } label: {
                    Circle().fill(Color(red: Double(value >> 16 & 255) / 255, green: Double(value >> 8 & 255) / 255, blue: Double(value & 255) / 255))
                        .frame(width: 14, height: 14)
                        .overlay(Circle().stroke(Color.white.opacity(color == value && tool != .eraser ? 1 : 0.3), lineWidth: color == value && tool != .eraser ? 2 : 1).padding(-3))
                        .padding(4)
                }.buttonStyle(.plain)
            }
            Button(action: clear) { Image(systemName: "trash").frame(width: 28, height: 28) }.buttonStyle(.plain).help(clearTip).padding(.leading, 6)
        }
        .foregroundStyle(.white)
    }

    private func toolButton(_ value: AnnotationTool, _ symbol: String, _ tip: String) -> some View {
        Button { tool = value; view.tool = value; view.window?.invalidateCursorRects(for: view) } label: {
            Image(systemName: symbol).frame(width: 28, height: 28).background(Circle().fill(Color.white.opacity(tool == value ? 0.33 : 0)))
        }.buttonStyle(.plain).help(tip)
    }
}

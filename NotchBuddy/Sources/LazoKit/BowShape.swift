import CoreGraphics

/// The character's body is a bow (lazo): two plump wings pinched at the waist.
/// Unit coordinates (x, y in −1…1, to be scaled by rx/ry), a closed polyline that starts at the
/// top of the waist and runs clockwise. The right half is five cubic Béziers, the left half is
/// its mirror image. Same outline as `BOW_OUTLINE` in windows/src/nudo/engine.ts.
enum BowShape {
    static let outline: [CGPoint] = {
        let segments: [[(CGFloat, CGFloat)]] = [
            [(0, -0.6), (0.3, -0.62), (0.52, -0.98), (0.78, -0.98)],
            [(0.78, -0.98), (0.96, -0.98), (1, -0.76), (1, -0.5)],
            [(1, -0.5), (1, -0.17), (1, 0.17), (1, 0.5)],
            [(1, 0.5), (1, 0.76), (0.96, 0.98), (0.78, 0.98)],
            [(0.78, 0.98), (0.52, 0.98), (0.3, 0.62), (0, 0.6)],
        ]
        let steps = 14
        var right: [CGPoint] = []
        for (si, c) in segments.enumerated() {
            for i in (si == 0 ? 0 : 1)...steps {
                let t = CGFloat(i) / CGFloat(steps), u = 1 - t
                let b0 = u * u * u, b1 = 3 * u * u * t, b2 = 3 * u * t * t, b3 = t * t * t
                right.append(CGPoint(
                    x: b0 * c[0].0 + b1 * c[1].0 + b2 * c[2].0 + b3 * c[3].0,
                    y: b0 * c[0].1 + b1 * c[1].1 + b2 * c[2].1 + b3 * c[3].1))
            }
        }
        // The right half ends at the bottom of the waist; the mirror comes back up without
        // repeating either centre point.
        let left = right.dropFirst().dropLast().reversed().map { CGPoint(x: -$0.x, y: $0.y) }
        return right + left
    }()

    /// The outline scaled to a body of half-width `rx` and half-height `ry`.
    static func points(rx: CGFloat, ry: CGFloat) -> [CGPoint] {
        outline.map { CGPoint(x: $0.x * rx, y: $0.y * ry) }
    }
}

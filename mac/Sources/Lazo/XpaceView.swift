import AppKit
import SwiftUI

func xpaceSize(_ bytes: Int64) -> String {
    let b = Double(bytes)
    if b >= 1_099_511_627_776 { return String(format: "%.2f TB", b / 1_099_511_627_776) }
    if b >= 1_073_741_824 { return String(format: "%.1f GB", b / 1_073_741_824) }
    if b >= 1_048_576 { return String(format: "%.1f MB", b / 1_048_576) }
    if b >= 1024 { return String(format: "%.0f KB", b / 1024) }
    return "\(bytes) B"
}

/// Estado de Xpace: sobrevive a que se cierre y se vuelva a abrir el panel.
@MainActor
final class XpaceSession: ObservableObject {
    static let shared = XpaceSession()
    @Published var path = ""
    @Published var running = false
    @Published var result: XResult?
    @Published var error: String?
    @Published var tab = "map"
    @Published var mapNode: XNode?
    @Published var expanded: Set<XNode> = []
    @Published var selected: String?
    @Published var progress = (files: Int64(0), bytes: Int64(0), folders: 0, current: "")
    private var scanner: XpaceScanner?
    private var timer: Timer?

    func start(_ folder: String) {
        guard !running else { return }
        guard FileManager.default.fileExists(atPath: folder) else { error = "Esa carpeta no existe."; return }
        path = folder; error = nil; result = nil; expanded = []; mapNode = nil; selected = nil; running = true
        let scanner = XpaceScanner()
        self.scanner = scanner
        timer = Timer.scheduledTimer(withTimeInterval: 0.15, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self, let s = self.scanner else { return }
                self.progress = (s.files, s.bytes, s.folders, s.current)
            }
        }
        Task.detached {
            let outcome = scanner.scan(folder)
            await MainActor.run {
                self.timer?.invalidate()
                self.running = false
                if outcome.cancelled { self.error = "Análisis cancelado."; return }
                self.result = outcome
                self.expanded = [outcome.root]
                self.mapNode = outcome.root
                self.tipsLoaded = false
                if self.tab == "tips" { self.loadSuggestions() }
            }
        }
    }

    func cancel() { scanner?.cancel() }

    // MARK: Sugerencias y borrado

    @Published var suggestions: [XSuggestion] = []
    @Published var loadingTips = false
    @Published var tipsLoaded = false

    func loadSuggestions(force: Bool = false) {
        guard !loadingTips, force || !tipsLoaded else { return }
        loadingTips = true
        let scanned = result
        Task.detached {
            var all = XpaceSuggestions.known() + (scanned.map { XpaceSuggestions.from($0) } ?? [])
            var seen = Set<String>()
            all = all.filter { seen.insert($0.path).inserted }
            let paths = all.map(\.path)
            all = all.filter { s in !paths.contains { $0 != s.path && s.path.hasPrefix($0 + "/") } }
            all.sort { $0.size > $1.size }
            let final = all
            await MainActor.run {
                self.suggestions = final
                self.loadingTips = false
                self.tipsLoaded = true
            }
        }
    }

    /// Pide confirmación, quita el elemento y deja el análisis y las sugerencias al día sin volver a recorrer el disco.
    @discardableResult
    func remove(path: String, size: Int64, isFolder: Bool) -> Bool {
        let outcome = XpaceActions.confirmAndRemove(path: path, size: size, isFolder: isFolder)
        guard outcome != .cancelled else { return false }
        suggestions.removeAll { $0.path == path || $0.path.hasPrefix(path + "/") }
        forget(path: path, isFolder: isFolder)
        if selected == path { selected = nil }
        return true
    }

    private func forget(path: String, isFolder: Bool) {
        guard var r = result, path.hasPrefix(r.path) else { return }
        let rel = path.dropFirst(r.path.count).split(separator: "/").map(String.init)
        guard !rel.isEmpty else { return }
        var node = r.root
        for part in rel.dropLast() {
            guard let next = node.children.first(where: { $0.name == part }) else { return }
            node = next
        }
        let last = rel.last!
        func subtract(from start: XNode?, size: Int64, files: Int64, folders: Int) {
            var up = start
            while let n = up { n.size -= size; n.files -= files; n.folders -= folders; up = n.parent }
        }
        if isFolder {
            guard let target = node.children.first(where: { $0.name == last }) else { return }
            node.children.removeAll { $0 === target }
            subtract(from: node, size: target.size, files: target.files, folders: target.folders + 1)
            r.bytes = r.root.size; r.files = r.root.files; r.folders = r.root.folders
            r.largest.removeAll { f in let p = f.folder.fullPath(r.path) + "/" + f.name; return p.hasPrefix(path + "/") }
        } else if let index = r.largest.firstIndex(where: { $0.folder === node && $0.name == last }) {
            let file = r.largest.remove(at: index)
            node.ownSize -= file.size; node.ownFiles -= 1
            subtract(from: node, size: file.size, files: 1, folders: 0)
            r.bytes = r.root.size; r.files = r.root.files
            let ext = (file.name as NSString).pathExtension.lowercased()
            let key = ext.isEmpty ? "(sin extensión)" : "." + ext
            if let i = r.types.firstIndex(where: { $0.ext == key }) { r.types[i].size -= file.size; r.types[i].count -= 1 }
        }
        if let map = mapNode, !isReachable(map, from: r.root) { mapNode = r.root }
        result = r
    }

    private func isReachable(_ node: XNode, from root: XNode) -> Bool {
        var n: XNode? = node
        while let c = n { if c === root { return true }; n = c.parent }
        return false
    }
}

struct XRow: Identifiable {
    var id: String
    var depth = 0
    var name: String
    var path: String?
    var size: Int64
    var files: Int64 = -1
    var modified: Date?
    var fraction: Double = -1
    var node: XNode?
    var isFile = false
    var expandable = false
    var isExpanded = false
}

/// Pantalla de Xpace: elegir dónde mirar, analizar y recorrer el espacio por mapa, carpetas, archivos grandes y tipos.
struct XpaceView: View {
    @ObservedObject var session = XpaceSession.shared
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let p = prefs.palette
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text(session.path.isEmpty ? "Elige una unidad o una carpeta para analizar" : session.path)
                    .font(.lazo(12.5)).lineLimit(1).truncationMode(.middle)
                    .foregroundStyle(session.path.isEmpty ? p.muted : p.ink)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.horizontal, 12).padding(.vertical, 8).surface(12)
                PillButton(title: "Carpeta…") { pick() }.disabled(session.running)
                if session.running {
                    PillButton(title: "Cancelar") { session.cancel() }
                } else {
                    PillButton(title: session.result == nil ? "Analizar" : "Volver a analizar") { session.start(session.path) }
                        .disabled(session.path.isEmpty)
                }
            }
            volumes
            if session.running {
                running
            } else {
                if let error = session.error { Text(error).font(.lazo(12)).foregroundStyle(.red) }
                if let result = session.result {
                    Summary(result: result)
                    tabs
                    switch session.tab {
                    case "tips": SuggestionsPane()
                    case "map": MapPane(result: result)
                    case "files": RowsList(result: result, mode: "files")
                    case "types": RowsList(result: result, mode: "types")
                    default: RowsList(result: result, mode: "folders")
                    }
                } else if session.tab == "tips" {
                    SuggestionsPane()
                } else if session.error == nil {
                    Text("Xpace recorre las carpetas en paralelo y muestra dónde se va el espacio, al estilo de TreeSize. No lee el contenido de los archivos. Si una carpeta de tu Mac no se puede leer, dale a Lazo acceso en Ajustes del Sistema › Privacidad › Acceso total al disco.")
                        .font(.lazo(11)).foregroundStyle(p.muted).padding(.top, 6)
                }
            }
        }
    }

    private var tabs: some View {
        Segmented(options: [("map", "Mapa"), ("folders", "Lista"), ("files", "Archivos grandes"), ("types", "Tipos"), ("tips", "Sugerencias")],
                  selection: Binding(get: { session.tab }, set: { session.tab = $0; session.selected = nil; if $0 == "tips" { session.loadSuggestions() } }))
    }

    private var volumes: some View {
        let home = NSHomeDirectory()
        var entries: [(String, String, String)] = [("Inicio", home, "")]
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeAvailableCapacityForImportantUsageKey, .volumeIsInternalKey]
        let urls = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) ?? []
        for url in urls {
            let values = try? url.resourceValues(forKeys: Set(keys))
            let free = values?.volumeAvailableCapacityForImportantUsage ?? 0
            let name = values?.volumeName ?? url.lastPathComponent
            entries.append((url.path == "/" ? "\(name) (disco)" : name, url.path, xpaceSize(free) + " libres"))
        }
        return ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                PillButton(title: "✦ Sugerencias") { session.tab = "tips"; session.loadSuggestions() }
                ForEach(entries, id: \.1) { entry in
                    PillButton(title: entry.2.isEmpty ? entry.0 : "\(entry.0) · \(entry.2)") { session.start(entry.1) }
                        .disabled(session.running)
                }
            }
        }
    }

    private var running: some View {
        let g = session.progress
        return VStack(alignment: .leading, spacing: 4) {
            ProgressView().progressViewStyle(.linear).padding(.vertical, 6)
            Text("Analizando…  \(g.files.formatted()) archivos · \(xpaceSize(g.bytes)) · \(g.folders.formatted()) carpetas").font(.lazo(12, .medium))
            Text(g.current).font(.lazo(10.5)).opacity(0.6).lineLimit(1).truncationMode(.middle)
        }
    }

    private func pick() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.prompt = "Analizar"
        panel.message = "Elige la carpeta que quieres analizar"
        LauncherController.shared.holding += 1
        defer { LauncherController.shared.holding -= 1 }
        NSApp.activate(ignoringOtherApps: true)
        if panel.runModal() == .OK, let url = panel.url { session.start(url.path) }
    }
}

private struct Summary: View {
    let result: XResult
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let cells: [(String, String)] = [
            (xpaceSize(result.bytes), "Analizado"), (result.files.formatted(), "archivos"),
            (result.folders.formatted(), "carpetas"), (String(format: "%.1f s", result.elapsed), "en analizar"),
        ]
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .top, spacing: 18) {
                ForEach(cells, id: \.1) { cell in
                    VStack(alignment: .leading, spacing: 1) {
                        Text(cell.1).font(.lazo(10.5)).foregroundStyle(prefs.palette.muted)
                        Text(cell.0).font(.lazo(19, .semibold))
                    }
                }
                Spacer(minLength: 0)
            }
            if result.denied > 0 {
                Text("\(result.denied.formatted()) carpetas sin acceso quedaron fuera del total.").font(.lazo(10.5)).foregroundStyle(prefs.palette.muted)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 10).frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(prefs.palette.soft))
    }
}

// MARK: - Mapa de árbol

private let mapPalette = ["#8B6CF6", "#EE5A45", "#14B8A6", "#3B82F6", "#F2A33A", "#F2547D", "#22A55B", "#E879F9"].map { Color(hex: $0) }

private final class MapItem {
    var node: XNode?
    var name: String
    var size: Int64
    var isFiles = false, isOther = false
    var hue: Int
    var rect = CGRect.zero
    var kids: [MapItem] = []
    init(node: XNode?, name: String, size: Int64, hue: Int) { self.node = node; self.name = name; self.size = size; self.hue = hue }
}

private struct MapPane: View {
    let result: XResult
    @ObservedObject var session = XpaceSession.shared
    @ObservedObject var prefs = Prefs.shared
    @State private var hover: ObjectIdentifier?

    var body: some View {
        let node = session.mapNode ?? result.root
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                crumbs(node)
                Spacer()
                Text(xpaceSize(node.size)).font(.system(size: 12, design: .monospaced)).foregroundStyle(prefs.palette.muted)
            }
            GeometryReader { geo in
                let items = layout(node, geo.size)
                ZStack(alignment: .topLeading) {
                    Canvas { context, _ in
                        for item in items { draw(&context, item, depth: 1) }
                    }
                    Color.clear.contentShape(Rectangle())
                        .onContinuousHover { phase in
                            if case .active(let point) = phase { hover = at(items, point).map { ObjectIdentifier($0) } } else { hover = nil }
                        }
                        .onTapGesture { point in
                            if let hit = at(items, point), let n = hit.node, !hit.isFiles, !n.children.isEmpty { session.mapNode = n }
                        }
                        .help(tooltip(items))
                        .contextMenu {
                            if let item = hoveredItem(items), let n = item.node, !item.isOther {
                                let path = item.isFiles ? n.fullPath(result.path) : n.fullPath(result.path)
                                Button("Abrir en Finder") { XpaceActions.reveal(path) }
                                if !item.isFiles, n.parent != nil {
                                    Button("Mover a la papelera…", role: .destructive) { session.remove(path: path, size: item.size, isFolder: true) }
                                }
                            }
                        }
                }
            }
            .frame(height: 340)
            .clipShape(RoundedRectangle(cornerRadius: 12))
            Text("Cada recuadro mide lo que ocupa. Clic en una carpeta para entrar · las migas de arriba vuelven atrás.")
                .font(.lazo(10.5)).foregroundStyle(prefs.palette.muted)
        }
    }

    private func hoveredItem(_ items: [MapItem]) -> MapItem? {
        guard let hover else { return nil }
        func find(_ list: [MapItem]) -> MapItem? {
            for item in list { if ObjectIdentifier(item) == hover { return item }; if let k = find(item.kids) { return k } }
            return nil
        }
        return find(items)
    }

    private func tooltip(_ items: [MapItem]) -> String {
        guard let hover else { return "" }
        func find(_ list: [MapItem]) -> MapItem? {
            for item in list { if ObjectIdentifier(item) == hover { return item }; if let k = find(item.kids) { return k } }
            return nil
        }
        guard let item = find(items) else { return "" }
        let total = Double(max(1, (session.mapNode ?? result.root).size))
        return "\(item.name)\n\(xpaceSize(item.size)) · \(String(format: "%.1f", Double(item.size) * 100 / total)) %"
    }

    private func crumbs(_ node: XNode) -> some View {
        var chain: [XNode] = []
        var n: XNode? = node
        while let c = n { chain.insert(c, at: 0); n = c.parent }
        return HStack(spacing: 4) {
            ForEach(Array(chain.enumerated()), id: \.offset) { index, target in
                let last = index == chain.count - 1
                Button { session.mapNode = target } label: {
                    Text(index == 0 ? result.path : target.name).font(.lazo(12, last ? .semibold : .regular)).lineLimit(1)
                        .foregroundStyle(last ? prefs.palette.ink : prefs.palette.muted)
                }.buttonStyle(.plain).disabled(last)
                if !last { Text("›").foregroundStyle(prefs.palette.muted) }
            }
        }
    }

    private func children(_ node: XNode, hue: Int, top: Bool) -> [MapItem] {
        var list: [MapItem] = []
        let total = Double(max(1, node.size))
        var other: Int64 = 0, otherCount = 0, index = 0
        for child in node.children where child.size > 0 {
            if Double(child.size) < total * 0.004 && list.count >= 4 { other += child.size; otherCount += 1; continue }
            list.append(MapItem(node: child, name: child.name, size: child.size, hue: top ? index % mapPalette.count : hue))
            index += 1
        }
        if node.ownSize > 0 { let i = MapItem(node: node, name: "archivos sueltos", size: node.ownSize, hue: -1); i.isFiles = true; list.append(i) }
        if other > 0 { let i = MapItem(node: nil, name: "otros (\(otherCount))", size: other, hue: -1); i.isOther = true; list.append(i) }
        return list.sorted { $0.size > $1.size }
    }

    private func layout(_ node: XNode, _ size: CGSize) -> [MapItem] {
        guard size.width >= 20, size.height >= 20 else { return [] }
        let items = children(node, hue: 0, top: true)
        squarify(items, CGRect(origin: .zero, size: size))
        for item in items {
            guard let n = item.node, !item.isFiles, !item.isOther, item.rect.width >= 120, item.rect.height >= 80 else { continue }
            item.kids = children(n, hue: item.hue, top: false)
            if !item.kids.isEmpty {
                squarify(item.kids, CGRect(x: item.rect.minX + 4, y: item.rect.minY + 24, width: item.rect.width - 8, height: item.rect.height - 28))
            }
        }
        return items
    }

    /// Reparte los elementos (ya ordenados de mayor a menor) en el rectángulo: recuadros lo más cuadrados posible.
    private func squarify(_ items: [MapItem], _ area: CGRect) {
        let total = items.reduce(0.0) { $0 + Double($1.size) }
        guard total > 0, area.width > 0, area.height > 0 else { return }
        let scale = Double(area.width * area.height) / total
        var start = 0
        var free = area
        func worst(_ s: Int, _ e: Int, _ sum: Double, _ side: Double) -> Double {
            var maxA = 0.0, minA = Double.greatestFiniteMagnitude
            for i in s..<e { let a = Double(items[i].size) * scale; maxA = max(maxA, a); minA = min(minA, a) }
            if minA <= 0 { return .greatestFiniteMagnitude }
            return max(side * side * maxA / (sum * sum), sum * sum / (side * side * minA))
        }
        while start < items.count {
            let horizontal = free.width >= free.height
            let side = Double(horizontal ? free.height : free.width)
            var end = start
            var sum = 0.0, best = Double.greatestFiniteMagnitude
            while end < items.count {
                let next = sum + Double(items[end].size) * scale
                let w = worst(start, end + 1, next, side)
                if w > best && end > start { break }
                best = w; sum = next; end += 1
            }
            let thickness = side <= 0 ? 0 : sum / side
            var offset = 0.0
            for i in start..<end {
                let length = sum <= 0 ? 0 : Double(items[i].size) * scale / sum * side
                items[i].rect = horizontal
                    ? CGRect(x: free.minX, y: free.minY + offset, width: thickness, height: length)
                    : CGRect(x: free.minX + offset, y: free.minY, width: length, height: thickness)
                offset += length
            }
            free = horizontal
                ? CGRect(x: free.minX + thickness, y: free.minY, width: max(0, free.width - thickness), height: free.height)
                : CGRect(x: free.minX, y: free.minY + thickness, width: free.width, height: max(0, free.height - thickness))
            start = end
        }
    }

    private func at(_ items: [MapItem], _ point: CGPoint) -> MapItem? {
        for item in items where item.rect.contains(point) {
            for kid in item.kids where kid.rect.contains(point) { return kid }
            return item
        }
        return nil
    }

    private func draw(_ context: inout GraphicsContext, _ item: MapItem, depth: Int) {
        guard item.rect.width >= 3, item.rect.height >= 3 else { return }
        let r = item.rect.insetBy(dx: 1.5, dy: 1.5)
        let dark = prefs.palette.dark
        let base = item.hue >= 0 ? mapPalette[item.hue % mapPalette.count] : (dark ? Color(white: 0.6) : Color(white: 0.45))
        let hovered = hover == ObjectIdentifier(item)
        var alpha = depth == 1 ? (dark ? 0.19 : 0.16) : (dark ? 0.25 : 0.22)
        if hovered { alpha += 0.15 }
        let shape = Path(roundedRect: r, cornerRadius: depth == 1 ? 8 : 5)
        context.fill(shape, with: .color(base.opacity(alpha)))
        context.stroke(shape, with: .color(base.opacity(hovered ? 1 : 0.53)), lineWidth: hovered ? 1.6 : 1)
        if r.width > 56, r.height > 28 {
            let ink = prefs.palette.ink, muted = prefs.palette.muted
            context.draw(Text(item.name).font(.lazo(depth == 1 ? 12 : 11, depth == 1 ? .semibold : .regular)).foregroundStyle(ink),
                         at: CGPoint(x: r.minX + 8, y: r.minY + 6), anchor: .topLeading)
            if depth == 1, r.width > 90 {
                if !item.kids.isEmpty {
                    if r.width > 260 {
                        context.draw(Text(xpaceSize(item.size)).font(.system(size: 10.5, design: .monospaced)).foregroundStyle(muted),
                                     at: CGPoint(x: r.maxX - 8, y: r.minY + 7), anchor: .topTrailing)
                    }
                } else if r.height > 56 {
                    let big = r.width > 150 && r.height > 90
                    context.draw(Text(xpaceSize(item.size)).font(.system(size: big ? 15 : 12, weight: .semibold, design: .monospaced)).foregroundStyle(ink),
                                 at: CGPoint(x: r.minX + 8, y: r.maxY - 8), anchor: .bottomLeading)
                }
            }
        }
        for kid in item.kids { draw(&context, kid, depth: 2) }
    }
}

// MARK: - Listas

private struct RowsList: View {
    let result: XResult
    let mode: String
    @ObservedObject var session = XpaceSession.shared
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let rows = build()
        let p = prefs.palette
        VStack(alignment: .leading, spacing: 4) {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(rows) { row in
                        HStack(spacing: 8) {
                            if mode == "folders" {
                                Image(systemName: row.expandable ? (row.isExpanded ? "chevron.down" : "chevron.right") : "circle.fill")
                                    .font(.system(size: row.expandable ? 9 : 3, weight: .bold)).frame(width: 12)
                                    .padding(.leading, CGFloat(row.depth) * 16).opacity(row.expandable ? 0.7 : 0.25)
                            }
                            VStack(alignment: .leading, spacing: 0) {
                                Text(row.name).font(.lazo(12.5, row.depth == 0 && mode == "folders" ? .semibold : .regular)).lineLimit(1).truncationMode(.middle)
                                if mode == "files", let path = row.path {
                                    Text((path as NSString).deletingLastPathComponent).font(.lazo(10)).foregroundStyle(p.muted).lineLimit(1).truncationMode(.head)
                                }
                            }
                            Spacer(minLength: 6)
                            if row.fraction >= 0 {
                                ZStack(alignment: .leading) {
                                    Capsule().fill(p.track)
                                    Capsule().fill(prefs.accentColor.opacity(0.75)).frame(width: max(2, 80 * row.fraction))
                                }.frame(width: 80, height: 6)
                                Text(String(format: "%.0f%%", row.fraction * 100)).font(.lazo(10.5)).foregroundStyle(p.muted).frame(width: 34, alignment: .trailing)
                            }
                            Text(xpaceSize(row.size)).font(.system(size: 11.5, design: .monospaced)).frame(width: 72, alignment: .trailing)
                            if row.files >= 0 {
                                Text(row.files.formatted()).font(.lazo(10.5)).foregroundStyle(p.muted).frame(width: 60, alignment: .trailing)
                            }
                            if mode != "types" {
                                Text(row.modified?.formatted(.iso8601.year().month().day()) ?? "").font(.lazo(10.5)).foregroundStyle(p.muted)
                                    .frame(width: 74, alignment: .trailing)
                            }
                        }
                        .padding(.horizontal, 8).frame(height: 30)
                        .background(RoundedRectangle(cornerRadius: 8).fill(session.selected == row.id ? p.soft : .clear))
                        .contentShape(Rectangle())
                        .onTapGesture(count: 2) { activate(row) }
                        .onTapGesture {
                            session.selected = row.id
                            if row.expandable, let node = row.node { toggle(node) }
                        }
                        .contextMenu {
                            if let path = row.path {
                                Button("Mostrar en Finder") { reveal(path) }
                                Button("Copiar ruta") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(path, forType: .string) }
                                if removable(row) { Divider(); Button("Mover a la papelera…", role: .destructive) { remove(row) } }
                            }
                        }
                    }
                }
            }
            .frame(height: 330)
            HStack(spacing: 8) {
                let current = rows.first { $0.id == session.selected }
                PillButton(title: "Mostrar en Finder") { if let path = current?.path { XpaceActions.reveal(path) } }.disabled(current?.path == nil)
                PillButton(title: "Quitar…") { if let current { remove(current) } }.disabled(current.map { !removable($0) } ?? true)
                Spacer()
                Text("Doble clic abre en Finder · clic derecho para más · tamaño lógico.").font(.lazo(10.5)).foregroundStyle(p.muted)
            }
        }
    }

    private func removable(_ row: XRow) -> Bool {
        guard row.path != nil, !row.id.hasSuffix("|archivos"), !row.id.hasPrefix("ext:") else { return false }
        if let node = row.node { return node.parent != nil }
        return row.isFile
    }

    private func remove(_ row: XRow) {
        guard let path = row.path, removable(row) else { return }
        session.remove(path: path, size: row.size, isFolder: row.node != nil)
    }

    private func toggle(_ node: XNode) {
        if session.expanded.contains(node) { session.expanded.remove(node) } else { session.expanded.insert(node) }
    }

    private func activate(_ row: XRow) {
        if let path = row.path { reveal(path) }
    }

    private func reveal(_ path: String) {
        let url = URL(fileURLWithPath: path)
        if FileManager.default.fileExists(atPath: path) { NSWorkspace.shared.activateFileViewerSelecting([url]) }
    }

    private func build() -> [XRow] {
        switch mode {
        case "files":
            return result.largest.map { f in
                let folder = f.folder.fullPath(result.path)
                let full = (folder == "/" ? "" : folder) + "/" + f.name
                return XRow(id: full, name: f.name, path: full, size: f.size, modified: f.modified, isFile: true)
            }
        case "types":
            return result.types.prefix(500).map { t in
                XRow(id: "ext:" + t.ext, name: t.ext, size: t.size, files: t.count, fraction: result.bytes == 0 ? 0 : Double(t.size) / Double(result.bytes), isFile: true)
            }
        default:
            var rows: [XRow] = []
            add(&rows, result.root, 0, result.path, 1)
            return rows
        }
    }

    private func add(_ rows: inout [XRow], _ node: XNode, _ depth: Int, _ path: String, _ fraction: Double) {
        let expandable = !node.children.isEmpty || node.ownFiles > 0
        let open = expandable && session.expanded.contains(node)
        rows.append(XRow(id: path, depth: depth, name: depth == 0 ? path : node.name, path: path, size: node.size, files: node.files,
                         modified: node.modified, fraction: fraction, node: node, expandable: expandable, isExpanded: open))
        guard open else { return }
        for child in node.children {
            add(&rows, child, depth + 1, (path == "/" ? "" : path) + "/" + child.name, node.size == 0 ? 0 : Double(child.size) / Double(node.size))
        }
        if node.ownFiles > 0 {
            rows.append(XRow(id: path + "|archivos", depth: depth + 1, name: "[\(node.ownFiles.formatted()) \(node.ownFiles == 1 ? "archivo" : "archivos")]",
                             path: path, size: node.ownSize, files: node.ownFiles, fraction: node.size == 0 ? 0 : Double(node.ownSize) / Double(node.size), isFile: true))
        }
    }
}

// MARK: - Sugerencias

private struct SuggestionsPane: View {
    @ObservedObject var session = XpaceSession.shared
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        let p = prefs.palette
        let removable = session.suggestions.filter { !$0.infoOnly }
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                if session.loadingTips {
                    ProgressView().controlSize(.small)
                    Text("Buscando qué se puede quitar…").font(.lazo(12))
                } else {
                    Text(removable.isEmpty ? "No encontré nada claro para quitar." : "Se podrían recuperar hasta \(xpaceSize(removable.reduce(0) { $0 + $1.size }))")
                        .font(.lazo(13, .semibold))
                }
                Spacer()
                PillButton(title: "Buscar de nuevo") { session.loadSuggestions(force: true) }.disabled(session.loadingTips)
            }
            Text(session.result == nil
                 ? "Revisé cachés y carpetas de desarrollo conocidas. Analiza una carpeta o un disco para encontrar además instaladores y archivos enormes sin tocar."
                 : "Incluye lo que encontró el análisis y las cachés conocidas del Mac. «Seguro» se vuelve a generar solo; «Revisar» pide que mires antes.")
                .font(.lazo(10.5)).foregroundStyle(p.muted)
            ScrollView {
                LazyVStack(spacing: 6) {
                    ForEach(session.suggestions) { s in
                        HStack(alignment: .top, spacing: 10) {
                            Image(systemName: s.risk == .safe ? "checkmark.shield.fill" : "exclamationmark.triangle.fill")
                                .foregroundStyle(s.risk == .safe ? Color.green : Color.orange).font(.system(size: 15)).padding(.top, 2)
                            VStack(alignment: .leading, spacing: 2) {
                                HStack(spacing: 6) {
                                    Text(s.title).font(.lazo(12.5, .semibold)).lineLimit(1)
                                    Text(s.risk == .safe ? "Seguro" : "Revisar").font(.lazo(9.5, .semibold)).padding(.horizontal, 6).padding(.vertical, 1)
                                        .background(Capsule().fill((s.risk == .safe ? Color.green : Color.orange).opacity(0.25)))
                                }
                                Text(s.why).font(.lazo(11)).foregroundStyle(p.muted).fixedSize(horizontal: false, vertical: true)
                                Text(s.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")).font(.lazo(10)).foregroundStyle(p.muted.opacity(0.8)).lineLimit(1).truncationMode(.middle)
                            }
                            Spacer(minLength: 6)
                            VStack(alignment: .trailing, spacing: 4) {
                                Text(xpaceSize(s.size)).font(.system(size: 13, weight: .semibold, design: .monospaced))
                                HStack(spacing: 4) {
                                    PillButton(title: "Finder") { XpaceActions.reveal(s.path) }
                                    if !s.infoOnly { PillButton(title: "Quitar…") { session.remove(path: s.path, size: s.size, isFolder: s.isFolder) } }
                                }
                            }
                        }
                        .padding(10)
                        .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(p.soft))
                    }
                }
            }
            .frame(height: 330)
        }
        .onAppear { session.loadSuggestions() }
    }
}

// MARK: - Utilidades

/// Panel de Utilidades en macOS. El conversor de nubes de puntos a ReCap solo existe en Windows (necesita DeCap).
struct ToolsView: View {
    @ObservedObject var state: LauncherState

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            PanelHeader(title: "Utilidades · Xpace", state: state)
            XpaceView()
            Text("El conversor de nubes de puntos a ReCap solo está disponible en Windows.").font(.lazo(10.5)).opacity(0.5)
        }
        .panelFrame(640)
    }
}

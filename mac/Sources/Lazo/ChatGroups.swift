import Foundation
import LazoCore

/// Identificadores de equipo: en memoria van como UUID con guiones en minúscula (como `Peer.id`); en archivos y red,
/// como Windows los escribe (`Guid.ToString("N")`, 32 dígitos sin guiones).
enum GuidText {
    static func n(_ id: String) -> String { id.replacingOccurrences(of: "-", with: "").lowercased() }

    /// Acepta con o sin guiones; devuelve el formato con guiones en minúscula, o nil si no es un identificador.
    static func parse(_ text: String) -> String? {
        let hex = text.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "-", with: "").lowercased()
        guard hex.count == 32, hex.allSatisfy({ $0.isHexDigit }) else { return nil }
        let c = Array(hex)
        func part(_ a: Int, _ b: Int) -> String { String(c[a..<b]) }
        return [part(0, 8), part(8, 12), part(12, 16), part(16, 20), part(20, 32)].joined(separator: "-")
    }
}

struct GroupMember {
    let id: String
    let name: String
}

/// Un grupo de chat: sin servidor, cada integrante guarda la lista y los mensajes se envían a cada uno.
struct ChatGroup: Identifiable {
    let id: String
    var name: String
    var owner: String
    /// Los demás integrantes (sin contarte a ti).
    var members: [GroupMember]
}

enum ChatGroups {
    static let maxMembers = 30
    private static let lock = NSLock()
    private static var file: URL { ChatStore.dataFolder().appendingPathComponent("groups.txt") }

    static func all() -> [ChatGroup] {
        lock.lock(); defer { lock.unlock() }
        return load()
    }

    static func get(_ id: String) -> ChatGroup? { all().first { $0.id == id } }

    static func save(_ group: ChatGroup) {
        lock.lock(); defer { lock.unlock() }
        var groups = load()
        groups.removeAll { $0.id == group.id }
        groups.append(group)
        store(groups)
    }

    static func remove(_ id: String) {
        lock.lock(); defer { lock.unlock() }
        var groups = load()
        groups.removeAll { $0.id == id }
        store(groups)
    }

    private static func load() -> [ChatGroup] {
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return [] }
        return text.split(separator: "\n").compactMap { row in
            let parts = row.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            guard parts.count >= 4, let id = GuidText.parse(parts[0]), let owner = GuidText.parse(parts[2]) else { return nil }
            var group = ChatGroup(id: id, name: parts[1], owner: owner, members: [])
            for member in parts[3].split(separator: ",") {
                let pair = member.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
                if pair.count == 2, let memberId = GuidText.parse(pair[0]) { group.members.append(GroupMember(id: memberId, name: pair[1])) }
            }
            return group
        }
    }

    private static func store(_ groups: [ChatGroup]) {
        let text = groups.map { g in
            GuidText.n(g.id) + "|" + clean(g.name) + "|" + GuidText.n(g.owner) + "|" + g.members.map { GuidText.n($0.id) + ":" + clean($0.name) }.joined(separator: ",")
        }.joined(separator: "\n")
        try? FileManager.default.createDirectory(at: ChatStore.dataFolder(), withIntermediateDirectories: true)
        try? (text.isEmpty ? "" : text + "\n").write(to: file, atomically: true, encoding: .utf8)
    }

    /// Texto de la lista de integrantes para enviarla por red.
    static func pack(_ group: ChatGroup, selfId: String, selfName: String) -> String {
        var lines = [GuidText.n(group.id), clean(group.name), GuidText.n(group.owner), GuidText.n(selfId) + ":" + clean(selfName)]
        for m in group.members where m.id != selfId { lines.append(GuidText.n(m.id) + ":" + clean(m.name)) }
        return lines.joined(separator: "\n")
    }

    /// Lo contrario de `pack`, desde el punto de vista de quien recibe: los demás, sin él.
    static func unpack(_ body: String, selfId: String) -> ChatGroup? {
        let lines = body.components(separatedBy: "\n")
        guard lines.count >= 4, let id = GuidText.parse(lines[0]), let owner = GuidText.parse(lines[2]) else { return nil }
        let name = clean(lines[1])
        guard !name.isEmpty else { return nil }
        var group = ChatGroup(id: id, name: name, owner: owner, members: [])
        for line in lines.dropFirst(3) where group.members.count < maxMembers {
            let pair = line.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
            if pair.count == 2, let memberId = GuidText.parse(pair[0]), memberId != selfId, !pair[1].trimmingCharacters(in: .whitespaces).isEmpty {
                group.members.append(GroupMember(id: memberId, name: clean(pair[1])))
            }
        }
        return group
    }

    static func clean(_ value: String) -> String {
        let cleaned = value.replacingOccurrences(of: "|", with: " ").replacingOccurrences(of: ":", with: " ").replacingOccurrences(of: ",", with: " ")
            .replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespaces)
        return String(cleaned.prefix(40))
    }
}

/// Identificador de un mensaje escondido al principio del texto con caracteres invisibles (igual que Windows): viaja por el
/// mismo camino de siempre y los equipos que no lo entienden lo muestran sin cambios.
enum WireId {
    private static let start = Unicode.Scalar(0x2063)!, end = Unicode.Scalar(0x2064)!
    private static let alphabet: [Unicode.Scalar] = [Unicode.Scalar(0x200B)!, Unicode.Scalar(0x200C)!, Unicode.Scalar(0x200D)!, Unicode.Scalar(0x2060)!]

    static func wrap(_ id: String, _ text: String) -> String {
        guard id.count == 8, id.allSatisfy({ $0.isHexDigit }) else { return text }
        var code = String.UnicodeScalarView()
        code.append(start)
        for c in id {
            let value = Int(String(c), radix: 16) ?? 0
            code.append(alphabet[(value >> 2) & 3]); code.append(alphabet[value & 3])
        }
        code.append(end)
        return String(code) + text
    }

    /// Separa el identificador ("" si no lo trae) y devuelve el texto limpio. Se recorre por escalares Unicode: los
    /// caracteres invisibles se fundirían con sus vecinos si se contaran como «caracteres» de Swift.
    static func unwrap(_ text: String) -> (id: String, text: String) {
        let scalars = Array(text.unicodeScalars)
        guard scalars.count >= 18, scalars[0] == start, scalars[17] == end else { return ("", text) }
        var id = ""
        for i in 0..<8 {
            guard let high = alphabet.firstIndex(of: scalars[1 + 2 * i]), let low = alphabet.firstIndex(of: scalars[2 + 2 * i]) else { return ("", text) }
            id += String((high << 2) | low, radix: 16)
        }
        var rest = String.UnicodeScalarView()
        rest.append(contentsOf: scalars[18...])
        return (id, String(rest))
    }
}

/// Envíos que no pudieron entregarse porque la persona estaba desconectada: salen cuando vuelve. Mismo archivo que Windows.
enum ChatOutbox {
    struct Item {
        let kind: UInt8
        let body: String
        let row: String
    }

    private static let lock = NSLock()
    private static let life: TimeInterval = 14 * 86_400
    private static var file: URL { ChatStore.dataFolder().appendingPathComponent("outbox.txt") }
    /// Ticks de .NET: 100 ns desde el año 1, como los escribe Windows.
    private static func ticks(_ date: Date = Date()) -> Int64 { Int64(date.timeIntervalSince1970 * 10_000_000) + 621_355_968_000_000_000 }

    static func add(_ peer: String, kind: UInt8, body: String) {
        lock.lock(); defer { lock.unlock() }
        try? FileManager.default.createDirectory(at: ChatStore.dataFolder(), withIntermediateDirectories: true)
        let row = GuidText.n(peer) + "|\(kind)|\(ticks())|" + Data(body.utf8).base64EncodedString() + "\n"
        if let handle = try? FileHandle(forWritingTo: file) { handle.seekToEndOfFile(); handle.write(Data(row.utf8)); try? handle.close() }
        else { try? row.write(to: file, atomically: true, encoding: .utf8) }
    }

    static func items(for peer: String) -> [Item] {
        lock.lock(); defer { lock.unlock() }
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return [] }
        let now = ticks()
        return text.split(separator: "\n").compactMap { row in
            let parts = row.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            guard parts.count >= 4, GuidText.parse(parts[0]) == peer, let kind = UInt8(parts[1]), let t = Int64(parts[2]),
                  Double(now - t) / 10_000_000 <= life, let data = Data(base64Encoded: parts[3]) else { return nil }
            return Item(kind: kind, body: String(decoding: data, as: UTF8.self), row: String(row))
        }
    }

    static func done(_ item: Item) {
        lock.lock(); defer { lock.unlock() }
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return }
        var rows = text.split(separator: "\n").map(String.init)
        if let at = rows.firstIndex(of: item.row) { rows.remove(at: at) }
        try? (rows.isEmpty ? "" : rows.joined(separator: "\n") + "\n").write(to: file, atomically: true, encoding: .utf8)
    }

    static func pending(_ peer: String) -> Int { items(for: peer).count }
}

/// Lo que pasa cuando llega un borrado o algo de un grupo: se valida quién lo manda y se actualiza el almacén local.
enum ChatEvents {
    struct Result {
        var changed = false
        var thread = ""
        var title = ""
        var text = ""
        /// Avisar a la persona (mensaje nuevo o alta en un grupo).
        var notify = false
    }

    static func apply(from: String, name: String, kind: UInt8, body: String, selfId: String) -> Result {
        let lines = body.components(separatedBy: "\n")
        switch kind {
        case NetworkEngine.chatKindDelete:
            let groupId = lines.count >= 2 ? GuidText.parse(lines[0]) : nil
            let id = (groupId != nil ? lines[1] : lines[0]).trimmingCharacters(in: .whitespaces)
            var thread = from
            var sender: String?
            if let groupId {
                thread = groupId
                guard let group = ChatGroups.get(groupId), isMember(group, from) else { return Result() }
                sender = name
            }
            guard !id.isEmpty, ChatStore.markDeleted(thread, msgId: id, sender: sender) else { return Result() }
            return Result(changed: true, thread: thread)
        case NetworkEngine.chatKindGroupMessage:
            guard lines.count >= 3, let groupId = GuidText.parse(lines[0]), let group = ChatGroups.get(groupId), isMember(group, from) else { return Result() }
            let text = lines.dropFirst(2).joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty, text.count <= 2000 else { return Result() }
            ChatStore.appendIncoming(groupId, name: group.name, text: text, msgId: lines[1].trimmingCharacters(in: .whitespaces), sender: name)
            return Result(changed: true, thread: groupId, title: group.name, text: name + ": " + text, notify: true)
        case NetworkEngine.chatKindGroupInfo:
            guard let incoming = ChatGroups.unpack(body, selfId: selfId), incoming.owner == from else { return Result() }
            let existing = ChatGroups.get(incoming.id)
            if let existing, existing.owner != from { return Result() }
            ChatGroups.save(incoming)
            ChatStore.ensureThread(incoming.id, name: incoming.name)
            return Result(changed: true, thread: incoming.id, title: incoming.name, text: name + " te añadió al grupo", notify: existing == nil)
        case NetworkEngine.chatKindGroupLeave:
            guard let groupId = GuidText.parse(lines[0]), var group = ChatGroups.get(groupId), isMember(group, from) else { return Result() }
            group.members.removeAll { $0.id == from }
            ChatGroups.save(group)
            return Result(changed: true, thread: groupId)
        default:
            return Result()
        }
    }

    static func isMember(_ group: ChatGroup, _ id: String) -> Bool {
        group.owner == id || group.members.contains { $0.id == id }
    }
}

import Foundation

public struct Item: Codable, Identifiable, Equatable {
    public var id: UUID
    public var content: String
    public var createdAt: Date
    public var pinned: Bool
    public var completed: Bool
    public var pinOrder: Int

    public init(content: String) {
        id = UUID()
        self.content = content
        createdAt = Date()
        pinned = false
        completed = false
        pinOrder = 0
    }
}

public enum PinResult: Equatable {
    case applied, needsReplacement, invalid
}

public struct Notebook: Codable, Equatable {
    public private(set) var items: [Item] = []
    public init() {}

    public var pins: [Item] {
        items.filter { $0.pinned && !$0.completed }.sorted { $0.pinOrder < $1.pinOrder }
    }

    public var inbox: [Item] {
        items.filter { !$0.pinned && !$0.completed }.sorted { $0.createdAt > $1.createdAt }
    }

    public var done: [Item] {
        items.filter(\.completed).sorted { $0.createdAt > $1.createdAt }
    }

    @discardableResult
    public mutating func add(_ content: String) -> UUID? {
        let text = content.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        let item = Item(content: text)
        items.append(item)
        return item.id
    }

    @discardableResult
    public mutating func pin(_ id: UUID, replacing oldID: UUID? = nil, limit: Int = 3) -> PinResult {
        guard let index = items.firstIndex(where: { $0.id == id }), !items[index].completed else {
            return .invalid
        }
        if items[index].pinned { return .applied }
        let rank: Int
        if let oldID {
            guard let oldIndex = items.firstIndex(where: { $0.id == oldID && $0.pinned && !$0.completed }) else {
                return .invalid
            }
            rank = items[oldIndex].pinOrder
            items[oldIndex].pinned = false
        } else {
            guard pins.count < max(1, min(20, limit)) else { return .needsReplacement }
            rank = pins.count
        }
        items[index].pinned = true
        items[index].pinOrder = rank
        normalizePins()
        return .applied
    }

    public mutating func unpin(_ id: UUID) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        items[i].pinned = false
        normalizePins()
    }

    @discardableResult
    public mutating func edit(_ id: UUID, content: String) -> Bool {
        let text = content.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, let i = items.firstIndex(where: { $0.id == id }) else { return false }
        items[i].content = text
        return true
    }

    public mutating func toggleCompleted(_ id: UUID) {
        guard let i = items.firstIndex(where: { $0.id == id }) else { return }
        items[i].completed.toggle()
        items[i].pinned = false
        normalizePins()
    }

    public mutating func delete(_ id: UUID) {
        items.removeAll { $0.id == id }
        normalizePins()
    }

    public mutating func clearCompleted() {
        items.removeAll { $0.completed }
    }

    public mutating func movePin(_ id: UUID, to targetID: UUID) {
        var order = pins.map(\.id)
        guard let source = order.firstIndex(of: id), let target = order.firstIndex(of: targetID), source != target else { return }
        order.remove(at: source)
        order.insert(id, at: target)
        for (rank, itemID) in order.enumerated() {
            if let index = items.firstIndex(where: { $0.id == itemID }) { items[index].pinOrder = rank }
        }
    }

    private mutating func normalizePins() {
        let order = pins.map(\.id)
        for (rank, id) in order.enumerated() {
            if let i = items.firstIndex(where: { $0.id == id }) { items[i].pinOrder = rank }
        }
    }

    public func validated() throws -> Notebook {
        guard Set(items.map(\.id)).count == items.count,
              pins.count <= 20,
              !items.contains(where: { $0.pinned && $0.completed }) else {
            throw CocoaError(.fileReadCorruptFile)
        }
        var result = self
        result.normalizePins()
        return result
    }
}

public struct NotebookFile {
    public let url: URL
    public init(url: URL) { self.url = url }

    public func load() throws -> Notebook {
        guard FileManager.default.fileExists(atPath: url.path) else { return Notebook() }
        return try JSONDecoder().decode(Notebook.self, from: Data(contentsOf: url)).validated()
    }

    public func save(_ notebook: Notebook) throws {
        _ = try notebook.validated()
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(notebook)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url, options: .atomic)
    }
}

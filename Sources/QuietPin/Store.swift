import AppKit
import SwiftUI
import QuietPinCore

struct BackgroundColor: Codable, Equatable {
    var red = 0.91
    var green = 0.90
    var blue = 0.86
    var alpha = 1.0

    var nsColor: NSColor { NSColor(srgbRed: red, green: green, blue: blue, alpha: alpha) }
    var color: Color { Color(nsColor: nsColor) }
    var isDark: Bool { red * 0.2126 + green * 0.7152 + blue * 0.0722 < 0.45 }
    var paletteKey: String {
        "#" + [red, green, blue, alpha].map { String(format: "%02X", Int((min(1, max(0, $0)) * 255).rounded())) }.joined()
    }

    init() {}
    init(_ color: NSColor) {
        let rgb = color.usingColorSpace(.sRGB) ?? .white
        red = rgb.redComponent
        green = rgb.greenComponent
        blue = rgb.blueComponent
        alpha = rgb.alphaComponent
    }
}

struct Preferences: Codable {
    var background = BackgroundColor()
    var idleOpacity = 0.40
    var activeOpacity = 0.95
    var fadeOnLeave = true
    var alwaysOnTop = true
    var collapsed = false
    var expandedFrame: String?
    var collapsedFrame: String?
    var shortcut = 0
    var stripMode: Bool?
    var stripFrame: String?
    var dockEdge: String?
    var customKeyCode: UInt32?
    var customModifiers: UInt32?
    var customKeyLabel: String?
    var commandReturnToSave: Bool?
    var captureOpacity: Double?
    var savedColors: [BackgroundColor]?
    var skipClearCompletedConfirmation: Bool?
    var pinLimit: Int?
    var pinCapacity: Int { max(1, min(20, pinLimit ?? 3)) }
}

@MainActor
final class Store: ObservableObject {
    @Published private(set) var notebook = Notebook()
    @Published var preferences: Preferences {
        didSet {
            if let data = try? JSONEncoder().encode(preferences) { defaults.set(data, forKey: "preferences") }
        }
    }
    @Published var errorMessage: String?
    @Published var pendingPin: UUID?
    @Published var shortcutError: String?
    @Published var recordingShortcut = false
    @Published var captureText = ""
    @Published var captureSession = UUID()
    private let file: NotebookFile
    private let defaults: UserDefaults
    private var canSave = true

    init(directory: URL? = nil, defaults: UserDefaults = .standard) {
        self.defaults = defaults
        preferences = defaults.data(forKey: "preferences")
            .flatMap { try? JSONDecoder().decode(Preferences.self, from: $0) } ?? Preferences()
        let root = directory ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("QuietPin", isDirectory: true)
        file = NotebookFile(url: root.appendingPathComponent("inbox.json"))
        do { notebook = try file.load() }
        catch {
            canSave = false
            errorMessage = "无法读取已有记录，已停止写入以保护原文件。\n\(file.url.path)\n\(error.localizedDescription)"
        }
    }

    @discardableResult
    private func update(_ edit: (inout Notebook) -> Void) -> Bool {
        guard canSave else {
            errorMessage = "记录文件未能载入。请先备份并检查：\n\(file.url.path)"
            return false
        }
        var next = notebook
        edit(&next)
        do {
            try file.save(next)
            notebook = next
            return true
        } catch {
            errorMessage = "保存失败，内容尚未写入磁盘：\n\(error.localizedDescription)"
            return false
        }
    }

    @discardableResult
    func add(_ content: String, pinned: Bool, replacing: UUID? = nil) -> Bool {
        guard !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        if pinned && notebook.pins.count >= preferences.pinCapacity && replacing == nil { return false }
        if let replacing, !notebook.pins.contains(where: { $0.id == replacing }) {
            errorMessage = "要替换的 Pin 已改变，请重新选择。"
            return false
        }
        var valid = true
        let saved = update { book in
            guard let id = book.add(content) else { valid = false; return }
            if pinned, book.pin(id, replacing: replacing, limit: preferences.pinCapacity) != .applied { valid = false }
        }
        return saved && valid
    }

    func requestPin(_ id: UUID) {
        if notebook.pins.contains(where: { $0.id == id }) {
            _ = update { $0.unpin(id) }
        } else if notebook.pins.count >= preferences.pinCapacity {
            pendingPin = id
        } else {
            _ = update { _ = $0.pin(id, limit: preferences.pinCapacity) }
        }
    }

    func replacePin(with oldID: UUID) {
        guard let newID = pendingPin else { return }
        if update({ _ = $0.pin(newID, replacing: oldID) }) { pendingPin = nil }
    }

    func complete(_ id: UUID) { _ = update { $0.toggleCompleted(id) } }
    func edit(_ id: UUID, content: String) -> Bool {
        guard !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              notebook.items.contains(where: { $0.id == id }) else { return false }
        return update { _ = $0.edit(id, content: content) }
    }
    func delete(_ id: UUID) { _ = update { $0.delete(id) } }
    func clearCompleted(skipFutureConfirmation: Bool = false) {
        if update({ $0.clearCompleted() }), skipFutureConfirmation {
            preferences.skipClearCompletedConfirmation = true
        }
    }
    func movePin(_ id: UUID, to target: UUID) { _ = update { $0.movePin(id, to: target) } }

    var currentColorIsSaved: Bool {
        (preferences.savedColors ?? []).contains { $0.paletteKey == preferences.background.paletteKey }
    }

    func saveCurrentColor() {
        guard !currentColorIsSaved else { return }
        preferences.savedColors = (preferences.savedColors ?? []) + [preferences.background]
    }

    func removeSavedColor(_ color: BackgroundColor) {
        preferences.savedColors = (preferences.savedColors ?? []).filter { $0.paletteKey != color.paletteKey }
    }

    func resetAppearance() {
        let original = Preferences()
        preferences.background = original.background
        preferences.idleOpacity = original.idleOpacity
        preferences.activeOpacity = original.activeOpacity
        preferences.fadeOnLeave = original.fadeOnLeave
        preferences.captureOpacity = original.captureOpacity
    }
}

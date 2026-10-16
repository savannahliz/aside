import AppKit

@main
struct PaletteChecks {
    @MainActor static func main() throws {
        let suite = "QuietPin.PaletteTest.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defer { defaults.removePersistentDomain(forName: suite) }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(suite)
        let first = BackgroundColor(NSColor(srgbRed: 0.8, green: 0.7, blue: 0.6, alpha: 0.5))
        let second = BackgroundColor(NSColor(srgbRed: 0.2, green: 0.4, blue: 0.5, alpha: 1))
        var legacy = Preferences()
        legacy.background = first
        var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(legacy)) as! [String: Any]
        json.removeValue(forKey: "savedColors")
        defaults.set(try JSONSerialization.data(withJSONObject: json), forKey: "preferences")
        let store = Store(directory: directory, defaults: defaults)
        precondition(store.preferences.pinCapacity == 3)
        precondition(store.preferences.skipClearCompletedConfirmation != true)
        precondition(store.add("done", pinned: false))
        precondition(store.add("keep pinned", pinned: true))
        let pinID = store.notebook.pins[0].id
        precondition(store.edit(pinID, content: "  edited pin  "))
        precondition(store.notebook.pins[0].content == "edited pin")
        store.preferences.pinLimit = 1
        precondition(!store.add("one too many", pinned: true))
        store.preferences.pinLimit = 4
        precondition(store.add("second pin", pinned: true))
        precondition(Store(directory: directory, defaults: defaults).preferences.pinCapacity == 4)
        store.complete(store.notebook.inbox[0].id)
        store.clearCompleted()
        let notesAfterClear = Store(directory: directory, defaults: defaults).notebook
        precondition(notesAfterClear.done.isEmpty && notesAfterClear.pins.count == 2 && notesAfterClear.items.count == 2)
        precondition(store.preferences.skipClearCompletedConfirmation != true)
        precondition(store.add("done without future confirmation", pinned: false))
        store.complete(store.notebook.inbox[0].id)
        store.clearCompleted(skipFutureConfirmation: true)
        let noPrompt = Store(directory: directory, defaults: defaults)
        precondition(noPrompt.preferences.skipClearCompletedConfirmation == true && noPrompt.notebook.done.isEmpty)
        noPrompt.clearCompleted()
        noPrompt.resetAppearance()
        precondition(Store(directory: directory, defaults: defaults).preferences.skipClearCompletedConfirmation == true)
        print("PASS: clear confirmation — legacy default asks, ordinary clear keeps asking, opt-out persists across reload/clear/appearance reset")
        precondition(store.preferences.background == first && store.preferences.savedColors == nil)
        store.saveCurrentColor()
        store.saveCurrentColor()
        precondition(store.preferences.savedColors?.count == 1 && store.currentColorIsSaved)
        store.preferences.background = second
        store.saveCurrentColor()
        let restarted = Store(directory: directory, defaults: defaults)
        precondition(restarted.preferences.savedColors == [first, second])
        restarted.preferences.background = restarted.preferences.savedColors![0]
        precondition(restarted.preferences.background == first)
        restarted.removeSavedColor(first)
        precondition(restarted.preferences.background == first && restarted.preferences.savedColors == [second])
        let deleted = Store(directory: directory, defaults: defaults)
        precondition(deleted.preferences.savedColors == [second])
        deleted.resetAppearance()
        precondition(deleted.preferences.savedColors == [second])
        deleted.removeSavedColor(second)
        precondition(Store(directory: directory, defaults: defaults).preferences.savedColors == [])
        print("PASS: palette — legacy preferences, save, deduplication, RGBA round trip, recall, delete without changing active color, restart, reset preservation")
    }
}

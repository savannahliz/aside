import Foundation
import QuietPinCore

@main
struct CoreChecks {
    static func main() throws {
        var book = Notebook()
        precondition(book.add(" \n ") == nil)
        let a = book.add("  写论文  ")!
        let b = book.add("回复导师")!
        let c = book.add("健身")!
        let d = book.add("第四条")!
        precondition(book.items.first?.content == "写论文")
        for id in [a, b, c] { precondition(book.pin(id) == .applied) }
        precondition(book.pin(d) == .needsReplacement)
        precondition(book.pin(d, limit: 4) == .applied)
        precondition(book.pins.count == 4)
        book.unpin(d)
        precondition(book.pins.count == 3)
        precondition(book.pin(d, replacing: b) == .applied)
        precondition(book.pins.map(\.id) == [a, d, c])
        precondition(book.inbox.map(\.id) == [b])
        precondition(book.edit(d, content: "  改过的事项  "))
        precondition(book.items.first(where: { $0.id == d })?.content == "改过的事项")
        precondition(!book.edit(d, content: "  "))
        precondition(book.pin(b, limit: 1) == .needsReplacement)
        _ = try book.validated()
        book.movePin(c, to: a)
        precondition(book.pins.map(\.id) == [c, a, d])
        book.toggleCompleted(a)
        precondition(book.pins.map(\.id) == [c, d])
        precondition(book.done.map(\.id) == [a])
        precondition(book.pin(a) == .invalid)
        book.toggleCompleted(a)
        precondition(book.pins.count == 2)
        precondition(book.inbox.contains { $0.id == a })
        book.unpin(c)
        precondition(book.pin(b) == .applied)
        precondition(book.pins.map(\.id) == [d, b])
        book.delete(d)
        precondition(book.pins.map(\.id) == [b])
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("quietpin-test-\(UUID())")
        let file = NotebookFile(url: root.appendingPathComponent("inbox.json"))
        try file.save(book)
        let restored = try file.load()
        precondition(restored == book)
        book.toggleCompleted(a)
        book.toggleCompleted(c)
        let activeBeforeClear = book.items.filter { !$0.completed }
        let pinsBeforeClear = book.pins
        book.clearCompleted()
        precondition(book.done.isEmpty && book.items == activeBeforeClear && book.pins == pinsBeforeClear)
        book.clearCompleted()
        precondition(book.items == activeBeforeClear)
        try file.save(book)
        let clearedFromDisk = try file.load()
        precondition(clearedFromDisk == book)
        for item in book.items { book.toggleCompleted(item.id) }
        book.clearCompleted()
        precondition(book.items.isEmpty)
        book.clearCompleted()
        precondition(book.items.isEmpty)
        print("PASS: clear completed — mixed, none, all, empty, pin/order preservation and disk persistence")
        try Data("invalid json".utf8).write(to: file.url)
        do { _ = try file.load(); fatalError("Corruption must not become an empty notebook") }
        catch { print("PASS: corrupt data is reported without overwriting") }
        print("PASS: empty input, Unicode, pin limit, replacement, ordering, completion, restore, deletion, disk round trip")
    }
}

import SwiftUI
import UniformTypeIdentifiers
import QuietPinCore
import ServiceManagement

private let accent = Color(red: 0.30, green: 0.39, blue: 0.33)
// Keep using the back-deployable property wrapper with newer SDKs that also
// export a State macro unavailable in standalone command-line toolchains.
private typealias ViewState<Value> = SwiftUI.State<Value>

struct PanelBackground: View {
    @ObservedObject var store: Store
    var body: some View {
        RoundedRectangle(cornerRadius: 16)
            .fill(store.preferences.background.color)
            .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(.primary.opacity(0.10), lineWidth: 1))
    }
}

struct IconButton: View {
    let symbol: String
    let label: String
    var action: () -> Void
    var body: some View {
        Button(action: action) { Image(systemName: symbol).frame(width: 24, height: 24).contentShape(Rectangle()) }
            .buttonStyle(.plain)
            .help(label)
            .accessibilityLabel(label)
    }
}

struct WindowDragArea: NSViewRepresentable {
    var doubleClicked: (() -> Void)? = nil
    func makeNSView(context: Context) -> DragView { DragView() }
    func updateNSView(_ view: DragView, context: Context) { view.doubleClicked = doubleClicked }

    final class DragView: NSView {
        var doubleClicked: (() -> Void)?
        override func mouseDown(with event: NSEvent) {
            if event.clickCount == 2, let doubleClicked { doubleClicked() }
            else { window?.performDrag(with: event) }
        }
    }
}

struct InboxView: View {
    @ObservedObject var store: Store
    let toggleCollapsed: () -> Void
    let expand: () -> Void
    let openSettings: () -> Void
    let minimize: () -> Void
    let hoverChanged: (Bool) -> Void
    let editingChanged: (Bool) -> Void
    @ViewState private var draft = ""
    @ViewState private var pinDraft = false
    @ViewState private var replacingDraft = false
    @ViewState private var showDone = false
    @ViewState private var confirmingClearCompleted = false
    @ViewState private var hovering = false
    @ViewState private var draggedPin: UUID? = nil
    @ViewState private var editingID: UUID? = nil
    @ViewState private var editDraft = ""

    var body: some View {
        VStack(spacing: 0) {
            if store.preferences.stripMode == true {
                HStack(spacing: 0) {
                    HStack {
                        Image(systemName: "line.3.horizontal").font(.system(size: 9)).foregroundStyle(.secondary)
                        Spacer(minLength: 36)
                    }
                    .frame(maxHeight: .infinity)
                    .overlay(WindowDragArea(doubleClicked: expand))
                    IconButton(symbol: "rectangle.expand.vertical", label: "展开 Inbox", action: expand)
                        .font(.system(size: 10))
                }
                .padding(.horizontal, 10).frame(height: 28)
            } else {
            if !store.preferences.collapsed {
                HStack(spacing: 4) {
                    HStack(spacing: 4) {
                        Text("Inbox").font(.system(size: 19, weight: .semibold, design: .rounded))
                        Text("\(store.notebook.inbox.count + store.notebook.pins.count)")
                            .font(.system(size: 11, design: .monospaced)).foregroundStyle(.secondary)
                        Spacer(minLength: 0)
                    }
                    .frame(height: 24)
                    .overlay(WindowDragArea())
                    Label("\(store.notebook.pins.count)/\(store.preferences.pinCapacity)", systemImage: "pin.fill")
                        .font(.system(size: 11, weight: .medium)).foregroundStyle(.secondary)
                    windowButtons
                    IconButton(symbol: "gearshape", label: "设置", action: openSettings)
                    IconButton(symbol: "arrow.down.right.and.arrow.up.left", label: "仅显示置顶事项", action: toggleCollapsed)
                }
                .padding(.horizontal, 18).padding(.top, 17).padding(.bottom, 12)
                Divider().padding(.horizontal, 18)
            }

            ScrollView {
                VStack(alignment: .leading, spacing: 3) {
                    if store.preferences.collapsed && store.notebook.pins.isEmpty {
                        Text("暂无置顶事项")
                            .font(.system(size: 13)).foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, minHeight: 45, alignment: .leading)
                            .padding(.horizontal, 10)
                    }
                    ForEach(store.notebook.pins) { item in
                        ItemRow(item: item, store: store, compact: store.preferences.collapsed,
                                editing: editingID == item.id, editDraft: $editDraft,
                                beginEdit: { startEditing(item) }, saveEdit: { saveEdit(item.id) }, cancelEdit: { cancelEdit(item.id) })
                            .onDrag {
                                draggedPin = item.id
                                return NSItemProvider(object: item.id.uuidString as NSString)
                            }
                            .onDrop(of: [UTType.text], delegate: PinDropDelegate(
                                target: item.id, dragged: $draggedPin, store: store))
                    }
                    if !store.preferences.collapsed {
                        if !store.notebook.pins.isEmpty && !store.notebook.inbox.isEmpty {
                            Divider().padding(.vertical, 9).padding(.horizontal, 8)
                        }
                        ForEach(store.notebook.inbox) { item in
                            ItemRow(item: item, store: store, compact: false,
                                    editing: editingID == item.id, editDraft: $editDraft,
                                    beginEdit: { startEditing(item) }, saveEdit: { saveEdit(item.id) }, cancelEdit: { cancelEdit(item.id) })
                        }
                        if store.notebook.inbox.isEmpty && store.notebook.pins.isEmpty {
                            VStack(alignment: .leading, spacing: 9) {
                                Image(systemName: "tray").font(.system(size: 25, weight: .ultraLight))
                                    .padding(.bottom, 5)
                                Text("想到就记，记完就走。")
                                    .font(.system(size: 15, weight: .medium))
                                Text("随手记入 Inbox，把最想看见的事项 Pin 在这里。")
                                    .font(.system(size: 12)).foregroundStyle(.secondary).lineSpacing(4)
                            }
                            .padding(.horizontal, 10).padding(.vertical, 32)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        if !store.notebook.done.isEmpty {
                            DisclosureGroup(isExpanded: $showDone) {
                                ForEach(store.notebook.done) { item in
                                    ItemRow(item: item, store: store, compact: false,
                                            editing: editingID == item.id, editDraft: $editDraft,
                                            beginEdit: { startEditing(item) }, saveEdit: { saveEdit(item.id) }, cancelEdit: { cancelEdit(item.id) })
                                }
                            } label: {
                                HStack {
                                    Text("已完成 · \(store.notebook.done.count)").font(.system(size: 11)).foregroundStyle(.secondary)
                                    Spacer()
                                    Button("清空") {
                                        if store.preferences.skipClearCompletedConfirmation == true { store.clearCompleted() }
                                        else { confirmingClearCompleted = true }
                                    }
                                        .buttonStyle(.plain).font(.system(size: 11)).foregroundStyle(.secondary)
                                        .accessibilityLabel("清空已完成事项")
                                }
                            }.padding(.top, 15).padding(.horizontal, 7)
                        }
                    }
                }
                .padding(.horizontal, store.preferences.collapsed ? 12 : 10)
                .padding(.top, store.preferences.collapsed ? 25 : 10)
                .padding(.bottom, 12)
            }

            if !store.preferences.collapsed {
                Divider().padding(.horizontal, 18)
                HStack(spacing: 7) {
                    Image(systemName: "plus").foregroundStyle(.secondary)
                    CaptureField(text: $draft, placeholder: "记下一件事…", focusToken: nil, submit: submitDraft, cancel: {})
                        .frame(height: 28)
                    IconButton(symbol: pinDraft ? "pin.fill" : "pin", label: pinDraft ? "取消新事项置顶" : "新事项直接置顶") { pinDraft.toggle() }
                    IconButton(symbol: "arrow.turn.down.left", label: "保存事项", action: submitDraft)
                        .disabled(draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                }
                .padding(.horizontal, 18).padding(.vertical, 11)
            }
            }
        }
        .background(PanelBackground(store: store))
        .overlay(alignment: .topLeading) {
            if store.preferences.collapsed && store.preferences.stripMode != true {
                WindowDragArea().frame(width: 80, height: 24)
            }
        }
        .overlay(alignment: .topTrailing) {
            if store.preferences.collapsed && store.preferences.stripMode != true {
                HStack(spacing: 2) {
                    windowButtons
                    IconButton(symbol: "gearshape", label: "设置", action: openSettings)
                    IconButton(symbol: "rectangle.compress.vertical", label: "收为细条", action: toggleCollapsed)
                    IconButton(symbol: "arrow.up.left.and.arrow.down.right", label: "展开 Inbox", action: expand)
                }
                .font(.system(size: 10))
                .padding(.trailing, 8).padding(.top, 1)
                .opacity(hovering ? 1 : 0)
                .allowsHitTesting(hovering)
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 16))
        .preferredColorScheme(store.preferences.background.isDark ? .dark : .light)
        .tint(accent)
        .onHover { inside in
            if !inside, let editingID { saveEdit(editingID) }
            hovering = inside
            hoverChanged(inside)
        }
        .alert("清空已完成事项？", isPresented: $confirmingClearCompleted) {
            Button("取消", role: .cancel) {}
            Button("清空 \(store.notebook.done.count) 条", role: .destructive) { store.clearCompleted() }
            Button("以后不再提示", role: .destructive) { store.clearCompleted(skipFutureConfirmation: true) }
        } message: {
            Text("将永久删除 \(store.notebook.done.count) 条已完成事项，无法撤销。未完成和置顶事项不受影响。选择「以后不再提示」将清空本次事项，并在今后直接清空，不再确认。")
        }
        .sheet(isPresented: Binding(get: { store.pendingPin != nil }, set: { if !$0 { store.pendingPin = nil } })) {
            ReplacementView(store: store, choose: { store.replacePin(with: $0) }, cancel: { store.pendingPin = nil })
        }
        .sheet(isPresented: $replacingDraft) {
            ReplacementView(store: store, choose: { id in
                if store.add(draft, pinned: true, replacing: id) {
                    draft = ""; pinDraft = false; replacingDraft = false
                }
            }, cancel: { replacingDraft = false })
        }
    }

    private var windowButtons: some View {
        Group {
            IconButton(symbol: store.preferences.alwaysOnTop ? "rectangle.on.rectangle.fill" : "rectangle.on.rectangle",
                       label: store.preferences.alwaysOnTop ? "取消 Inbox 窗口置顶" : "开启 Inbox 窗口置顶") {
                store.preferences.alwaysOnTop.toggle()
            }
            IconButton(symbol: "minus", label: "最小化到菜单栏", action: minimize)
        }
    }

    private func submitDraft() {
        guard !draft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if pinDraft && store.notebook.pins.count >= store.preferences.pinCapacity { replacingDraft = true; return }
        if store.add(draft, pinned: pinDraft) { draft = ""; pinDraft = false }
    }

    private func startEditing(_ item: Item) {
        editDraft = item.content
        editingID = item.id
        editingChanged(true)
    }

    private func saveEdit(_ id: UUID) {
        guard editingID == id else { return }
        if editDraft.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ||
            store.edit(id, content: editDraft) {
            cancelEdit(id)
        }
    }

    private func cancelEdit(_ id: UUID) {
        guard editingID == id else { return }
        editingID = nil
        editingChanged(false)
    }
}

struct ItemRow: View {
    let item: Item
    @ObservedObject var store: Store
    let compact: Bool
    let editing: Bool
    @Binding var editDraft: String
    let beginEdit: () -> Void
    let saveEdit: () -> Void
    let cancelEdit: () -> Void
    @ViewState private var hovering = false
    @FocusState private var editFocused: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 9) {
            Button { store.complete(item.id) } label: {
                Image(systemName: item.completed ? "checkmark.circle.fill" : "circle")
                    .font(.system(size: 14, weight: .light))
                    .foregroundStyle(.secondary).frame(width: 18, height: 22)
            }
            .buttonStyle(.plain).help(item.completed ? "恢复事项" : "标记完成")
            .accessibilityLabel(item.completed ? "恢复 \(item.content)" : "完成 \(item.content)")
            if editing {
                TextField("修改事项", text: $editDraft)
                    .font(.system(size: 13, weight: item.pinned ? .medium : .regular))
                    .textFieldStyle(.plain)
                    .focused($editFocused)
                    .onAppear { editFocused = true }
                    .onDisappear(perform: cancelEdit)
                    .onSubmit(saveEdit)
                    .onExitCommand(perform: cancelEdit)
                    .onChange(of: editFocused) { focused in if !focused { saveEdit() } }
                    .frame(maxWidth: .infinity, alignment: .leading).padding(.top, 3)
            } else {
                Button(action: beginEdit) {
                    Text(item.content)
                        .font(.system(size: 13, weight: item.pinned ? .medium : .regular))
                        .strikethrough(item.completed)
                        .foregroundStyle(item.completed ? .secondary : .primary)
                        .lineSpacing(3).fixedSize(horizontal: false, vertical: true)
                        .frame(maxWidth: .infinity, alignment: .leading).padding(.top, 3)
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain).help("点击修改事项")
                .accessibilityLabel("修改 \(item.content)")
            }
            if !item.completed {
                IconButton(symbol: item.pinned ? "pin.fill" : "pin", label: item.pinned ? "取消置顶 \(item.content)" : "置顶 \(item.content)") {
                    store.requestPin(item.id)
                }
                .font(.system(size: 11)).foregroundStyle(.secondary)
                .opacity(item.pinned || hovering ? 1 : 0)
            }
        }
        .padding(.horizontal, 8).padding(.vertical, 7)
        .background(RoundedRectangle(cornerRadius: 8).fill(.primary.opacity(hovering ? 0.05 : 0)))
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .contextMenu {
            if !item.completed {
                Button(item.pinned ? "取消置顶" : "置顶") { store.requestPin(item.id) }
            }
            Button(item.completed ? "恢复事项" : "标记完成") { store.complete(item.id) }
            Divider()
            Button("删除", role: .destructive) { store.delete(item.id) }
        }
    }
}

struct PinDropDelegate: DropDelegate {
    let target: UUID
    @Binding var dragged: UUID?
    let store: Store
    func validateDrop(info: DropInfo) -> Bool { dragged != nil }
    func dropUpdated(info: DropInfo) -> DropProposal? { DropProposal(operation: .move) }
    func performDrop(info: DropInfo) -> Bool {
        guard let dragged else { return false }
        store.movePin(dragged, to: target)
        self.dragged = nil
        return true
    }
}

struct ReplacementView: View {
    @ObservedObject var store: Store
    let choose: (UUID) -> Void
    let cancel: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("\(store.preferences.pinCapacity) 条 Pin 已满").font(.headline)
            Text("选择一条替换。原事项会留在 Inbox。")
                .font(.system(size: 12)).foregroundStyle(.secondary)
            ForEach(store.notebook.pins) { item in
                Button { choose(item.id) } label: {
                    HStack {
                        Image(systemName: "pin")
                        Text(item.content).lineLimit(3).multilineTextAlignment(.leading)
                        Spacer()
                        Image(systemName: "arrow.left.arrow.right")
                    }.frame(maxWidth: .infinity).padding(.vertical, 5)
                }
            }
            HStack { Spacer(); Button("取消", action: cancel).keyboardShortcut(.cancelAction) }
        }.padding(22).frame(width: 320)
    }
}

struct QuickCaptureView: View {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @ObservedObject var store: Store
    let close: () -> Void
    var inputReady: (NSTextField) -> Void = { _ in }
    @ViewState private var hovering = false
    @ViewState private var presented = false
    var body: some View {
        HStack(spacing: 12) {
            HStack(spacing: 15) {
            Image(systemName: "square.and.pencil")
                .font(.system(size: 23, weight: .light)).foregroundStyle(.secondary)
            CaptureField(text: $store.captureText, placeholder: "记下此刻的想法…", focusToken: store.captureSession,
                         submit: submit, cancel: close, commandReturn: store.preferences.commandReturnToSave == true,
                         fontSize: 20, ready: inputReady)
                .frame(height: 32)
            IconButton(symbol: store.preferences.commandReturnToSave == true ? "command" : "arrow.turn.down.left", label: "保存到 Inbox", action: submit)
                .foregroundStyle(.secondary)
            }
            .padding(.horizontal, 22).frame(height: 64)
            .background(Capsule().fill(store.preferences.background.color))
            .overlay(Capsule().strokeBorder(.primary.opacity(0.14), lineWidth: 1))
            Button(action: close) {
                Image(systemName: "xmark").font(.system(size: 18, weight: .medium))
                    .frame(width: 64, height: 64)
                    .background(Circle().fill(store.preferences.background.color))
                    .overlay(Circle().strokeBorder(.primary.opacity(0.14), lineWidth: 1))
                    .contentShape(Circle())
            }
            .buttonStyle(.plain).help("取消输入 · Esc").accessibilityLabel("取消输入")
            .opacity(hovering ? 1 : 0)
            .scaleEffect(hovering ? 1 : 0.72)
            .allowsHitTesting(hovering)
        }
        .frame(height: 64)
        .contentShape(Rectangle())
        .onHover { inside in
            if reduceMotion { hovering = inside }
            else { withAnimation(.spring(response: 0.30, dampingFraction: 0.60)) { hovering = inside } }
        }
        .scaleEffect(x: presented ? 1 : 0.90, y: presented ? 1 : 0.98)
        .onAppear { presentCapture() }
        .onChange(of: store.captureSession) { _ in presentCapture() }
        .preferredColorScheme(store.preferences.background.isDark ? .dark : .light)
    }

    private func presentCapture() {
        if reduceMotion { presented = true; hovering = false; return }
        presented = false
        hovering = false
        DispatchQueue.main.async {
            withAnimation(.spring(response: 0.12, dampingFraction: 0.85)) { presented = true }
        }
    }

    private func submit() {
        guard !store.captureText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if store.add(store.captureText, pinned: false) { finish() }
    }

    private func finish() {
        store.captureText = ""
        close()
    }
}

// NSTextField preserves Chinese/Japanese marked text: Return commits the IME
// candidate before a subsequent Return submits the item.
struct CaptureField: NSViewRepresentable {
    @Binding var text: String
    var placeholder: String
    var focusToken: UUID?
    var submit: () -> Void
    var cancel: () -> Void
    var commandReturn = false
    var fontSize: CGFloat = 14
    var ready: (NSTextField) -> Void = { _ in }

    func makeCoordinator() -> Coordinator { Coordinator(self) }
    func makeNSView(context: Context) -> NSTextField {
        let field = CaptureTextField()
        field.isBordered = false
        field.drawsBackground = false
        field.focusRingType = .none
        field.font = .systemFont(ofSize: fontSize)
        field.placeholderString = placeholder
        field.delegate = context.coordinator
        field.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        field.setAccessibilityLabel(placeholder)
        ready(field)
        return field
    }
    func updateNSView(_ field: NSTextField, context: Context) {
        context.coordinator.parent = self
        (field as? CaptureTextField)?.commandSubmit = submit
        if field.stringValue != text { field.stringValue = text }
        if let focusToken, context.coordinator.focusToken != focusToken {
            context.coordinator.focusToken = focusToken
            DispatchQueue.main.async {
                guard field.window?.isVisible == true else { return }
                field.window?.makeFirstResponder(field)
            }
        }
    }
    final class Coordinator: NSObject, NSTextFieldDelegate {
        var parent: CaptureField
        var focusToken: UUID?
        init(_ parent: CaptureField) { self.parent = parent }
        func controlTextDidChange(_ notification: Notification) {
            if let field = notification.object as? NSTextField { parent.text = field.stringValue }
        }
        func control(_ control: NSControl, textView: NSTextView, doCommandBy selector: Selector) -> Bool {
            guard !textView.hasMarkedText() else { return false }
            if selector == #selector(NSResponder.insertNewline(_:)) {
                let commandHeld = NSApp.currentEvent?.modifierFlags.contains(.command) == true
                if !parent.commandReturn || commandHeld { parent.submit() }
                return true
            }
            if selector == #selector(NSResponder.cancelOperation(_:)) { parent.cancel(); return true }
            return false
        }
    }
}

private final class CaptureTextField: NSTextField {
    var commandSubmit: (() -> Void)?
    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if event.keyCode == 36, event.modifierFlags.contains(.command),
           window?.firstResponder === currentEditor(),
           (currentEditor() as? NSTextView)?.hasMarkedText() != true {
            commandSubmit?()
            return true
        }
        return super.performKeyEquivalent(with: event)
    }
}

struct SettingsView: View {
    @Environment(\.colorScheme) private var colorScheme
    @ObservedObject var store: Store
    @ViewState private var loginEnabled = SMAppService.mainApp.status == .enabled
    @ViewState private var loginMessage: String? = nil
    var body: some View {
        Form {
            if store.showWelcome {
                Section {
                    VStack(alignment: .leading, spacing: 7) {
                        Text("欢迎使用 Aside").font(.headline)
                        Text("安装完成。Aside 常驻在屏幕顶部菜单栏的图钉图标里，不显示在 Dock。")
                            .foregroundStyle(.secondary)
                        Text("先在下方「全局快捷键」设置呼出快速记录的组合键（当前为 \(store.preferences.shortcutLabel)）。之后在任何应用里按下它，就能随手记一句。")
                            .foregroundStyle(.secondary)
                        HStack { Spacer(); Button("知道了") { store.showWelcome = false } }
                    }
                    .font(.callout)
                    .fixedSize(horizontal: false, vertical: true)
                }
            }
            Section("通用") {
                Toggle("窗口始终置顶", isOn: $store.preferences.alwaysOnTop)
                Stepper("最多置顶 \(store.preferences.pinCapacity) 条", value: Binding(
                    get: { store.preferences.pinCapacity },
                    set: { store.preferences.pinLimit = $0 }), in: 1...20)
                Text("调低数量不会移除已有置顶；可手动取消多余置顶。")
                    .font(.caption).foregroundStyle(.secondary)
                Toggle("登录时启动 Aside", isOn: Binding(get: { loginEnabled }, set: setLogin))
                if let loginMessage { Text(loginMessage).font(.caption).foregroundStyle(.secondary) }
                ShortcutRecorder(store: store)
                if let error = store.shortcutError {
                    Text(error).font(.caption).foregroundStyle(.red)
                }
                Picker("快速记录保存键", selection: Binding(
                    get: { store.preferences.commandReturnToSave == true },
                    set: { store.preferences.commandReturnToSave = $0 })) {
                    Text("Enter").tag(false)
                    Text("Command + Enter").tag(true)
                }
                Text("快捷输入居中出现，保存后自动消失，新记录默认不置顶。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("外观") {
                HStack {
                    Text("背景颜色")
                    Spacer()
                    PaletteButton(store: store).frame(width: 130, height: 28)
                }
                SavedColorShelf(store: store)
                Text("点击色块打开系统调色盘，可使用取色器吸取屏幕颜色。")
                    .font(.caption).foregroundStyle(.secondary)
                opacityRow("鼠标离开", value: $store.preferences.idleOpacity)
                    .disabled(!store.preferences.fadeOnLeave)
                opacityRow("交互时", value: $store.preferences.activeOpacity)
                opacityRow("快速输入", value: Binding(
                    get: { store.preferences.captureOpacity ?? 0.95 },
                    set: { store.preferences.captureOpacity = $0 }))
                Toggle("鼠标离开时淡化", isOn: $store.preferences.fadeOnLeave)
                Text("关闭淡化后，窗口固定使用「交互时」透明度。背景色 Alpha 与窗口透明度分别保存。")
                    .font(.caption).foregroundStyle(.secondary)
                AppearancePreview(store: store)
                Button("恢复默认外观") { store.resetAppearance() }
            }
            Section {
                Text("Aside · Capture it. Stay in flow.")
                    .font(.caption).foregroundStyle(.secondary)
                Text("记录仅保存在这台 Mac 上。拖动置顶事项可调整顺序；右键可完成或删除。")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section {
                Link(destination: URL(string: "https://github.com/savannahliz/aside")!) {
                    VStack(spacing: 5) {
                        if let url = Bundle.main.url(forResource: colorScheme == .dark ? "GitHub_Invertocat_White" : "GitHub_Invertocat_Black", withExtension: "png"),
                           let icon = NSImage(contentsOf: url) {
                            Image(nsImage: icon).resizable().scaledToFit().frame(width: 22, height: 22)
                        } else {
                            Image(systemName: "link").frame(width: 22, height: 22)
                        }
                        Text("GitHub 项目主页 ↗")
                            .font(.caption).foregroundStyle(.blue).underline()
                        Text("喜欢的话来点个⭐️支持作者吧，谢谢大家～")
                            .font(.caption2).foregroundStyle(.secondary)
                    }
                    .frame(maxWidth: .infinity)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("打开 Aside 的 GitHub 仓库")
            }
        }
        .formStyle(.grouped).padding(8).frame(width: 450, height: 720)
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didBecomeActiveNotification)) { _ in
            loginEnabled = SMAppService.mainApp.status == .enabled
        }
    }

    private func opacityRow(_ title: String, value: Binding<Double>) -> some View {
        HStack {
            Text(title).frame(width: 70, alignment: .leading)
            Slider(value: value, in: 0.15...1)
            Text("\(Int(value.wrappedValue * 100))%")
                .monospacedDigit().frame(width: 40, alignment: .trailing)
        }
    }

    private func setLogin(_ enabled: Bool) {
        do {
            if enabled { try SMAppService.mainApp.register() }
            else { try SMAppService.mainApp.unregister() }
            loginEnabled = SMAppService.mainApp.status == .enabled
            if SMAppService.mainApp.status == .requiresApproval {
                loginMessage = "请在系统设置的「登录项」中允许 Aside。"
                SMAppService.openSystemSettingsLoginItems()
            } else { loginMessage = nil }
        } catch {
            loginEnabled = SMAppService.mainApp.status == .enabled
            loginMessage = "未能更改登录启动：\(error.localizedDescription)"
        }
    }
}

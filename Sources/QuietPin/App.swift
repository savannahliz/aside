import AppKit
import SwiftUI
import Combine

final class FloatingPanel: NSPanel {
    var participatesAsMainWindow = true
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { participatesAsMainWindow }
}

@main
struct QuietPinApp {
    @MainActor static func main() {
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)
        app.run()
        withExtendedLifetime(delegate) {}
    }
}

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate {
    private(set) var store: Store!
    private var mainPanel: FloatingPanel!
    private var edgePanel: NSPanel!
    private var capturePanel: FloatingPanel!
    private var settingsWindow: NSWindow?
    private var statusItem: NSStatusItem!
    private let hotKey = HotKey()
    private var subscriptions = Set<AnyCancellable>()
    private var hovering = false
    private var changingMode = false
    private var priorShortcut = ""
    private var saveFrameWork: DispatchWorkItem?
    private var dockFrame: NSRect?
    private var dockHidden = false
    private var edgeTransitioning = false
    private var editingItem = false
    private var edgeMotionToken = 0
    private var edgeMotionTimer: Timer?
    private var dockTimer: Timer?
    private var userDragging = false
    // A note docked from a previous session starts visible and only hides after the pointer has
    // visited it once, so launching never leaves the user with nothing on screen.
    private var edgeArmed = true
    // Room for the window shadow so no part of it is left on screen when parked off the edge.
    private static let shadowClearance: CGFloat = 24
    private weak var captureInput: NSTextField?
    private var captureLocalMonitor: Any?
    private var captureGlobalMonitor: Any?
    private var closingCapture = false
    private var reopenCapture = false
    private var captureMotionTimer: Timer?
    private var motionEnabled: Bool {
        !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion &&
        !CommandLine.arguments.contains("--smoke-test") &&
        !CommandLine.arguments.contains("--edge-regression-test")
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        let smoke = CommandLine.arguments.contains("--smoke-test")
        let captureRegression = CommandLine.arguments.contains("--capture-regression-test")
        let edgeRegression = CommandLine.arguments.contains("--edge-regression-test")
        let motionRegression = CommandLine.arguments.contains("--motion-regression-test")
        let uiTest = CommandLine.arguments.contains("--ui-test")
        if smoke || uiTest || captureRegression || edgeRegression || motionRegression {
            let suite = uiTest ? "QuietPin.UITest" : "QuietPin.Smoke.\(UUID().uuidString)"
            store = Store(directory: FileManager.default.temporaryDirectory.appendingPathComponent(suite),
                          defaults: UserDefaults(suiteName: suite)!)
        } else { store = Store() }
        setupMenu()
        setupPanels()
        hotKey.action = { [weak self] in self?.toggleCapture() }
        store.$preferences.sink { [weak self] _ in
            DispatchQueue.main.async { self?.applyPreferences() }
        }.store(in: &subscriptions)
        store.$recordingShortcut.sink { [weak self] recording in
            guard let self else { return }
            if recording { self.hotKey.unregister(); self.priorShortcut = "" }
            else { DispatchQueue.main.async { self.applyPreferences() } }
        }.store(in: &subscriptions)
        store.$errorMessage.compactMap { $0 }.sink { [weak self] message in
            DispatchQueue.main.async {
                guard let self else { return }
                let alert = NSAlert()
                alert.messageText = "Aside 未能保存记录"
                alert.informativeText = message
                alert.alertStyle = .warning
                alert.runModal()
                self.store.errorMessage = nil
            }
        }.store(in: &subscriptions)
        applyPreferences()
        mainPanel.orderFrontRegardless()
        if store.preferences.dockEdge != nil { dockFrame = mainPanel.frame; edgeArmed = false }
        dockTimer = Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.pollEdge() }
        }
        if smoke { DispatchQueue.main.async { self.runSmokeTest() } }
        if captureRegression { DispatchQueue.main.async { self.runCaptureRegression() } }
        if edgeRegression { DispatchQueue.main.async { self.runEdgeRegression() } }
        if motionRegression { DispatchQueue.main.async { self.runMotionRegression() } }
        // First launch: open Settings with a welcome note so the user knows Aside is running and sets a shortcut.
        if store.showWelcome && !(smoke || uiTest || captureRegression || edgeRegression || motionRegression) {
            DispatchQueue.main.async { self.showSettings() }
        }
    }

    private func makePanel(rect: NSRect) -> FloatingPanel {
        let panel = FloatingPanel(contentRect: rect,
            styleMask: [.borderless, .resizable, .nonactivatingPanel],
            backing: .buffered, defer: false)
        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.standardWindowButton(.closeButton)?.isHidden = true
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.standardWindowButton(.zoomButton)?.isHidden = true
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.isMovableByWindowBackground = true
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.animationBehavior = .utilityWindow
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        return panel
    }

    private func setupPanels() {
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1400, height: 900)
        let defaultFrame = NSRect(x: screen.maxX - 390, y: screen.maxY - 530, width: 350, height: 460)
        mainPanel = makePanel(rect: defaultFrame)
        mainPanel.animationBehavior = .none
        mainPanel.title = "Aside Inbox"
        configureSizeLimits()
        mainPanel.delegate = self
        mainPanel.contentView = NSHostingView(rootView: InboxView(store: store,
            toggleCollapsed: { [weak self] in self?.toggleCollapsed() },
            expand: { [weak self] in self?.showInbox() },
            openSettings: { [weak self] in self?.showSettings() },
            minimize: { [weak self] in self?.hideInbox() },
            hoverChanged: { [weak self] inside in self?.hovering = inside; self?.updateOpacity() },
            editingChanged: { [weak self] editing in self?.editingItem = editing }))
        let saved = currentSavedFrame
        if let saved { mainPanel.setFrame(onScreen(NSRectFromString(saved)), display: false) }
        else if store.preferences.collapsed {
            let height: CGFloat = store.preferences.stripMode == true ? 28 : 160
            mainPanel.setFrame(NSRect(x: defaultFrame.minX, y: defaultFrame.maxY - height, width: 330, height: height), display: false)
        }
        edgePanel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 8, height: 100),
            styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        edgePanel.title = "Aside 边缘唤回条"
        edgePanel.isOpaque = false
        edgePanel.hasShadow = false
        edgePanel.hidesOnDeactivate = false
        edgePanel.isReleasedWhenClosed = false
        edgePanel.animationBehavior = .none
        edgePanel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        capturePanel = makePanel(rect: NSRect(x: 0, y: 0, width: 660, height: 64))
        capturePanel.title = "Aside 快速输入"
        capturePanel.styleMask.remove(.resizable)
        capturePanel.participatesAsMainWindow = false
        capturePanel.animationBehavior = .none
        capturePanel.level = .floating
        capturePanel.delegate = self
        capturePanel.contentView = NSHostingView(rootView: QuickCaptureView(store: store,
            close: { [weak self] in self?.closeCapture() }, inputReady: { [weak self] in self?.captureInput = $0 }))
    }

    private func setupMenu() {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let entries: [(String, Selector, String)] = [
            ("显示 Inbox", #selector(showInbox), ""),
            ("快速记录", #selector(toggleCapture), ""),
            ("仅显示置顶事项", #selector(showPins), ""),
            ("收为细条", #selector(showStrip), ""),
            ("最小化到菜单栏", #selector(hideInbox), ""),
            ("窗口置顶", #selector(toggleAlwaysOnTop), ""),
            ("设置…", #selector(showSettings), ","),
            ("退出 Aside", #selector(quit), "q")
        ]
        for (title, selector, key) in entries {
            let item = NSMenuItem(title: title, action: selector, keyEquivalent: key)
            item.target = self
            menu.addItem(item)
        }
        menu.delegate = self
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.button?.image = NSImage(systemSymbolName: "pin.circle", accessibilityDescription: "Aside")
        statusItem.button?.toolTip = "Aside · Capture it. Stay in flow."
        statusItem.button?.title = " Aside"
        statusItem.menu = menu
        let appMenu = NSMenu()
        let root = NSMenuItem()
        root.submenu = menu.copy() as? NSMenu
        root.submenu?.delegate = self
        appMenu.addItem(root)
        let editRoot = NSMenuItem(title: "编辑", action: nil, keyEquivalent: "")
        let edit = NSMenu(title: "编辑")
        for (title, selector, key) in [("撤销", Selector(("undo:")), "z"), ("剪切", #selector(NSText.cut(_:)), "x"),
                                       ("复制", #selector(NSText.copy(_:)), "c"), ("粘贴", #selector(NSText.paste(_:)), "v"),
                                       ("全选", #selector(NSText.selectAll(_:)), "a")] {
            edit.addItem(withTitle: title, action: selector, keyEquivalent: key)
        }
        editRoot.submenu = edit
        appMenu.addItem(editRoot)
        NSApp.mainMenu = appMenu
    }

    private func applyPreferences() {
        guard mainPanel != nil else { return }
        mainPanel.level = store.preferences.alwaysOnTop ? .floating : .normal
        edgePanel.level = mainPanel.level
        edgePanel.backgroundColor = store.preferences.background.nsColor
        capturePanel.alphaValue = min(1, max(0.15, store.preferences.captureOpacity ?? 0.95))
        updateOpacity()
        let shortcut = "\(store.preferences.customKeyCode ?? 49):\(store.preferences.customModifiers ?? UInt32(store.preferences.shortcutSafeIndex))"
        if !store.recordingShortcut && priorShortcut != shortcut {
            priorShortcut = shortcut
            let result = hotKey.register(preferences: store.preferences)
            store.shortcutError = result == noErr ? nil : "快捷键注册失败（\(result)），可能被其他应用占用。请选择另一组快捷键。"
        }
    }

    private func updateOpacity() {
        guard mainPanel != nil else { return }
        let prefs = store.preferences
        // The edge tab stays invisible: hovering the screen edge still reveals the note, but nothing is left on screen.
        edgePanel.alphaValue = 0
        guard !dockHidden else { return }
        if prefs.dockEdge != nil {
            // Keep a docked note fully readable until the instant it hides.
            // Fading the full window first leaves a visible ghost on screen.
            mainPanel.alphaValue = min(1, max(0.15, prefs.activeOpacity))
            return
        }
        let active = hovering || mainPanel.isKeyWindow || mainPanel.attachedSheet != nil
        let value = prefs.fadeOnLeave && !active ? prefs.idleOpacity : prefs.activeOpacity
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.18
            mainPanel.animator().alphaValue = min(1, max(0.15, value))
        }
    }

    @objc private func showInbox() {
        revealFromEdge(animated: false)
        if store.preferences.collapsed { changeMode(collapsed: false, strip: false) }
        mainPanel.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc private func hideInbox() { mainPanel.orderOut(nil); edgePanel.orderOut(nil) }

    @objc private func showPins() { changeMode(collapsed: true, strip: false); mainPanel.orderFrontRegardless() }
    @objc private func showStrip() { changeMode(collapsed: true, strip: true); mainPanel.orderFrontRegardless() }
    @objc private func toggleAlwaysOnTop() { store.preferences.alwaysOnTop.toggle() }

    func menuNeedsUpdate(_ menu: NSMenu) {
        guard store != nil, mainPanel != nil else { return }
        for item in menu.items {
            switch item.action {
            case #selector(toggleCapture): item.title = "快速记录    \(store.preferences.shortcutLabel)"
            case #selector(toggleAlwaysOnTop): item.state = store.preferences.alwaysOnTop ? .on : .off
            case #selector(showPins): item.state = store.preferences.collapsed && store.preferences.stripMode != true ? .on : .off
            case #selector(showStrip): item.state = store.preferences.stripMode == true ? .on : .off
            case #selector(hideInbox): item.isEnabled = mainPanel.isVisible || edgePanel.isVisible
            default: break
            }
        }
    }

    @objc private func toggleCollapsed() {
        if store.preferences.stripMode == true { changeMode(collapsed: false, strip: false) }
        else if store.preferences.collapsed { changeMode(collapsed: true, strip: true) }
        else { changeMode(collapsed: true, strip: false) }
    }

    private var currentSavedFrame: String? {
        if store.preferences.stripMode == true { return store.preferences.stripFrame }
        return store.preferences.collapsed ? store.preferences.collapsedFrame : store.preferences.expandedFrame
    }

    private func configureSizeLimits() {
        let strip = store.preferences.stripMode == true
        mainPanel.minSize = NSSize(width: strip ? 160 : 290, height: strip ? 28 : (store.preferences.collapsed ? 88 : 280))
        mainPanel.maxSize = NSSize(width: 10000, height: strip ? 28 : 10000)
    }

    private func changeMode(collapsed: Bool, strip: Bool) {
        revealFromEdge(animated: false)
        saveFrameWork?.cancel()
        rememberFrame()
        changingMode = true
        let old = mainPanel.frame
        store.preferences.collapsed = collapsed
        store.preferences.stripMode = strip
        configureSizeLimits()
        let size = currentSavedFrame.map { NSRectFromString($0).size } ?? NSSize(width: collapsed ? 330 : 350, height: strip ? 28 : (collapsed ? 160 : 460))
        let rect = NSRect(x: old.minX, y: old.maxY - size.height, width: size.width, height: size.height)
        mainPanel.setFrame(onScreen(rect), display: true)
        if store.preferences.dockEdge != nil { dockFrame = mainPanel.frame }
        changingMode = false
        rememberFrame()
    }

    @objc private func toggleCapture() {
        if closingCapture { reopenCapture = true; return }
        if capturePanel.isVisible { closeCapture(); return }
        store.captureSession = UUID()
        let point = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { $0.frame.contains(point) } ?? NSScreen.main
        if let screen {
            let visible = screen.visibleFrame
            let width = min(660, visible.width - 40)
            capturePanel.setFrame(NSRect(x: visible.midX - width / 2, y: visible.midY - 32,
                                        width: width, height: 64), display: false)
        }
        capturePanel.contentView?.layoutSubtreeIfNeeded()
        capturePanel.contentView?.displayIfNeeded()
        // This nonactivating panel borrows keyboard focus; activating the app
        // as well causes a visible foreground/Space transition before capture.
        let targetOpacity = min(1, max(0.15, store.preferences.captureOpacity ?? 0.95))
        capturePanel.alphaValue = motionEnabled ? targetOpacity * 0.75 : targetOpacity
        capturePanel.makeKeyAndOrderFront(nil)
        if let captureInput { capturePanel.makeFirstResponder(captureInput) }
        if motionEnabled { animateCaptureOpacity(to: targetOpacity, duration: 0.09, easeOut: true) {} }
        captureLocalMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak self] event in
            guard let self, self.capturePanel.isVisible else { return event }
            if event.type == .keyDown {
                if event.keyCode == 53 { self.closeCapture(); return nil }
            } else if event.window !== self.capturePanel {
                self.closeCapture()
            }
            return event
        }
        captureGlobalMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .rightMouseDown, .otherMouseDown]) { [weak self] _ in
            self?.closeCapture()
        }
    }

    private func closeCapture() {
        guard !closingCapture else { return }
        closingCapture = true
        if let captureLocalMonitor { NSEvent.removeMonitor(captureLocalMonitor); self.captureLocalMonitor = nil }
        if let captureGlobalMonitor { NSEvent.removeMonitor(captureGlobalMonitor); self.captureGlobalMonitor = nil }
        if motionEnabled && capturePanel.isVisible {
            animateCaptureOpacity(to: 0, duration: 0.07, easeOut: false) { [weak self] in
                guard let self else { return }
                self.capturePanel.orderOut(nil)
                self.closingCapture = false
                if self.reopenCapture { self.reopenCapture = false; self.toggleCapture() }
            }
        } else {
            capturePanel.orderOut(nil)
            closingCapture = false
            if reopenCapture { reopenCapture = false; toggleCapture() }
        }
    }

    private func animateCaptureOpacity(to target: CGFloat, duration: TimeInterval, easeOut: Bool,
                                       completion: @escaping @MainActor () -> Void) {
        captureMotionTimer?.invalidate()
        let initial = capturePanel.alphaValue
        let started = ProcessInfo.processInfo.systemUptime
        captureMotionTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self else { timer.invalidate(); return }
                let progress = min(1, (ProcessInfo.processInfo.systemUptime - started) / duration)
                let eased = easeOut ? 1 - pow(1 - progress, 3) : progress * progress
                self.capturePanel.alphaValue = initial + (target - initial) * eased
                if progress >= 1 {
                    timer.invalidate()
                    self.captureMotionTimer = nil
                    completion()
                }
            }
        }
    }

    @objc private func showSettings() {
        if settingsWindow == nil {
            let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 450, height: 720),
                styleMask: [.titled, .closable], backing: .buffered, defer: false)
            window.title = "Aside 设置"
            window.isReleasedWhenClosed = false
            window.contentView = NSHostingView(rootView: SettingsView(store: store))
            window.level = .floating
            window.center()
            settingsWindow = window
        }
        settingsWindow?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func onScreen(_ proposed: NSRect) -> NSRect {
        guard proposed.width.isFinite, proposed.height.isFinite, proposed.minX.isFinite, proposed.minY.isFinite else {
            return NSRect(x: 100, y: 100, width: 350, height: 460)
        }
        let screen = NSScreen.screens.max { a, b in
            let ia = a.visibleFrame.intersection(proposed), ib = b.visibleFrame.intersection(proposed)
            return (ia.isNull ? 0 : ia.width * ia.height) < (ib.isNull ? 0 : ib.width * ib.height)
        } ?? NSScreen.main
        guard let bounds = screen?.visibleFrame else { return proposed }
        let strip = store.preferences.stripMode == true
        let width = min(bounds.width, max(strip ? 160 : 290, proposed.width))
        let height = strip ? 28 : min(bounds.height, max(store.preferences.collapsed ? 88 : 280, proposed.height))
        return NSRect(x: min(max(proposed.minX, bounds.minX), bounds.maxX - width),
                      y: min(max(proposed.minY, bounds.minY), bounds.maxY - height), width: width, height: height)
    }

    private func rememberFrame() {
        guard !changingMode, let mainPanel else { return }
        let frame = NSStringFromRect(dockHidden ? (dockFrame ?? mainPanel.frame) : mainPanel.frame)
        if store.preferences.stripMode == true { store.preferences.stripFrame = frame }
        else if store.preferences.collapsed { store.preferences.collapsedFrame = frame }
        else { store.preferences.expandedFrame = frame }
    }

    func windowDidMove(_ notification: Notification) {
        guard notification.object as? NSWindow === mainPanel else { return }
        guard !changingMode, !dockHidden else { return }
        // The final move notification can arrive after the mouse is released.
        // App-owned frame changes are excluded by changingMode above.
        userDragging = true
        scheduleFrameSave()
    }
    func windowDidResize(_ notification: Notification) {
        if notification.object as? NSWindow === mainPanel { scheduleFrameSave() }
    }
    func windowDidBecomeKey(_ notification: Notification) { updateOpacity() }
    func windowDidResignKey(_ notification: Notification) {
        if notification.object as? NSWindow === capturePanel { closeCapture() }
        else { updateOpacity() }
    }
    func windowShouldClose(_ sender: NSWindow) -> Bool { sender.orderOut(nil); return false }

    private func scheduleFrameSave() {
        guard !changingMode else { return }
        saveFrameWork?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.rememberFrame() }
        saveFrameWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.25, execute: work)
    }

    private func pollEdge(pointer: NSPoint? = nil) {
        guard !edgeTransitioning || !dockHidden else { return }
        let pressing = NSEvent.pressedMouseButtons & 1 == 1
        if userDragging && !pressing {
            userDragging = false
            let frame = mainPanel.frame
            let bounds = mainPanel.screen?.visibleFrame ?? NSScreen.main!.visibleFrame
            if frame.minX <= bounds.minX + 18 {
                store.preferences.dockEdge = "left"
            } else if frame.maxX >= bounds.maxX - 18 {
                store.preferences.dockEdge = "right"
            } else {
                store.preferences.dockEdge = nil
                dockFrame = nil
            }
            if store.preferences.dockEdge != nil {
                var snapped = onScreen(frame)
                snapped.origin.x = store.preferences.dockEdge == "left" ? bounds.minX : bounds.maxX - snapped.width
                changingMode = true
                mainPanel.setFrame(snapped, display: true)
                changingMode = false
                dockFrame = snapped
                updateOpacity()
            }
            edgeArmed = true
            rememberFrame()
        }
        let visiblePanel: NSWindow = dockHidden ? edgePanel : mainPanel
        guard !pressing, !userDragging, visiblePanel.isVisible,
              store.preferences.dockEdge != nil, dockFrame != nil,
              mainPanel.attachedSheet == nil, !editingItem else { return }
        let hoverFrame = edgeTransitioning && !dockHidden ? (dockFrame ?? visiblePanel.frame) : visiblePanel.frame
        let hit = hoverFrame.insetBy(dx: -3, dy: -3).contains(pointer ?? NSEvent.mouseLocation)
        if hit {
            edgeArmed = true
            revealFromEdge()
        } else if edgeArmed {
            hideAtEdge()
        }
    }

    private func hideAtEdge(animated: Bool = true) {
        guard !dockHidden, let resting = dockFrame, let edge = store.preferences.dockEdge else { return }
        let bounds = NSScreen.screens.first(where: { $0.visibleFrame.intersects(resting) })?.visibleFrame
            ?? NSScreen.main!.visibleFrame
        changingMode = true
        saveFrameWork?.cancel()
        dockFrame = resting
        dockHidden = true
        edgeTransitioning = true
        edgeMotionToken += 1
        let token = edgeMotionToken
        // Keep the full-size backing store intact. Resizing the live hosting
        // window to 8px leaves stale content/shadows during compositing.
        let height = min(100, resting.height)
        edgePanel.setFrame(NSRect(x: edge == "left" ? bounds.minX : bounds.maxX - 8,
                                 y: resting.midY - height / 2, width: 8, height: height), display: true)
        let finish: @MainActor () -> Void = { [weak self] in
            guard let self, self.edgeMotionToken == token, self.dockHidden else { return }
            self.mainPanel.orderOut(nil)
            self.mainPanel.setFrame(self.dockFrame ?? resting, display: false)
            self.edgePanel.orderFrontRegardless()
            self.edgeTransitioning = false
            self.changingMode = false
            self.updateOpacity()
        }
        guard animated && motionEnabled else { finish(); return }
        var parked = mainPanel.frame
        // Slide fully off screen, shadow included. The invisible edge tab no longer covers an 8 pt
        // sliver, so stopping short left a ghost strip for the last frame before the panel hid.
        parked.origin.x = edge == "left" ? bounds.minX - parked.width - Self.shadowClearance : bounds.maxX + Self.shadowClearance
        animateEdge(toX: parked.minX, duration: 0.12, spring: false, fadeOut: true, completion: finish)
    }

    private func revealFromEdge(animated: Bool = true) {
        guard dockHidden, let frame = dockFrame else { return }
        changingMode = true
        dockHidden = false
        edgeTransitioning = true
        edgeMotionToken += 1
        let token = edgeMotionToken
        let target = onScreen(frame)
        let bounds = NSScreen.screens.first(where: { $0.visibleFrame.intersects(target) })?.visibleFrame
            ?? NSScreen.main!.visibleFrame
        var parked = target
        let left = store.preferences.dockEdge == "left"
        parked.origin.x = left ? bounds.minX - parked.width - Self.shadowClearance : bounds.maxX + Self.shadowClearance
        mainPanel.setFrame(animated && motionEnabled ? parked : target, display: true)
        mainPanel.contentView?.layoutSubtreeIfNeeded()
        mainPanel.contentView?.displayIfNeeded()
        mainPanel.alphaValue = store.preferences.activeOpacity
        mainPanel.orderFrontRegardless()
        mainPanel.invalidateShadow()
        edgePanel.orderOut(nil)
        let finish: @MainActor () -> Void = { [weak self] in
            guard let self, self.edgeMotionToken == token, !self.dockHidden else { return }
            self.mainPanel.setFrame(target, display: true)
            self.edgeTransitioning = false
            self.changingMode = false
            self.updateOpacity()
        }
        guard animated && motionEnabled else { finish(); return }
        animateEdge(toX: target.minX, duration: 0.30, spring: true, completion: finish)
    }

    private func animateEdge(toX target: CGFloat, duration: TimeInterval, spring: Bool, fadeOut: Bool = false,
                             completion: @escaping @MainActor () -> Void) {
        edgeMotionTimer?.invalidate()
        let initial = mainPanel.frame.minX
        let initialAlpha = mainPanel.alphaValue
        let started = ProcessInfo.processInfo.systemUptime
        edgeMotionTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] timer in
            MainActor.assumeIsolated {
                guard let self else { timer.invalidate(); return }
                let progress = min(1, (ProcessInfo.processInfo.systemUptime - started) / duration)
                // Hiding accelerates away (ease-in): with ease-out the note crept across its last few
                // visible points at the slowest speed, which read as a ghost strip at the screen edge.
                let eased = spring ? 1 - exp(-9 * progress) * cos(8 * progress) : progress * progress
                var frame = self.mainPanel.frame
                frame.origin.x = initial + (target - initial) * eased
                self.mainPanel.setFrame(frame, display: true)
                // At 60 Hz a 120 ms slide has ~7 frames, so the last visible frame still shows a readable
                // chunk of the note that then vanishes at once. Fading while sliding leaves it nearly clear.
                if fadeOut { self.mainPanel.alphaValue = initialAlpha * pow(1 - progress, 1.5) }
                if progress >= 1 {
                    timer.invalidate()
                    self.edgeMotionTimer = nil
                    completion()
                }
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        dockTimer?.invalidate()
        edgeMotionTimer?.invalidate()
        captureMotionTimer?.invalidate()
        rememberFrame()
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { showInbox(); return true }
    @objc private func quit() { NSApp.terminate(nil) }

    private func runEdgeRegression() {
        let bounds = NSScreen.main!.visibleFrame
        let outside = NSPoint(x: bounds.midX, y: bounds.minY + 5)
        var passed = true
        for (mode, collapsed, strip) in [("inbox", false, false), ("pins", true, false), ("strip", true, true)] {
            changeMode(collapsed: collapsed, strip: strip)
            for (name, expected, offset) in [("left-near", "left", CGFloat(12)), ("left-over", "left", -120),
                                             ("right-near", "right", -12), ("right-over", "right", 120),
                                             ("detach", "", 0)] {
                revealFromEdge()
                var frame = mainPanel.frame
                frame.origin.y = bounds.midY - frame.height / 2
                frame.origin.x = expected == "left" ? bounds.minX + offset :
                    (expected == "right" ? bounds.maxX - frame.width + offset : bounds.midX - frame.width / 2)
                changingMode = true
                mainPanel.setFrame(frame, display: true)
                changingMode = false
                userDragging = false
                // AppKit may deliver the move notification after mouse release.
                windowDidMove(Notification(name: NSWindow.didMoveNotification, object: mainPanel))
                let hoverPoint = NSPoint(x: expected == "left" ? bounds.minX + 20 :
                    (expected == "right" ? bounds.maxX - 20 : bounds.midX), y: frame.midY)
                pollEdge(pointer: hoverPoint)
                let detected = store.preferences.dockEdge == (expected.isEmpty ? nil : expected)
                let noGhost = expected.isEmpty || (!dockHidden &&
                    abs(mainPanel.alphaValue - store.preferences.activeOpacity) < 0.01)
                pollEdge(pointer: outside)
                let hidden = expected.isEmpty ? !dockHidden : dockHidden && edgePanel.isVisible && edgePanel.frame.width == 8
                let noResize = mainPanel.frame.size == frame.size && (expected.isEmpty || !mainPanel.isVisible)
                let saved = currentSavedFrame.map { NSRectFromString($0) }
                let preserved = saved?.size == frame.size
                let tab = dockHidden ? edgePanel.frame : mainPanel.frame
                pollEdge(pointer: NSPoint(x: tab.midX, y: tab.midY))
                let restored = !dockHidden && mainPanel.isVisible && !edgePanel.isVisible && mainPanel.frame.size == frame.size
                let ok = detected && noGhost && hidden && preserved && restored && noResize
                passed = passed && ok
                print("edge \(mode)/\(name): detected=\(detected), noGhost=\(noGhost), hiddenImmediately=\(hidden), savedSize=\(preserved), restored=\(restored), noLiveResize=\(noResize)")
            }
        }
        print("Edge regression: \(passed ? "PASS" : "FAIL")")
        fflush(stdout)
        exit(passed ? 0 : 1)
    }

    private func runSmokeTest() {
        let added = store.add("Smoke test", pinned: true)
        let before = store.preferences.collapsed
        toggleCollapsed()
        let collapsed = store.preferences.collapsed != before
        toggleCollapsed()
        let strip = store.preferences.stripMode == true && mainPanel.frame.height <= 30
        toggleCollapsed()
        store.preferences.dockEdge = "right"
        dockFrame = mainPanel.frame
        hideAtEdge()
        let edgeHidden = dockHidden && !mainPanel.isVisible && edgePanel.isVisible && edgePanel.frame.width <= 8
        revealFromEdge()
        let edgeRestored = !dockHidden && mainPanel.frame.width >= 290
        store.preferences.dockEdge = nil
        toggleCapture()
        let captureVisible = capturePanel.isVisible && capturePanel.frame.height == 64
        closeCapture()
        showSettings()
        let passed = added && collapsed && strip && edgeHidden && edgeRestored && captureVisible && mainPanel.level == .floating && store.shortcutError == nil
        print("Aside smoke: \(passed ? "PASS" : "FAIL") — save=\(added), pins=\(collapsed), strip=\(strip), edgeHide=\(edgeHidden), edgeRestore=\(edgeRestored), capture=\(captureVisible), hotkey=\(store.shortcutError == nil)")
        fflush(stdout)
        NSApp.terminate(nil)
    }

    private func runCaptureRegression() {
        let foreground = NSWorkspace.shared.frontmostApplication?.processIdentifier
        var passed = true
        func cycle(_ remaining: Int) {
            self.toggleCapture()
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                let sameApp = NSWorkspace.shared.frontmostApplication?.processIdentifier == foreground
                let focused = self.capturePanel.isKeyWindow && self.capturePanel.firstResponder is NSTextView
                let shown = self.capturePanel.isVisible && self.capturePanel.frame.height == 64
                passed = passed && sameApp && focused && shown
                print("capture: foregroundUnchanged=\(sameApp), editorFocused=\(focused), visible=\(shown)")
                self.closeCapture()
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.2) {
                    let restored = !self.capturePanel.isVisible && NSWorkspace.shared.frontmostApplication?.processIdentifier == foreground
                    passed = passed && restored
                    if remaining > 1 { cycle(remaining - 1) }
                    else {
                        print("Capture regression: \(passed ? "PASS" : "FAIL") — three open/close cycles without app switching")
                        fflush(stdout)
                        if !passed { exit(1) }
                        NSApp.terminate(nil)
                    }
                }
            }
        }
        cycle(3)
    }

    private func runMotionRegression() {
        dockTimer?.invalidate()
        let bounds = NSScreen.main!.visibleFrame
        let frame = NSRect(x: bounds.maxX - 350, y: bounds.midY - 200, width: 350, height: 400)
        changingMode = true
        mainPanel.setFrame(frame, display: true)
        changingMode = false
        store.preferences.dockEdge = "right"
        dockFrame = frame
        hideAtEdge()
        let movingOut = dockHidden && edgeTransitioning && mainPanel.isVisible && mainPanel.frame.size == frame.size
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.30) {
            let hidden = self.dockHidden && !self.edgeTransitioning && !self.mainPanel.isVisible &&
                self.edgePanel.isVisible && self.mainPanel.frame.size == frame.size
            self.revealFromEdge()
            let movingIn = !self.dockHidden && self.edgeTransitioning && self.mainPanel.isVisible &&
                self.mainPanel.frame.size == frame.size
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.04) {
                self.pollEdge(pointer: NSPoint(x: bounds.midX, y: bounds.minY + 5))
                let interrupted = self.dockHidden && self.edgeTransitioning
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) {
                    let quickHidden = self.dockHidden && !self.edgeTransitioning && !self.mainPanel.isVisible
                    self.revealFromEdge()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 1.00) {
                let actual = self.mainPanel.frame
                let frameRestored = abs(actual.minX - frame.minX) <= 1 && abs(actual.minY - frame.minY) <= 1 &&
                    abs(actual.width - frame.width) <= 1 && abs(actual.height - frame.height) <= 1
                let revealed = !self.dockHidden && !self.edgeTransitioning && self.mainPanel.isVisible &&
                    !self.edgePanel.isVisible && frameRestored
                if !revealed {
                    print("reveal diagnostic: hidden=\(self.dockHidden), transitioning=\(self.edgeTransitioning), main=\(self.mainPanel.isVisible), edge=\(self.edgePanel.isVisible), frame=\(NSStringFromRect(self.mainPanel.frame)), expected=\(NSStringFromRect(frame))")
                }
                self.toggleCapture()
                let startingOpacity = min(1, max(0.15, self.store.preferences.captureOpacity ?? 0.95)) * 0.75
                let fadedStart = self.capturePanel.isVisible && abs(self.capturePanel.alphaValue - startingOpacity) < 0.01
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.22) {
                    let captureShown = self.capturePanel.isVisible && self.capturePanel.isKeyWindow &&
                        abs(self.capturePanel.alphaValue - (self.store.preferences.captureOpacity ?? 0.95)) < 0.01
                    if !captureShown {
                        print("capture diagnostic: visible=\(self.capturePanel.isVisible), key=\(self.capturePanel.isKeyWindow), alpha=\(self.capturePanel.alphaValue), closing=\(self.closingCapture)")
                    }
                    self.closeCapture()
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.20) {
                        let captureHidden = !self.capturePanel.isVisible && !self.closingCapture
                        let passed = movingOut && hidden && movingIn && interrupted && quickHidden && revealed && fadedStart && captureShown && captureHidden
                        print("Motion regression: \(passed ? "PASS" : "FAIL") — out=\(movingOut), hidden=\(hidden), in=\(movingIn), interrupted=\(interrupted), quickHidden=\(quickHidden), revealed=\(revealed), faded=\(fadedStart), capture=\(captureShown), closed=\(captureHidden)")
                        fflush(stdout)
                        exit(passed ? 0 : 1)
                    }
                }
                    }
                }
            }
        }
    }
}

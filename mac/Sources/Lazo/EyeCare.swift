import AppKit
import SwiftUI

/// Descanso Visual: regla 20-20-20 (cada 20 min, 20 s mirando lejos) y pausa activa (cada 55 min, 5 min).
/// Cuenta la jornada del día, se reinicia a medianoche y conserva lo que faltaba al reiniciar.
@MainActor
final class EyeCare: ObservableObject {
    static let shared = EyeCare()
    static let microInterval = 20 * 60, microDuration = 20
    static let activeInterval = 55 * 60, activeDuration = 5 * 60

    enum Style: String { case notice, eyes }
    enum Clock: String { case digital, analog }

    @Published var toMicro = EyeCare.microInterval
    @Published var toActive = EyeCare.activeInterval
    @Published var workToday = 0
    @Published var paused = false { didSet { save() } }
    @Published var sound = true { didSet { save() } }
    @Published var style: Style = .notice { didSet { save() } }
    @Published var clock: Clock = .digital { didSet { save() } }
    private var microDue = Date()
    private var activeDue = Date()
    private var day = Calendar.current.startOfDay(for: Date())
    private var alertOpen = false
    private var timer: Timer?
    private let d = UserDefaults.standard

    func start() {
        load()
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { _ in Task { @MainActor in EyeCare.shared.tick() } }
    }

    private func tick() {
        let today = Calendar.current.startOfDay(for: Date())
        if today != day { day = today; workToday = 0 }
        if paused || alertOpen || CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: .null) > 300 {
            // En pausa, con un aviso abierto o sin actividad: los contadores no avanzan.
            microDue = microDue.addingTimeInterval(1)
            activeDue = activeDue.addingTimeInterval(1)
        } else {
            workToday += 1
        }
        toMicro = max(0, Int(microDue.timeIntervalSinceNow.rounded()))
        toActive = max(0, Int(activeDue.timeIntervalSinceNow.rounded()))
        if workToday % 60 == 0 { save() }
        if alertOpen || paused { return }
        if toActive <= 0 { show(.active) } else if toMicro <= 0 { show(.micro) }
    }

    enum Break { case micro, active }

    func trigger(_ kind: Break) { show(kind) }

    private func show(_ kind: Break) {
        alertOpen = true
        if sound { NSSound(named: "Glass")?.play() }
        let duration = kind == .micro ? EyeCare.microDuration : EyeCare.activeDuration
        if kind == .micro && style == .eyes { EyesWindow.show(seconds: duration) { self.complete(kind) } }
        else { BreakPanel.show(kind, seconds: duration) { done in done ? self.complete(kind) : self.snooze(kind) } }
    }

    func complete(_ kind: Break) {
        alertOpen = false
        microDue = Date().addingTimeInterval(TimeInterval(EyeCare.microInterval))
        if kind == .active { activeDue = Date().addingTimeInterval(TimeInterval(EyeCare.activeInterval)) }
        save()
    }

    func snooze(_ kind: Break) {
        alertOpen = false
        if kind == .micro { microDue = Date().addingTimeInterval(300) } else { activeDue = Date().addingTimeInterval(300) }
        save()
    }

    private func load() {
        paused = d.bool(forKey: "eye.paused")
        sound = d.object(forKey: "eye.sound") as? Bool ?? true
        style = Style(rawValue: d.string(forKey: "eye.style") ?? "") ?? .notice
        clock = Clock(rawValue: d.string(forKey: "eye.clock") ?? "") ?? .digital
        let savedDay = d.object(forKey: "eye.day") as? Date ?? .distantPast
        workToday = Calendar.current.isDate(savedDay, inSameDayAs: Date()) ? d.integer(forKey: "eye.work") : 0
        let micro = d.object(forKey: "eye.micro") as? Int ?? EyeCare.microInterval
        let active = d.object(forKey: "eye.active") as? Int ?? EyeCare.activeInterval
        microDue = Date().addingTimeInterval(TimeInterval(max(0, micro)))
        activeDue = Date().addingTimeInterval(TimeInterval(max(0, active)))
    }

    private func save() {
        d.set(paused, forKey: "eye.paused")
        d.set(sound, forKey: "eye.sound")
        d.set(style.rawValue, forKey: "eye.style")
        d.set(clock.rawValue, forKey: "eye.clock")
        d.set(Date(), forKey: "eye.day")
        d.set(workToday, forKey: "eye.work")
        d.set(max(0, Int(microDue.timeIntervalSinceNow)), forKey: "eye.micro")
        d.set(max(0, Int(activeDue.timeIntervalSinceNow)), forKey: "eye.active")
    }

    static func format(_ seconds: Int) -> String {
        seconds >= 3600 ? String(format: "%d h %02d min", seconds / 3600, seconds % 3600 / 60) : String(format: "%d:%02d", seconds / 60, seconds % 60)
    }
}

struct EyeCareView: View {
    @ObservedObject var state: LauncherState
    @ObservedObject var care = EyeCare.shared
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: "Descanso Visual", state: state)
            HStack(spacing: 14) {
                if care.clock == .analog { AnalogClock().frame(width: 74, height: 74) }
                else {
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        Text(context.date.formatted(date: .omitted, time: .shortened)).font(.system(size: 34, weight: .semibold, design: .rounded)).monospacedDigit()
                    }
                }
                VStack(alignment: .leading, spacing: 4) {
                    Text("Jornada de hoy: " + EyeCare.format(care.workToday)).font(.lazo(12.5, .medium))
                    Text(care.paused ? "En pausa" : "Próximo descanso en " + EyeCare.format(care.toMicro)).font(.lazo(12)).opacity(0.7)
                    Text("Pausa activa en " + EyeCare.format(care.toActive)).font(.lazo(12)).opacity(0.7)
                }
            }
            .padding(12).frame(maxWidth: .infinity, alignment: .leading).surface(16)
            HStack(spacing: 8) {
                PillButton(title: "Descansar 20 s") { care.trigger(.micro) }
                PillButton(title: "Pausa activa") { care.trigger(.active) }
                PillButton(title: care.paused ? "Reanudar" : "Pausar", primary: care.paused) { care.paused.toggle() }
            }
            SectionLabel(text: "Aviso de 20 segundos")
            Segmented(options: [(EyeCare.Style.notice, "Notificación"), (.eyes, "Ojos en pantalla")], selection: $care.style)
            SectionLabel(text: "Reloj")
            Segmented(options: [(EyeCare.Clock.digital, "Digital"), (.analog, "Análogo")], selection: $care.clock)
            Toggle("Sonido", isOn: $care.sound).toggleStyle(.switch).font(.lazo(12.5))
            Text("Cada 20 minutos, mira algo a 6 metros durante 20 segundos. Cada 55 minutos, levántate 5 minutos.")
                .font(.lazo(11)).opacity(0.6).fixedSize(horizontal: false, vertical: true)
        }
        .panelFrame(360)
    }
}

struct AnalogClock: View {
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        TimelineView(.periodic(from: .now, by: 1)) { context in
            Canvas { g, size in
                let c = CGPoint(x: size.width / 2, y: size.height / 2), r = min(size.width, size.height) / 2 - 2
                g.stroke(Path(ellipseIn: CGRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2)), with: .color(prefs.palette.line), lineWidth: 2)
                let parts = Calendar.current.dateComponents([.hour, .minute, .second], from: context.date)
                func hand(_ fraction: Double, _ length: CGFloat, _ width: CGFloat, _ color: Color) {
                    let angle = fraction * 2 * .pi - .pi / 2
                    var path = Path()
                    path.move(to: c)
                    path.addLine(to: CGPoint(x: c.x + cos(angle) * length, y: c.y + sin(angle) * length))
                    g.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: width, lineCap: .round))
                }
                let h = Double(parts.hour! % 12) + Double(parts.minute!) / 60, m = Double(parts.minute!) + Double(parts.second!) / 60
                hand(h / 12, r * 0.5, 3, prefs.palette.ink)
                hand(m / 60, r * 0.75, 2, prefs.palette.ink)
                hand(Double(parts.second!) / 60, r * 0.82, 1, prefs.accentColor)
            }
        }
    }
}

/// Aviso de descanso abajo a la derecha con cuenta atrás.
enum BreakPanel {
    @MainActor static func show(_ kind: EyeCare.Break, seconds: Int, done: @escaping (Bool) -> Void) {
        var panel: FloatingPanel!
        var finished = false
        let model = Countdown(total: seconds)
        func finish(_ value: Bool) {
            guard !finished else { return }
            finished = true
            model.stop()
            panel.fadeOut()
            done(value)
        }
        let view = Card {
            VStack(alignment: .leading, spacing: 8) {
                Text(kind == .micro ? "Descansa la vista" : "Pausa activa").font(.lazo(14, .semibold))
                Text(kind == .micro ? "Mira algo lejano, a unos 6 metros, durante 20 segundos." : "Levántate, estira el cuerpo y camina un poco.")
                    .font(.lazo(12)).opacity(0.75).fixedSize(horizontal: false, vertical: true)
                CountdownView(model: model)
                HStack {
                    Spacer()
                    PillButton(title: "En 5 min") { finish(false) }
                    PillButton(title: "Listo", primary: true) { finish(true) }
                }
            }
        }
        panel = FloatingPanel(view, width: 340)
        FloatingPanel.Stack.add(panel)
        panel.fadeIn()
        model.onDone = { finish(true) }
        model.start()
    }
}

final class Countdown: ObservableObject {
    let total: Int
    @Published var remaining: Int
    var onDone: (() -> Void)?
    private var timer: Timer?

    init(total: Int) { self.total = total; remaining = total }

    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            guard let self else { return }
            self.remaining -= 1
            if self.remaining <= 0 { self.stop(); NSSound(named: "Hero")?.play(); self.onDone?() }
        }
    }

    func stop() { timer?.invalidate() }
}

struct CountdownView: View {
    @ObservedObject var model: Countdown
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(EyeCare.format(model.remaining)).font(.system(size: 20, weight: .semibold, design: .rounded)).monospacedDigit()
            ProgressView(value: Double(model.total - model.remaining), total: Double(model.total)).tint(prefs.accentColor)
        }
    }
}

/// Ojos a pantalla completa que miran a lo lejos durante el descanso de 20 segundos.
enum EyesWindow {
    @MainActor static func show(seconds: Int, done: @escaping () -> Void) {
        let screen = NSScreen.screens.first { NSMouseInRect(NSEvent.mouseLocation, $0.frame, false) } ?? NSScreen.main!
        let window = NSWindow(contentRect: screen.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.level = .screenSaver
        window.backgroundColor = .clear
        window.isOpaque = false
        window.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let model = Countdown(total: seconds)
        var closed = false
        func close() {
            guard !closed else { return }
            closed = true
            model.stop()
            NSAnimationContext.runAnimationGroup({ $0.duration = 0.3; window.animator().alphaValue = 0 }) { window.orderOut(nil) }
            done()
        }
        window.contentView = NSHostingView(rootView: EyesView(model: model, close: close))
        window.alphaValue = 0
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        NSAnimationContext.runAnimationGroup { $0.duration = 0.4; window.animator().alphaValue = 1 }
        model.onDone = close
        model.start()
    }
}

private struct EyesView: View {
    @ObservedObject var model: Countdown
    let close: () -> Void
    @ObservedObject var prefs = Prefs.shared

    var body: some View {
        ZStack {
            prefs.palette.shell2.opacity(0.94).ignoresSafeArea()
            VStack(spacing: 28) {
                TimelineView(.animation) { context in
                    let look = sin(context.date.timeIntervalSinceReferenceDate * 0.7) * 0.35
                    HStack(spacing: 40) { eye(look); eye(look) }
                }
                Text("Mira a lo lejos").font(.lazo(28, .semibold)).foregroundStyle(prefs.palette.ink)
                Text(EyeCare.format(model.remaining)).font(.system(size: 22, weight: .medium, design: .rounded)).monospacedDigit().foregroundStyle(prefs.palette.muted)
                PillButton(title: "Terminar", action: close)
            }
        }
        .onExitCommand(perform: close)
    }

    private func eye(_ look: Double) -> some View {
        ZStack {
            Ellipse().fill(Color.white).frame(width: 150, height: 96)
            Circle().fill(prefs.palette.ink).frame(width: 46, height: 46).offset(x: look * 50)
        }
    }
}

import CoreMedia
import CoreVideo
import Foundation
import VideoToolbox

/// Medición de latencia de codificación: el codificador actual (JPEG por bloques) contra H.264 por hardware de baja latencia.
/// Usa fotogramas sintéticos con movimiento, así que no necesita permiso de grabación de pantalla.
enum VideoBench {
    static func run(width: Int, height: Int, frames: Int) {
        print("Banco de video \(width)×\(height), \(frames) fotogramas")
        let buffers = (0..<8).map { makeFrame(width, height, phase: $0) }

        // 1. JPEG por bloques (lo que usa Lazo hoy).
        let tiles = TileEncoder()
        var jpeg: [Double] = [], bytes = 0
        for i in 0..<frames {
            let t0 = DispatchTime.now()
            let out = tiles.encode(buffers[i % buffers.count], key: i == 0)
            jpeg.append(ms(since: t0))
            bytes += out.reduce(0) { $0 + $1.jpeg.count }
        }
        report("JPEG por bloques (CPU)", jpeg, bytes, frames)

        // 2. H.264 por hardware: codificar + decodificar.
        guard let h264 = H264Pipe(width: width, height: height) else { print("No se pudo crear la sesión H.264"); return }
        var total: [Double] = [], encodeOnly: [Double] = [], size = 0
        for i in 0..<frames {
            let r = h264.roundTrip(buffers[i % buffers.count], key: i == 0)
            if i > 5 { total.append(r.total); encodeOnly.append(r.encode) }
            size += r.bytes
        }
        report("H.264 hardware: codificar", encodeOnly, size, frames)
        report("H.264 hardware: codificar + decodificar", total, size, frames)
        print("Codificador por hardware: \(h264.usesHardware ? "sí" : "no (software)")")
    }

    private static func ms(since t: DispatchTime) -> Double { Double(DispatchTime.now().uptimeNanoseconds - t.uptimeNanoseconds) / 1e6 }

    private static func report(_ name: String, _ samples: [Double], _ bytes: Int, _ frames: Int) {
        let sorted = samples.sorted()
        let avg = samples.reduce(0, +) / Double(max(1, samples.count))
        let p95 = sorted.isEmpty ? 0 : sorted[min(sorted.count - 1, Int(Double(sorted.count) * 0.95))]
        let mbps = Double(bytes) * 8 / 1e6 / (Double(frames) / 60)
        print(String(format: "  %@: media %.1f ms · p95 %.1f ms · ~%.0f Mb/s a 60 fps", name, avg, p95, mbps))
    }

    /// Escritorio simulado: fondo liso, texto-barras y una ventana que se mueve y cambia por fase.
    private static func makeFrame(_ w: Int, _ h: Int, phase: Int) -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(nil, w, h, kCVPixelFormatType_32BGRA, [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &buffer)
        let pb = buffer!
        CVPixelBufferLockBaseAddress(pb, [])
        let base = CVPixelBufferGetBaseAddress(pb)!.assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(pb)
        var seed = UInt32(12345 + phase)
        for y in 0..<h {
            let row = base + y * stride
            for x in 0..<w {
                var v: UInt8 = 238
                if y % 22 < 3 && x > 80 && x < w / 2 && (x / 7 + y / 22) % 3 != 0 { v = 40 }
                let wx = 400 + phase * 90, wy = 250 + phase * 40
                if x >= wx && x < wx + 900 && y >= wy && y < wy + 600 {
                    seed = seed &* 1664525 &+ 1013904223
                    v = UInt8(truncatingIfNeeded: 120 + Int((seed >> 24) & 0x3F) + (x + y + phase * 13) % 50)
                }
                row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = 255
            }
        }
        CVPixelBufferUnlockBaseAddress(pb, [])
        return pb
    }
}

/// Codificador y decodificador H.264 en el mismo proceso para medir el viaje completo.
final class H264Pipe {
    private var encoder: VTCompressionSession?
    private var decoder: VTDecompressionSession?
    private var encodedAt = DispatchTime.now()
    private var lastBytes = 0
    private var encodeMs = 0.0
    private var decoded = DispatchSemaphore(value: 0)
    private(set) var hardware = false
    private let width: Int, height: Int

    init?(width: Int, height: Int) {
        self.width = width; self.height = height
        let spec: [CFString: Any] = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true]
        var session: VTCompressionSession?
        let status = VTCompressionSessionCreate(allocator: nil, width: Int32(width), height: Int32(height), codecType: kCMVideoCodecType_H264,
                                                encoderSpecification: spec as CFDictionary, imageBufferAttributes: nil, compressedDataAllocator: nil,
                                                outputCallback: nil, refcon: nil, compressionSessionOut: &session)
        guard status == noErr, let session else { return nil }
        encoder = session
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel, value: kVTProfileLevel_H264_High_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ExpectedFrameRate, value: 60 as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: 40_000_000 as CFNumber)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval, value: 240 as CFNumber)
        VTCompressionSessionPrepareToEncodeFrames(session)
        var using: CFTypeRef?
        VTSessionCopyProperty(session, key: kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder, allocator: nil, valueOut: &using)
        hardware = (using as? Bool) ?? false
    }

    deinit {
        if let encoder { VTCompressionSessionInvalidate(encoder) }
        if let decoder { VTDecompressionSessionInvalidate(decoder) }
    }

    /// Se consulta después de codificar: antes del primer fotograma la sesión puede no haber elegido codificador.
    var usesHardware: Bool {
        guard let encoder else { return false }
        var using: CFTypeRef?
        VTSessionCopyProperty(encoder, key: kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder, allocator: nil, valueOut: &using)
        return (using as? Bool) ?? false
    }

    func roundTrip(_ buffer: CVPixelBuffer, key: Bool) -> (total: Double, encode: Double, bytes: Int) {
        guard let encoder else { return (0, 0, 0) }
        let start = DispatchTime.now()
        let props: CFDictionary? = key ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary : nil
        var sample: CMSampleBuffer?
        let done = DispatchSemaphore(value: 0)
        VTCompressionSessionEncodeFrame(encoder, imageBuffer: buffer, presentationTimeStamp: CMTime(value: Int64(start.uptimeNanoseconds / 1000), timescale: 1_000_000),
                                        duration: .invalid, frameProperties: props, infoFlagsOut: nil) { _, _, buf in
            sample = buf
            done.signal()
        }
        VTCompressionSessionCompleteFrames(encoder, untilPresentationTimeStamp: .invalid)
        done.wait()
        let encodeTime = Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1e6
        guard let sample else { return (0, encodeTime, 0) }
        let size = CMSampleBufferGetTotalSampleSize(sample)
        if decoder == nil, let format = CMSampleBufferGetFormatDescription(sample) {
            var created: VTDecompressionSession?
            VTDecompressionSessionCreate(allocator: nil, formatDescription: format, decoderSpecification: nil,
                                         imageBufferAttributes: [kCVPixelBufferPixelFormatTypeKey: kCVPixelFormatType_32BGRA] as CFDictionary,
                                         outputCallback: nil, decompressionSessionOut: &created)
            decoder = created
        }
        if let decoder {
            let flags = VTDecodeFrameFlags._EnableAsynchronousDecompression
            VTDecompressionSessionDecodeFrame(decoder, sampleBuffer: sample, flags: flags, infoFlagsOut: nil) { _, _, _, _, _ in self.decoded.signal() }
            VTDecompressionSessionWaitForAsynchronousFrames(decoder)
            _ = decoded.wait(timeout: .now() + 1)
        }
        return (Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1e6, encodeTime, size)
    }
}

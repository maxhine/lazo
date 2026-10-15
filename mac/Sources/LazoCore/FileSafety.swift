import Foundation
import ImageIO
import UniformTypeIdentifiers

/// Marca lo recibido como descargado (com.apple.quarantine), igual que Safari:
/// Gatekeeper revisa apps, instaladores y scripts antes de abrirlos.
public enum Quarantine {
    public static func apply(_ url: URL, sender: String) {
        let value = String(format: "0081;%08x;Lazo;%@", Int(Date().timeIntervalSince1970), UUID().uuidString)
        _ = value.withCString { pointer in
            setxattr(url.path, "com.apple.quarantine", pointer, strlen(pointer), 0, 0)
        }
    }

    /// Tipos que ejecutan código al abrirse: se pide confirmación y nunca se abren con un clic.
    public static func isRisky(_ url: URL) -> Bool {
        let risky: Set<String> = ["app", "pkg", "mpkg", "command", "sh", "zsh", "bash", "tool", "terminal", "workflow",
                                  "scpt", "scptd", "applescript", "jar", "dmg", "iso", "py", "rb", "pl", "js", "action",
                                  "prefpane", "saver", "plugin", "kext", "webloc", "inetloc", "fileloc",
                                  "exe", "bat", "cmd", "ps1", "vbs", "msi", "lnk", "url", "reg", "hta", "scr",
                                  "docm", "xlsm", "pptm"]
        return risky.contains(url.pathExtension.lowercased())
    }
}

/// Miniatura JPEG (≤ 160 px, ≤ 48 000 bytes) para la alerta de recepción.
public enum ImagePreview {
    public static func jpeg(for url: URL) -> Data? {
        let ext = url.pathExtension.lowercased()
        guard ["jpg", "jpeg", "png", "bmp", "gif", "tif", "tiff", "heic", "webp"].contains(ext),
              let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize, size <= 25 * 1024 * 1024,
              let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                        kCGImageSourceThumbnailMaxPixelSize: 160,
                                        kCGImageSourceCreateThumbnailWithTransform: true]
        guard let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
        return encode(thumb, quality: 0.68).flatMap { $0.count <= 48000 ? $0 : nil }
    }

    public static func encode(_ image: CGImage, quality: Double) -> Data? {
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(output, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(destination, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }
}

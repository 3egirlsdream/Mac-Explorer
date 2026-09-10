import AppKit
import WebKit
import ImageIO
import UniformTypeIdentifiers

func fail(_ message: String) -> Never {
    fputs(message + "\n", stderr)
    exit(1)
}

func printInfo() -> NSPrintInfo {
    let info = NSPrintInfo.shared.copy() as! NSPrintInfo
    info.paperSize = NSSize(width: 595.28, height: 841.89)
    info.leftMargin = 56.69
    info.rightMargin = 56.69
    info.topMargin = 56.69
    info.bottomMargin = 56.69
    info.horizontalPagination = .fit
    info.verticalPagination = .automatic
    info.isHorizontallyCentered = false
    info.isVerticallyCentered = false
    info.scalingFactor = 1
    info.dictionary()[NSPrintInfo.AttributeKey.allPages] = true
    info.dictionary()[NSPrintInfo.AttributeKey.copies] = 1
    return info
}

func savePDF(_ operation: NSPrintOperation, _ output: String) {
    operation.printInfo.jobDisposition = .save
    operation.printInfo.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = URL(fileURLWithPath: output)
    operation.showsPrintPanel = false
    operation.showsProgressPanel = false
    if !operation.run() { fail("无法完成 PDF 排版或写入") }
    guard let data = try? Data(contentsOf: URL(fileURLWithPath: output)),
          data.starts(with: Data("%PDF-".utf8)) else { fail("未生成有效的 PDF") }
    exit(0)
}

final class HTMLPrinter: NSObject, WKNavigationDelegate {
    let output: String
    let webView: WKWebView
    let window: NSWindow
    init(input: String, output: String) {
        self.output = output
        let configuration = WKWebViewConfiguration()
        configuration.preferences.javaScriptCanOpenWindowsAutomatically = false
        let info = printInfo()
        webView = WKWebView(frame: NSRect(x: 0, y: 0, width: info.paperSize.width - info.leftMargin - info.rightMargin, height: 728), configuration: configuration)
        window = NSWindow(contentRect: webView.frame, styleMask: .borderless, backing: .buffered, defer: false)
        super.init()
        window.contentView = webView
        webView.navigationDelegate = self
        let url = URL(fileURLWithPath: input)
        webView.loadFileURL(url, allowingReadAccessTo: url.deletingLastPathComponent())
    }
    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        // The generated document contains no executable input. Wait for fonts and images,
        // including failed images, before allowing WebKit to paginate it.
        Task { @MainActor in
            do {
                _ = try await webView.callAsyncJavaScript("await document.fonts.ready; await Promise.all(Array.from(document.images).map(i => i.complete ? Promise.resolve() : new Promise(r => { i.onload=r; i.onerror=r; }))); return true;", arguments: [:], in: nil, contentWorld: .defaultClient)
                let info = printInfo()
                info.jobDisposition = .save
                info.dictionary()[NSPrintInfo.AttributeKey.jobSavingURL] = URL(fileURLWithPath: self.output)
                let operation = webView.printOperation(with: info)
                operation.showsPrintPanel = false
                operation.showsProgressPanel = false
                operation.canSpawnSeparateThread = true
                operation.runModal(for: self.window, delegate: self, didRun: #selector(self.printFinished(_:success:context:)), contextInfo: nil)
            } catch { fail(error.localizedDescription) }
        }
    }
    @objc func printFinished(_ operation: NSPrintOperation, success: Bool, context: UnsafeMutableRawPointer?) {
        if !success { fail("PDF 排版失败") }
        exit(0)
    }
    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { fail(error.localizedDescription) }
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { fail(error.localizedDescription) }
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) { fail("PDF 排版进程意外退出") }
}

func largestImage(_ path: String) -> CGImage {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil) else { fail("无法读取图标文件") }
    var best: CGImage?
    for index in 0..<CGImageSourceGetCount(source) {
        if let image = CGImageSourceCreateImageAtIndex(source, index, nil),
           best == nil || image.width * image.height > best!.width * best!.height { best = image }
    }
    guard let image = best else { fail("图标不包含可读取的图像") }
    return image
}

let args = CommandLine.arguments
guard args.count >= 3 else { fail("usage: FileConversion <word-pdf|html-pdf|image-info|image> <input> [output] [width height]") }
let mode = args[1], input = args[2]
if mode == "image-info" {
    let image = largestImage(input)
    print("\(image.width) \(image.height)")
    exit(0)
}
guard args.count >= 4 else { fail("缺少输出路径") }
let output = args[3]
if mode == "image" {
    guard args.count == 6, let width = Int(args[4]), let height = Int(args[5]),
          width > 0, height > 0, width <= 8192, height <= 8192, width * height <= 32_000_000 else { fail("图像尺寸超出限制") }
    let image = largestImage(input)
    let jpeg = URL(fileURLWithPath: output).pathExtension.lowercased() == "jpg"
    guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { fail("无法创建图像") }
    if jpeg { context.setFillColor(NSColor.white.cgColor); context.fill(CGRect(x: 0, y: 0, width: width, height: height)) }
    context.interpolationQuality = .high
    context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
    guard let rendered = context.makeImage(),
          let destination = CGImageDestinationCreateWithURL(URL(fileURLWithPath: output) as CFURL, (jpeg ? UTType.jpeg : UTType.png).identifier as CFString, 1, nil) else { fail("无法写入图像") }
    CGImageDestinationAddImage(destination, rendered, [kCGImageDestinationLossyCompressionQuality: 0.9] as CFDictionary)
    if !CGImageDestinationFinalize(destination) { fail("图像写入失败") }
    exit(0)
}

let application = NSApplication.shared
application.setActivationPolicy(.prohibited)
application.finishLaunching()
if mode == "word-pdf" {
    do {
        let type: NSAttributedString.DocumentType = URL(fileURLWithPath: input).pathExtension.lowercased() == "doc" ? .docFormat : .officeOpenXML
        let content = NSMutableAttributedString(attributedString: try NSAttributedString(url: URL(fileURLWithPath: input), options: [.documentType: type], documentAttributes: nil))
        if args.count == 5 {
            struct Attachment: Decodable { let Marker: String; let Path: String; let Width: Double; let Height: Double }
            let attachments = try JSONDecoder().decode([Attachment].self, from: Data(contentsOf: URL(fileURLWithPath: args[4])))
            for item in attachments {
                let range = (content.string as NSString).range(of: item.Marker)
                guard range.location != NSNotFound, let image = NSImage(contentsOfFile: item.Path) else { fail("无法还原 Word 图片") }
                image.size = NSSize(width: item.Width, height: item.Height)
                let attachment = NSTextAttachment()
                attachment.attachmentCell = NSTextAttachmentCell(imageCell: image)
                content.replaceCharacters(in: range, with: NSAttributedString(attachment: attachment))
            }
        }
        let info = printInfo()
        let width = info.paperSize.width - info.leftMargin - info.rightMargin
        let view = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 728))
        view.textContainerInset = .zero
        view.textContainer?.lineFragmentPadding = 0
        view.textContainer?.containerSize = NSSize(width: width, height: CGFloat.greatestFiniteMagnitude)
        view.textStorage?.setAttributedString(content)
        if let manager = view.layoutManager, let container = view.textContainer {
            manager.ensureLayout(for: container)
            view.setFrameSize(NSSize(width: width, height: max(1, ceil(manager.usedRect(for: container).height))))
        }
        savePDF(NSPrintOperation(view: view, printInfo: info), output)
    } catch { fail("无法读取 Word 文档（可能已加密或格式损坏）：\(error.localizedDescription)") }
} else if mode == "html-pdf" {
    let printer = HTMLPrinter(input: input, output: output)
    withExtendedLifetime(printer) { application.run() }
} else { fail("不支持的转换模式") }

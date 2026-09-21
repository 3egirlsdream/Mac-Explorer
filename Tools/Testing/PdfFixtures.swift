// Native PDF fixtures; always pass an isolated test directory.
import Foundation
import AppKit
import PDFKit

let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
let bounds = CGRect(x: 0, y: 0, width: 612, height: 792)
func textPDF(_ name: String, _ lines: [String], fontSize: CGFloat = 24) -> PDFDocument {
    let url = root.appendingPathComponent(name)
    var media = bounds
    let context = CGContext(url as CFURL, mediaBox: &media, nil)!
    for text in lines {
        context.beginPDFPage(nil)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        (text as NSString).draw(at: NSPoint(x: 45, y: 650), withAttributes: [.font: NSFont.systemFont(ofSize: fontSize), .foregroundColor: NSColor.black])
        NSGraphicsContext.restoreGraphicsState()
        context.endPDFPage()
    }
    context.closePDF()
    return PDFDocument(url: url)!
}
let text = textPDF("text.pdf", ["原生文字测试 Native invoice 2026", "重复正文 Native invoice 2026"])
let image = NSImage(size: bounds.size)
image.lockFocus()
NSColor.white.setFill()
bounds.fill()
("扫描文档 Scan recognition 4826" as NSString).draw(at: NSPoint(x: 45, y: 600), withAttributes: [.font: NSFont.systemFont(ofSize: 24), .foregroundColor: NSColor.black])
image.unlockFocus()
let bitmap = NSBitmapImageRep(data: image.tiffRepresentation!)!
try bitmap.representation(using: .png, properties: [:])!.write(to: root.appendingPathComponent("scan.png"))
let scan = PDFDocument()
scan.insert(PDFPage(image: image)!, at: 0)
scan.write(to: root.appendingPathComponent("scan.pdf"))
let mixed = PDFDocument()
mixed.insert(text.page(at: 0)!.copy() as! PDFPage, at: 0)
mixed.insert(scan.page(at: 0)!.copy() as! PDFPage, at: 1)
mixed.write(to: root.appendingPathComponent("mixed.pdf"))
_ = textPDF("blank.pdf", [""])
text.write(to: root.appendingPathComponent("locked.pdf"), withOptions: [.userPasswordOption: "secret", .ownerPasswordOption: "owner"])
try Data("this is not a pdf".utf8).write(to: root.appendingPathComponent("corrupt.pdf"))
// A large text stream stays tiny on disk through repeated PDF page content.
let longLine = String(repeating: "Long document limit ", count: 600)
let longDocument = textPDF("long-page.pdf", [longLine], fontSize: 0.05)
let oversized = PDFDocument()
for index in 0..<450 {
    oversized.insert(longDocument.page(at: 0)!.copy() as! PDFPage, at: index)
}
oversized.write(to: root.appendingPathComponent("oversized.pdf"))

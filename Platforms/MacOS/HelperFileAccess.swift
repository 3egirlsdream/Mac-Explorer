import Foundation

// Implicit security-scoped bookmarks transfer the parent's user-selected grants.
// Retain the resolved URLs until helper exit; the OS also enforces the inherited sandbox.
func acquireHelperFileAccess() -> [URL] {
    guard let text = ProcessInfo.processInfo.environment["MACEXPLORER_FILE_BOOKMARKS"],
          let json = text.data(using: .utf8),
          let bookmarks = try? JSONDecoder().decode([String].self, from: json) else { return [] }
    return bookmarks.compactMap { bookmark in
        guard let data = Data(base64Encoded: bookmark) else { return nil }
        var stale = false
        return try? URL(resolvingBookmarkData: data, options: [.withoutUI], relativeTo: nil,
                        bookmarkDataIsStale: &stale)
    }
}
let helperFileAccess = acquireHelperFileAccess()

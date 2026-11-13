import Darwin
import Foundation

// The sandbox launcher consumes transferred bookmarks before this helper runs.
// Only fixed operations are accepted; Git never receives a shell command.
let args = CommandLine.arguments
let allowedGit = [
    "/Library/Developer/CommandLineTools/usr/bin/git",
    "/Applications/Xcode.app/Contents/Developer/usr/bin/git",
    "/opt/homebrew/bin/git", "/usr/local/bin/git"
]
guard args.count >= 4, allowedGit.contains(args[1]) else { exit(2) }
let operation = args[3]
let values = Array(args.dropFirst(4))
func objectId(_ value: String) -> Bool {
    (value.count == 40 || value.count == 64) && value.utf8.allSatisfy { (48...57).contains($0) || (65...70).contains($0) || (97...102).contains($0) }
}
func paths(_ values: [String]) -> Bool {
    !values.isEmpty && values.allSatisfy { value in
        !value.isEmpty && !value.hasPrefix("/") && !value.split(separator: "/").contains { $0 == ".." || $0.lowercased() == ".git" }
    }
}
var command = [args[1], "--no-pager", "--no-optional-locks", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-c", "core.quotepath=false"]
if operation == "check-ignore" { command.removeAll { $0 == "--literal-pathspecs" } }
if operation != "version" {
    guard args[2].hasPrefix("/"), chdir(args[2]) == 0 else { exit(2) }
}
switch operation {
case "diff", "diff-staged":
    guard paths(values) else { exit(2) }
    command += ["diff", "--no-ext-diff", "--no-textconv", "--no-color"]
    if operation == "diff-staged" { command += ["--cached"] }
    command += ["--"] + values
case "scope-status", "check-ignore", "stage", "unstage", "unstage-new", "discard":
    guard paths(values) else { exit(2) }
    switch operation {
    case "scope-status": command += ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=dirty"]
    case "check-ignore": command += ["check-ignore"]
    case "stage": command += ["add", "--all"]
    case "unstage": command += ["restore", "--staged"]
    case "unstage-new": command += ["rm", "--cached", "-f"]
    default: command += ["restore", "--worktree"]
    }
    command += ["--"] + values
case "head":
    guard values.isEmpty else { exit(2) }
    command += ["rev-parse", "--verify", "--quiet", "HEAD"]
case "history":
    guard values.count >= 4, objectId(values[0]), let skip = Int(values[1]), skip >= 0,
          values[2] == "follow" || values[2] == "scope", paths(Array(values.dropFirst(3))),
          values[2] != "follow" || values.count == 4 else { exit(2) }
    command += ["log", "--max-count=101", "--skip=\(skip)", "--format=%H%x00%P%x00%an%x00%aI%x00%s", "--name-status", "--no-ext-diff", "--no-textconv", "-z", "--diff-merges=first-parent", "--find-renames"]
    if values[2] == "follow" { command += ["--follow"] }
    command += [values[0], "--"] + Array(values.dropFirst(3))
case "commit-diff":
    guard values.count >= 2, objectId(values[0]), paths(Array(values.dropFirst())) else { exit(2) }
    command += ["show", "--format=", "--first-parent", "--root", "--no-ext-diff", "--no-textconv", "--no-color", "--find-renames", values[0], "--"] + Array(values.dropFirst())
case "version":
    guard args[2] == "-", values.isEmpty else { exit(2) }
    command += ["--version"]
case "status", "ignored", "untracked", "filters":
    guard values.isEmpty else { exit(2) }
    if operation == "filters" {
        command += ["config", "--null", "--get-regexp", "^(filter\\..*\\.(clean|smudge|process)|extensions\\.partialclone|remote\\..*\\.partialclonefilter)$"]
    } else if operation == "status" {
        command += ["status", "--porcelain", "-z", "--ignore-submodules=dirty"]
    } else {
        command += ["ls-files", "-o"]
        if operation == "ignored" { command += ["-i"] }
        command += ["--exclude-standard", "-z", "--directory"]
    }
default: exit(2)
}
for key in ProcessInfo.processInfo.environment.keys where key.hasPrefix("GIT_") { unsetenv(key) }
setenv("GIT_NO_LAZY_FETCH", "1", 1)
setenv("GIT_TERMINAL_PROMPT", "0", 1)
unsetenv("MACEXPLORER_FILE_BOOKMARKS")
let argv = command.map { strdup($0) } + [nil]
argv.withUnsafeBufferPointer { buffer in _ = execv(args[1], buffer.baseAddress!) }
exit(1)

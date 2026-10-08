import Darwin

// Consume the transferred bookmarks before exec. Replacing this process keeps
// the inherited sandbox; Git never receives a shell command or arbitrary verb.
let args = CommandLine.arguments
let allowedGit = [
    "/Library/Developer/CommandLineTools/usr/bin/git",
    "/Applications/Xcode.app/Contents/Developer/usr/bin/git",
    "/opt/homebrew/bin/git", "/usr/local/bin/git"
]
guard args.count == 4, allowedGit.contains(args[1]) else { exit(2) }
var command = [args[1], "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null"]
switch args[3] {
case "version":
    guard args[2] == "-" else { exit(2) }
    command += ["--version"]
case "status", "ignored", "untracked", "filters":
    guard args[2].hasPrefix("/"), chdir(args[2]) == 0 else { exit(2) }
    if args[3] == "filters" {
        command += ["config", "--null", "--get-regexp",
            "^(filter\\..*\\.(clean|process)|extensions\\.partialclone|remote\\..*\\.partialclonefilter)$", ".+"]
    }
    else if args[3] == "status" { command += ["status", "--porcelain", "-z", "--ignore-submodules=dirty"] }
    else {
        command += ["ls-files", "-o"]
        if args[3] == "ignored" { command += ["-i"] }
        command += ["--exclude-standard", "-z", "--directory"]
    }
default: exit(2)
}
for key in ProcessInfo.processInfo.environment.keys where key.hasPrefix("GIT_") { unsetenv(key) }
setenv("GIT_NO_LAZY_FETCH", "1", 1)
unsetenv("MACEXPLORER_FILE_BOOKMARKS")
let argv = command.map { strdup($0) } + [nil]
argv.withUnsafeBufferPointer { buffer in _ = execv(args[1], buffer.baseAddress!) }
exit(1)

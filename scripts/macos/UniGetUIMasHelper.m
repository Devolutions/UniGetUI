#import <Foundation/Foundation.h>
#import <LocalAuthentication/LocalAuthentication.h>
#import <Security/Security.h>
#import <SystemConfiguration/SystemConfiguration.h>
#include <xpc/xpc.h>
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <mach-o/fat.h>
#include <libkern/OSByteOrder.h>
#include <fcntl.h>
#include <grp.h>
#include <pwd.h>
#include <sys/stat.h>
#include <unistd.h>

static const char *ServiceName = "com.devolutions.unigetui.mashelper";
static NSString *const ServiceLabel = @"com.devolutions.unigetui.mashelper";
static NSString *const LegacyServiceLabel = @"io.github.marticliment.unigetui.mashelper";
static NSString *const HelperDirectory = @"/Library/PrivilegedHelperTools";
static NSString *const InstalledHelperPath = @"/Library/PrivilegedHelperTools/com.devolutions.unigetui.mashelper";
static NSString *const DaemonDirectory = @"/Library/LaunchDaemons";
static NSString *const DaemonPlistPath = @"/Library/LaunchDaemons/com.devolutions.unigetui.mashelper.plist";
static NSString *const StagingParentPath = @"/private/var/run/com.devolutions.unigetui.mashelper";
// These are protocol outcomes, never raw mas exit codes.
enum { Failed = 1, Unavailable = 77, Canceled = 128 };
static const NSUInteger MaximumExecutableSize = 128 * 1024 * 1024;
static const int MaxSymlinkHops = 32;

static void Report(NSString *message)
{
    fprintf(stderr, "%s\n", message.UTF8String);
    fflush(stderr);
}

static BOOL ValidOperation(NSString *verb, BOOL force, NSString *identifier)
{
    if (verb == nil || identifier == nil || identifier.length == 0 || identifier.length > 20)
        return NO;
    NSArray<NSString *> *allowed = force
        ? @[@"get", @"install", @"update"]
        : @[@"get", @"install", @"update", @"uninstall", @"purchase", @"upgrade"];
    if (![allowed containsObject:verb])
        return NO;
    for (NSUInteger i = 0; i < identifier.length; i++)
        if ([identifier characterAtIndex:i] < '0' || [identifier characterAtIndex:i] > '9')
            return NO;
    return YES;
}

static BOOL UserIsAdministrator(uid_t uid, gid_t *primaryGroup)
{
    if (uid == 0)
        return NO;
    struct passwd entry, *found = NULL;
    char buffer[16384];
    if (getpwuid_r(uid, &entry, buffer, sizeof(buffer), &found) != 0 || found == NULL)
        return NO;
    int groups[128], count = 128;
    if (getgrouplist(entry.pw_name, (int)entry.pw_gid, groups, &count) < 0)
        return NO;
    // macOS's built-in admin group has GID 80.
    for (int i = 0; i < count; i++)
        if (groups[i] == 80)
        {
            if (primaryGroup != NULL)
                *primaryGroup = entry.pw_gid;
            return YES;
        }
    return NO;
}

static BOOL ActiveAdministrator(uid_t uid, gid_t *primaryGroup)
{
    if (uid == 0)
        return NO;
    uid_t consoleUid = 0;
    CFStringRef console = SCDynamicStoreCopyConsoleUser(NULL, &consoleUid, NULL);
    if (console != NULL)
        CFRelease(console);
    if (consoleUid != uid)
        return NO;
    return UserIsAdministrator(uid, primaryGroup);
}

static BOOL TrustedStat(const struct stat *status, uid_t allowedOwner)
{
    // Reject world-writable items, sticky temp directories (/tmp, /var/tmp), and group-writable
    // items outside wheel (0) or admin (80).
    if ((status->st_mode & S_IWOTH) != 0 || (status->st_mode & S_ISVTX) != 0)
        return NO;
    if ((status->st_mode & S_IWGRP) != 0 && status->st_gid != 0 && status->st_gid != 80)
        return NO;
    return status->st_uid == 0
        || (allowedOwner != 0 && status->st_uid == allowedOwner)
        || UserIsAdministrator(status->st_uid, NULL);
}

static BOOL TrustedPathWalk(NSString *path, uid_t allowedOwner, int links)
{
    if (path == nil || ![path hasPrefix:@"/"])
        return NO;
    struct stat status;
    if (lstat("/", &status) != 0 || !TrustedStat(&status, allowedOwner))
        return NO;
    NSArray<NSString *> *components = [path pathComponents];
    NSString *current = @"/";
    for (NSUInteger i = 0; i < components.count; i++)
    {
        NSString *part = components[i];
        if ([part isEqualToString:@"/"] || [part isEqualToString:@"."] || part.length == 0)
            continue;
        if ([part isEqualToString:@".."])
        {
            current = [current stringByDeletingLastPathComponent];
            if (current.length == 0)
                current = @"/";
            continue;
        }
        NSString *next = [current isEqualToString:@"/"]
            ? [@"/" stringByAppendingString:part]
            : [current stringByAppendingFormat:@"/%@", part];
        if (lstat(next.fileSystemRepresentation, &status) != 0)
            return NO;
        if (S_ISLNK(status.st_mode))
        {
            if (++links > MaxSymlinkHops)
                return NO;
            char targetBuffer[PATH_MAX];
            ssize_t length = readlink(next.fileSystemRepresentation, targetBuffer, sizeof(targetBuffer) - 1);
            if (length <= 0)
                return NO;
            targetBuffer[length] = '\0';
            NSString *target = @(targetBuffer);
            NSString *resolved = [target hasPrefix:@"/"]
                ? target
                : ([current isEqualToString:@"/"]
                    ? [@"/" stringByAppendingString:target]
                    : [current stringByAppendingFormat:@"/%@", target]);
            for (NSUInteger rest = i + 1; rest < components.count; rest++)
                if (![components[rest] isEqualToString:@"/"])
                    resolved = [resolved stringByAppendingFormat:@"/%@", components[rest]];
            return TrustedPathWalk(resolved, allowedOwner, links);
        }
        if (!TrustedStat(&status, allowedOwner))
            return NO;
        current = next;
    }
    return S_ISREG(status.st_mode);
}

static BOOL TrustedExecutablePath(NSString *path, uid_t allowedOwner)
{
    return TrustedPathWalk(path, allowedOwner, 0);
}

static NSString *SelfPath(void)
{
    char path[PATH_MAX];
    uint32_t size = sizeof(path);
    if (_NSGetExecutablePath(path, &size) != 0)
        return nil;
    char resolved[PATH_MAX];
    return realpath(path, resolved) == NULL ? nil : @(resolved);
}

static SecRequirementRef OwnRequirement(void)
{
    SecCodeRef code = NULL;
    SecRequirementRef requirement = NULL;
    if (SecCodeCopySelf(kSecCSDefaultFlags, &code) == errSecSuccess)
    {
        SecCodeCopyDesignatedRequirement(code, kSecCSDefaultFlags, &requirement);
        CFRelease(code);
    }
    return requirement;
}

static BOOL TrustedMessage(xpc_object_t message, SecRequirementRef requirement)
{
    SecCodeRef peer = NULL;
    if (requirement == NULL
        || SecCodeCreateWithXPCMessage(message, kSecCSDefaultFlags, &peer) != errSecSuccess)
        return NO;
    BOOL valid = SecCodeCheckValidity(peer, kSecCSDefaultFlags, requirement) == errSecSuccess;
    CFRelease(peer);
    return valid;
}

static BOOL ValidSignature(NSString *path, SecRequirementRef requirement)
{
    SecStaticCodeRef code = NULL;
    if (SecStaticCodeCreateWithPath((__bridge CFURLRef)[NSURL fileURLWithPath:path],
            kSecCSDefaultFlags, &code) != errSecSuccess)
        return NO;
    BOOL valid = SecStaticCodeCheckValidity(code, kSecCSCheckAllArchitectures, requirement) == errSecSuccess;
    CFRelease(code);
    return valid;
}

// Only absolute system-library imports are accepted (including weak/lazy imports). Unused rpaths
// may be present, but no import may resolve through them or through a user-owned library.
static BOOL SystemLinkedMachO(const uint8_t *bytes, NSUInteger length)
{
    if (length < sizeof(uint32_t))
        return NO;
    uint32_t magic;
    memcpy(&magic, bytes, sizeof(magic));
    if (magic == FAT_CIGAM)
    {
        if (length < sizeof(struct fat_header))
            return NO;
        struct fat_header header;
        memcpy(&header, bytes, sizeof(header));
        uint32_t count = OSSwapBigToHostInt32(header.nfat_arch);
        if (count == 0 || count > 8 || sizeof(header) + count * sizeof(struct fat_arch) > length)
            return NO;
        for (uint32_t i = 0; i < count; i++)
        {
            struct fat_arch arch;
            memcpy(&arch, bytes + sizeof(header) + i * sizeof(arch), sizeof(arch));
            NSUInteger offset = OSSwapBigToHostInt32(arch.offset), size = OSSwapBigToHostInt32(arch.size);
            if (offset > length || size > length - offset || size < sizeof(struct mach_header_64))
                return NO;
            uint32_t sliceMagic;
            memcpy(&sliceMagic, bytes + offset, sizeof(sliceMagic));
            if (sliceMagic != MH_MAGIC_64 || !SystemLinkedMachO(bytes + offset, size))
                return NO;
        }
        return YES;
    }
    if (magic != MH_MAGIC_64 || length < sizeof(struct mach_header_64))
        return NO;
    struct mach_header_64 header;
    memcpy(&header, bytes, sizeof(header));
    if (header.filetype != MH_EXECUTE || header.sizeofcmds > length - sizeof(header)
        || header.ncmds > header.sizeofcmds / sizeof(struct load_command))
        return NO;
    NSUInteger offset = sizeof(header), end = offset + header.sizeofcmds;
    for (uint32_t i = 0; i < header.ncmds; i++)
    {
        if (end - offset < sizeof(struct load_command))
            return NO;
        struct load_command command;
        memcpy(&command, bytes + offset, sizeof(command));
        if (command.cmdsize < sizeof(command) || command.cmdsize > end - offset)
            return NO;
        if (command.cmd == LC_DYLD_ENVIRONMENT)
            return NO;
        if (command.cmd == LC_LOAD_DYLIB || command.cmd == LC_LOAD_WEAK_DYLIB
            || command.cmd == LC_REEXPORT_DYLIB || command.cmd == LC_LOAD_UPWARD_DYLIB
            || command.cmd == LC_LAZY_LOAD_DYLIB)
        {
            if (command.cmdsize < sizeof(struct dylib_command))
                return NO;
            struct dylib_command library;
            memcpy(&library, bytes + offset, sizeof(library));
            NSUInteger nameOffset = library.dylib.name.offset;
            if (nameOffset < sizeof(library) || nameOffset >= command.cmdsize)
                return NO;
            const char *name = (const char *)bytes + offset + nameOffset;
            if (memchr(name, '\0', command.cmdsize - nameOffset) == NULL
                || (strncmp(name, "/usr/lib/", 9) != 0 && strncmp(name, "/System/Library/", 16) != 0)
                || strstr(name, "/../") != NULL)
                return NO;
        }
        offset += command.cmdsize;
    }
    return offset == end;
}

static NSData *ReadBoundedRegularFile(NSString *path)
{
    if (path == nil)
        return nil;
    int input = open(path.fileSystemRepresentation, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (input < 0)
        return nil;
    struct stat status;
    BOOL valid = fstat(input, &status) == 0 && S_ISREG(status.st_mode)
        && status.st_size > 0 && (uint64_t)status.st_size <= MaximumExecutableSize;
    NSMutableData *data = valid ? [NSMutableData dataWithLength:(NSUInteger)status.st_size] : nil;
    NSUInteger offset = 0;
    while (valid && offset < data.length)
    {
        ssize_t readCount = read(input, (uint8_t *)data.mutableBytes + offset, data.length - offset);
        if (readCount < 0 && errno == EINTR)
            continue;
        if (readCount <= 0) { valid = NO; break; }
        offset += (NSUInteger)readCount;
    }
    close(input);
    return valid ? data : nil;
}

static NSString *ResolveMas(NSString *path, uid_t allowedOwner)
{
    if (![path isAbsolutePath] || ![path.lastPathComponent isEqualToString:@"mas"]
        || !TrustedExecutablePath(path, allowedOwner))
        return nil;
    char resolved[PATH_MAX];
    if (realpath(path.fileSystemRepresentation, resolved) == NULL)
        return nil;
    NSString *actual = @(resolved);
    if (!TrustedExecutablePath(actual, allowedOwner))
        return nil;
    // Homebrew 7 uses a small shell wrapper in bin and the native executable in libexec/bin.
    NSString *bundled = [[actual.stringByDeletingLastPathComponent stringByDeletingLastPathComponent]
        stringByAppendingPathComponent:@"libexec/bin/mas"];
    if ([actual.stringByDeletingLastPathComponent.lastPathComponent isEqualToString:@"bin"]
        && [NSFileManager.defaultManager fileExistsAtPath:bundled])
    {
        if (!TrustedExecutablePath(bundled, allowedOwner)
            || realpath(bundled.fileSystemRepresentation, resolved) == NULL)
            return nil;
        actual = @(resolved);
        if (!TrustedExecutablePath(actual, allowedOwner))
            return nil;
    }
    return actual;
}

static BOOL Snapshot(NSString *source, NSString *destination, SecRequirementRef requirement)
{
    NSData *data = ReadBoundedRegularFile(source);
    if (data == nil || !SystemLinkedMachO(data.bytes, data.length))
        return NO;
    // Mode 0555 inside a root-owned 0755 staging directory: nobody except root can modify or
    // replace it, while CoreAuthUI and mas (after dropping euid) can read executable metadata.
    int output = open(destination.fileSystemRepresentation, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0555);
    if (output < 0)
        return NO;
    BOOL valid = fchown(output, 0, 0) == 0 || geteuid() != 0;
    NSUInteger offset = 0;
    while (valid && offset < data.length)
    {
        ssize_t written = write(output, (const uint8_t *)data.bytes + offset, data.length - offset);
        if (written < 0 && errno == EINTR)
            continue;
        if (written <= 0) { valid = NO; break; }
        offset += (NSUInteger)written;
    }
    if (close(output) != 0)
        valid = NO;
    if (!valid || !ValidSignature(destination, requirement))
    {
        unlink(destination.fileSystemRepresentation);
        return NO;
    }
    return YES;
}

static NSDictionary<NSString *, NSString *> *CleanEnvironment(uid_t uid, gid_t gid)
{
    // Do not forward DYLD_*, shell startup hooks, or client-supplied SUDO_UID/GID.
    return @{
        @"PATH": @"/usr/bin:/bin:/usr/sbin:/sbin",
        @"SUDO_UID": [NSString stringWithFormat:@"%u", uid],
        @"SUDO_GID": [NSString stringWithFormat:@"%u", gid],
        @"LC_CTYPE": @"UTF-8",
    };
}

static NSString *FormatAuthenticationReason(NSString *verb, BOOL force, NSString *identifier, NSString *prompt)
{
    NSString *commandTag = [NSString stringWithFormat:@"(mas %@%@ %@)",
        verb, force ? @" --force" : @"", identifier];
    NSString *cleaned = [prompt stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    for (NSString *prefix in @[@"UniGetUI wants to ", @"UniGetUI needs your password to "])
    {
        if ([cleaned hasPrefix:prefix])
        {
            cleaned = [cleaned substringFromIndex:prefix.length];
            break;
        }
    }
    while ([cleaned hasSuffix:@"."])
        cleaned = [cleaned substringToIndex:cleaned.length - 1];
    if (cleaned.length == 0)
        return [NSString stringWithFormat:@"run mas %@%@ %@", verb, force ? @" --force" : @"", identifier];
    if ([cleaned containsString:commandTag])
        return cleaned;
    return [NSString stringWithFormat:@"%@ %@", cleaned, commandTag];
}

static int AuthenticationResult(BOOL done, BOOL success, NSInteger errorCode)
{
    if (done && success)
        return 0;
    if (done && (errorCode == LAErrorUserFallback || errorCode == LAErrorPasscodeNotSet
        || errorCode == LAErrorNotInteractive))
        return Unavailable;
    return Canceled;
}

static BOOL BiometricsAvailable(void)
{
    LAContext *context = [[LAContext alloc] init];
    BOOL available = NO;
    if (@available(macOS 15.0, *))
        available = [context canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometricsOrCompanion error:NULL];
    else
        available = [context canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics error:NULL];
    [context invalidate];
    return available;
}

static int Authenticate(NSString *reason, uid_t uid, gid_t gid)
{
    if (setegid(gid) != 0 || seteuid(uid) != 0)
        return Failed;
    LAContext *context = [[LAContext alloc] init];
    NSError *error = nil;
    __block BOOL done = NO, success = NO;
    __block NSInteger failureCode = 0;
    // LAPolicyDeviceOwnerAuthentication presents Touch ID (and Apple Watch) when available, and
    // lets "Use Password..." switch seamlessly inside the same native macOS dialog.
    if (![context canEvaluatePolicy:LAPolicyDeviceOwnerAuthentication error:&error])
    {
        Report([NSString stringWithFormat:@"macOS authentication is unavailable: %@", error.localizedDescription]);
        [context invalidate];
        if (seteuid(0) != 0 || setegid(0) != 0)
            return Failed;
        return Unavailable;
    }
    [context evaluatePolicy:LAPolicyDeviceOwnerAuthentication localizedReason:reason
        reply:^(BOOL authorized, NSError *failure) {
            dispatch_async(dispatch_get_main_queue(), ^{
                success = authorized;
                failureCode = failure.code;
                done = YES;
            });
        }];
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:120];
    while (!done && deadline.timeIntervalSinceNow > 0)
        [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
    [context invalidate];
    if (seteuid(0) != 0 || setegid(0) != 0)
        return Failed;
    return AuthenticationResult(done, success, failureCode);
}

static int Worker(NSArray<NSString *> *arguments)
{
    if (getuid() != 0 || geteuid() != 0 || arguments.count != 7)
        return Failed;
    unsigned long numericUid = strtoul(arguments[0].UTF8String, NULL, 10);
    if (numericUid == 0 || numericUid > UINT_MAX)
        return Failed;
    uid_t uid = (uid_t)numericUid;
    gid_t gid;
    BOOL force = [arguments[4] isEqualToString:@"1"];
    if ((![arguments[4] isEqualToString:@"0"] && !force)
        || !ActiveAdministrator(uid, &gid)
        || !ValidOperation(arguments[3], force, arguments[5]))
        return Failed;
    NSString *reason = FormatAuthenticationReason(arguments[3], force, arguments[5], arguments[6]);
    int authorized = Authenticate(reason, uid, gid);
    if (authorized != 0)
        return authorized;
    if (!ActiveAdministrator(uid, &gid))
        return Failed;

    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:arguments[1]];
    task.arguments = force ? @[arguments[3], @"--force", arguments[5]] : @[arguments[3], arguments[5]];
    task.environment = CleanEnvironment(uid, gid);
    task.currentDirectoryURL = [NSURL fileURLWithPath:@"/"];
    task.standardInput = [NSFileHandle fileHandleWithNullDevice];
    NSError *error = nil;
    if (![task launchAndReturnError:&error])
    {
        Report(error.localizedDescription);
        return Failed;
    }
    [task waitUntilExit];
    if (task.terminationReason == NSTaskTerminationReasonExit && task.terminationStatus == 0)
        return 0;
    Report([NSString stringWithFormat:@"mas exited with code %d.", task.terminationStatus]);
    return Failed;
}

static BOOL RootOwnedItem(NSString *path, mode_t expectedType)
{
    struct stat status;
    return lstat(path.fileSystemRepresentation, &status) == 0
        && (status.st_mode & S_IFMT) == expectedType
        && status.st_uid == 0
        && (status.st_mode & (S_IWGRP | S_IWOTH)) == 0;
}

static BOOL EnsureRootDirectory(NSString *path, mode_t mode)
{
    struct stat status;
    if (lstat(path.fileSystemRepresentation, &status) != 0)
    {
        if (errno != ENOENT || mkdir(path.fileSystemRepresentation, mode) != 0)
            return NO;
    }
    if (lchown(path.fileSystemRepresentation, 0, 0) != 0
        || chmod(path.fileSystemRepresentation, mode) != 0)
        return NO;
    return RootOwnedItem(path, S_IFDIR);
}

static int RunLaunchctl(NSArray<NSString *> *arguments, NSString **outputText)
{
    NSTask *task = [[NSTask alloc] init];
    NSPipe *pipe = [NSPipe pipe];
    task.executableURL = [NSURL fileURLWithPath:@"/bin/launchctl"];
    task.arguments = arguments;
    task.environment = @{ @"PATH": @"/usr/bin:/bin:/usr/sbin:/sbin" };
    task.standardInput = [NSFileHandle fileHandleWithNullDevice];
    task.standardOutput = pipe;
    task.standardError = pipe;
    NSError *error = nil;
    if (![task launchAndReturnError:&error])
    {
        if (outputText != NULL)
            *outputText = error.localizedDescription;
        return Failed;
    }
    [pipe.fileHandleForWriting closeFile];
    NSData *data = [pipe.fileHandleForReading readDataToEndOfFile];
    [task waitUntilExit];
    if (outputText != NULL)
        *outputText = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"";
    return task.terminationReason == NSTaskTerminationReasonExit ? task.terminationStatus : Failed;
}

static BOOL AtomicWriteRootFile(NSString *directory, NSString *name, NSData *bytes,
    mode_t mode, SecRequirementRef requirement)
{
    NSString *pattern = [directory stringByAppendingFormat:@"/%@XXXXXX",
        [NSString stringWithFormat:@".%@.", name]];
    char *tempPath = strdup(pattern.fileSystemRepresentation);
    if (tempPath == NULL)
        return NO;
    int fd = mkstemp(tempPath);
    if (fd < 0)
    {
        free(tempPath);
        return NO;
    }
    BOOL ok = fchown(fd, 0, 0) == 0 && fchmod(fd, mode) == 0;
    NSUInteger offset = 0;
    while (ok && offset < bytes.length)
    {
        ssize_t written = write(fd, (const uint8_t *)bytes.bytes + offset, bytes.length - offset);
        if (written < 0 && errno == EINTR)
            continue;
        if (written <= 0) { ok = NO; break; }
        offset += (NSUInteger)written;
    }
    if (ok && fsync(fd) != 0)
        ok = NO;
    if (close(fd) != 0)
        ok = NO;
    NSString *stagedPath = @(tempPath);
    if (ok && requirement != NULL && !ValidSignature(stagedPath, requirement))
        ok = NO;
    NSString *finalPath = [directory stringByAppendingPathComponent:name];
    if (ok && rename(tempPath, finalPath.fileSystemRepresentation) != 0)
        ok = NO;
    if (!ok)
        unlink(tempPath);
    free(tempPath);
    return ok;
}

static NSData *DaemonPlistData(void)
{
    NSString *xml = [NSString stringWithFormat:
        @"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
        "<plist version=\"1.0\">\n"
        "<dict>\n"
        "    <key>Label</key>\n"
        "    <string>%@</string>\n"
        "    <key>ProgramArguments</key>\n"
        "    <array>\n"
        "        <string>%@</string>\n"
        "        <string>serve</string>\n"
        "    </array>\n"
        "    <key>MachServices</key>\n"
        "    <dict>\n"
        "        <key>%@</key>\n"
        "        <true/>\n"
        "    </dict>\n"
        "</dict>\n"
        "</plist>\n",
        ServiceLabel, InstalledHelperPath, ServiceLabel];
    return [xml dataUsingEncoding:NSUTF8StringEncoding];
}

static int InstallDaemon(void)
{
    if (getuid() != 0 || geteuid() != 0)
    {
        Report(@"install-daemon must run as root.");
        return Failed;
    }
    NSString *selfPath = SelfPath();
    if (selfPath == nil || !TrustedExecutablePath(selfPath, 0))
    {
        Report(@"The UniGetUIMasHelper binary is in an untrusted location.");
        return Failed;
    }
    NSData *selfBytes = ReadBoundedRegularFile(selfPath);
    if (selfBytes == nil || !SystemLinkedMachO(selfBytes.bytes, selfBytes.length))
    {
        Report(@"The UniGetUIMasHelper binary failed Mach-O validation.");
        return Failed;
    }
    SecRequirementRef requirement = OwnRequirement();
    if (requirement == NULL)
    {
        Report(@"The UniGetUIMasHelper signature requirement could not be read.");
        return Failed;
    }
    if (!RootOwnedItem(@"/", S_IFDIR) || !RootOwnedItem(@"/Library", S_IFDIR)
        || !EnsureRootDirectory(HelperDirectory, 0755)
        || !EnsureRootDirectory(DaemonDirectory, 0755))
    {
        CFRelease(requirement);
        Report(@"System helper directories failed ownership validation.");
        return Failed;
    }
    BOOL installed = AtomicWriteRootFile(HelperDirectory, ServiceLabel, selfBytes, 0555, requirement)
        && AtomicWriteRootFile(DaemonDirectory, [ServiceLabel stringByAppendingString:@".plist"],
            DaemonPlistData(), 0644, NULL);
    CFRelease(requirement);
    if (!installed)
    {
        unlink(DaemonPlistPath.fileSystemRepresentation);
        unlink(InstalledHelperPath.fileSystemRepresentation);
        Report(@"Could not write the privileged Mac App Store helper.");
        return Failed;
    }
    (void)RunLaunchctl(@[@"bootout", [@"system/" stringByAppendingString:LegacyServiceLabel]], NULL);
    (void)RunLaunchctl(@[@"bootout", [@"system/" stringByAppendingString:ServiceLabel]], NULL);
    NSString *launchOutput = nil;
    if (RunLaunchctl(@[@"bootstrap", @"system", DaemonPlistPath], &launchOutput) != 0)
    {
        unlink(DaemonPlistPath.fileSystemRepresentation);
        unlink(InstalledHelperPath.fileSystemRepresentation);
        Report([NSString stringWithFormat:@"launchctl could not bootstrap %@: %@",
            ServiceLabel, [launchOutput stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet]]);
        return Failed;
    }
    return 0;
}

static int RemoveDaemon(void)
{
    if (getuid() != 0 || geteuid() != 0)
    {
        Report(@"remove-daemon must run as root.");
        return Failed;
    }
    (void)RunLaunchctl(@[@"bootout", [@"system/" stringByAppendingString:LegacyServiceLabel]], NULL);
    (void)RunLaunchctl(@[@"bootout", [@"system/" stringByAppendingString:ServiceLabel]], NULL);
    unlink(DaemonPlistPath.fileSystemRepresentation);
    unlink(InstalledHelperPath.fileSystemRepresentation);
    [NSFileManager.defaultManager removeItemAtPath:StagingParentPath error:nil];
    return 0;
}

static BOOL HelperReady(void)
{
    if (!RootOwnedItem(DaemonPlistPath, S_IFREG) || !RootOwnedItem(InstalledHelperPath, S_IFREG))
        return NO;
    NSString *selfPath = SelfPath();
    NSData *selfBytes = ReadBoundedRegularFile(selfPath);
    NSData *installedBytes = ReadBoundedRegularFile(InstalledHelperPath);
    if (selfBytes == nil || installedBytes == nil || ![selfBytes isEqualToData:installedBytes])
        return NO;
    SecRequirementRef requirement = OwnRequirement();
    if (requirement == NULL)
        return NO;
    BOOL valid = ValidSignature(InstalledHelperPath, requirement);
    CFRelease(requirement);
    if (!valid)
        return NO;
    return RunLaunchctl(@[@"print", [@"system/" stringByAppendingString:ServiceLabel]], NULL) == 0;
}

static void Send(xpc_connection_t connection, const char *kind, NSString *text, int result)
{
    xpc_object_t reply = xpc_dictionary_create(NULL, NULL, 0);
    xpc_dictionary_set_string(reply, "kind", kind);
    xpc_dictionary_set_int64(reply, "result", result);
    if (text != nil)
        xpc_dictionary_set_string(reply, "text", text.UTF8String);
    xpc_connection_send_message(connection, reply);
}

static NSString *Field(xpc_object_t request, const char *key, NSUInteger maximumLength)
{
    const char *value = xpc_dictionary_get_string(request, key);
    if (value == NULL || strnlen(value, maximumLength + 1) > maximumLength)
        return nil;
    return [[NSString alloc] initWithUTF8String:value];
}

static int RunWorker(uid_t uid, NSString *masPath, NSString *verb, BOOL force, NSString *identifier,
    NSString *prompt, SecRequirementRef requirement, xpc_connection_t connection)
{
    if (!EnsureRootDirectory(StagingParentPath, 0755))
    {
        Send(connection, "output", @"Could not prepare the root Mac App Store staging directory.", 0);
        return Failed;
    }
    NSString *pattern = [StagingParentPath stringByAppendingPathComponent:@"work.XXXXXX"];
    char *directory = strdup(pattern.fileSystemRepresentation);
    if (directory == NULL || mkdtemp(directory) == NULL)
    {
        free(directory);
        Send(connection, "output", @"Could not create the private Mac App Store staging directory.", 0);
        return Failed;
    }
    if (lchown(directory, 0, 0) != 0 || chmod(directory, 0755) != 0)
    {
        rmdir(directory);
        free(directory);
        Send(connection, "output", @"Could not set permissions on the Mac App Store staging directory.", 0);
        return Failed;
    }
    NSString *root = @(directory);
    free(directory);
    NSString *worker = [root stringByAppendingPathComponent:@"UniGetUIMasHelper"];
    NSString *stagedMas = [root stringByAppendingPathComponent:@"mas"];
    int result = Failed;
    @try
    {
        NSString *resolved = ResolveMas(masPath, uid);
        if (resolved == nil || !Snapshot(resolved, stagedMas, NULL))
        {
            Send(connection, "output", @"The native helper requires a trusted, signed, system-linked mas executable. Use the password path for this installation of mas.", 0);
            return Unavailable;
        }
        // Never re-execute a potentially replaced, user-writable copy of the privileged helper.
        if (!Snapshot(SelfPath(), worker, requirement))
        {
            Send(connection, "output", @"The privileged helper's signature could not be verified.", 0);
            return Failed;
        }
        NSPipe *pipe = [NSPipe pipe];
        NSTask *task = [[NSTask alloc] init];
        task.executableURL = [NSURL fileURLWithPath:@"/bin/launchctl"];
        task.arguments = @[@"asuser", [NSString stringWithFormat:@"%u", uid], worker, @"worker",
            [NSString stringWithFormat:@"%u", uid], stagedMas, resolved, verb,
            force ? @"1" : @"0", identifier, prompt];
        task.environment = CleanEnvironment(0, 0);
        task.currentDirectoryURL = [NSURL fileURLWithPath:@"/"];
        task.standardInput = [NSFileHandle fileHandleWithNullDevice];
        task.standardOutput = pipe;
        task.standardError = pipe;
        NSError *error = nil;
        if (![task launchAndReturnError:&error])
        {
            Send(connection, "output", error.localizedDescription, 0);
            return Failed;
        }
        [pipe.fileHandleForWriting closeFile];
        // Bound each IPC message and drain both streams before waiting for the child.
        for (;;)
        {
            NSData *data = [pipe.fileHandleForReading readDataUpToLength:16384 error:&error];
            if (data.length == 0)
                break;
            xpc_object_t output = xpc_dictionary_create(NULL, NULL, 0);
            xpc_dictionary_set_string(output, "kind", "bytes");
            xpc_dictionary_set_data(output, "data", data.bytes, data.length);
            xpc_connection_send_message(connection, output);
        }
        [task waitUntilExit];
        result = task.terminationReason == NSTaskTerminationReasonExit ? task.terminationStatus : Failed;
        if (error != nil)
        {
            Send(connection, "output", error.localizedDescription, 0);
            result = Failed;
        }
    }
    @finally
    {
        NSError *error = nil;
        if (![NSFileManager.defaultManager removeItemAtPath:root error:&error])
            Report([NSString stringWithFormat:@"Could not remove the private staging directory: %@", error.localizedDescription]);
    }
    return result;
}

static int Serve(void)
{
    if (getuid() != 0 || geteuid() != 0
        || ![SelfPath() isEqualToString:InstalledHelperPath]
        || !RootOwnedItem(InstalledHelperPath, S_IFREG))
        return Failed;
    SecRequirementRef requirement = OwnRequirement();
    if (requirement == NULL)
        return Failed;
    dispatch_semaphore_t gate = dispatch_semaphore_create(1);
    xpc_connection_t listener = xpc_connection_create_mach_service(ServiceName, dispatch_get_main_queue(),
        XPC_CONNECTION_MACH_SERVICE_LISTENER);
    xpc_connection_set_event_handler(listener, ^(xpc_object_t event) {
        if (xpc_get_type(event) != XPC_TYPE_CONNECTION)
            return;
        xpc_connection_t client = (xpc_connection_t)event;
        xpc_connection_set_event_handler(client, ^(xpc_object_t request) {
            if (xpc_get_type(request) != XPC_TYPE_DICTIONARY)
                return;
            uid_t uid = xpc_connection_get_euid(client);
            gid_t gid;
            NSString *path = Field(request, "path", PATH_MAX);
            NSString *verb = Field(request, "verb", 16);
            BOOL force = xpc_dictionary_get_bool(request, "force");
            NSString *identifier = Field(request, "id", 20);
            NSString *prompt = Field(request, "prompt", 1024);
            if (!TrustedMessage(request, requirement) || !ActiveAdministrator(uid, &gid)
                || path == nil || prompt == nil || !ValidOperation(verb, force, identifier))
            {
                Send(client, "finished", @"The native helper rejected the caller or request.", Failed);
                return;
            }
            if (dispatch_semaphore_wait(gate, DISPATCH_TIME_NOW) != 0)
            {
                Send(client, "finished", @"Another native Mac App Store operation is already running.", Failed);
                return;
            }
            Send(client, "accepted", nil, 0);
            dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
                @autoreleasepool
                {
                    int result = RunWorker(uid, path, verb, force, identifier, prompt, requirement, client);
                    Send(client, "finished", nil, result);
                    dispatch_semaphore_signal(gate);
                }
            });
        });
        xpc_connection_resume(client);
    });
    xpc_connection_resume(listener);
    dispatch_main();
}

static int Client(NSArray<NSString *> *arguments)
{
    BOOL force = NO;
    NSString *masPath = nil, *verb = nil, *identifier = nil, *prompt = nil;
    if (arguments.count == 4)
    {
        masPath = arguments[0];
        verb = arguments[1];
        identifier = arguments[2];
        prompt = arguments[3];
    }
    else if (arguments.count == 5 && [arguments[2] isEqualToString:@"--force"])
    {
        masPath = arguments[0];
        verb = arguments[1];
        force = YES;
        identifier = arguments[3];
        prompt = arguments[4];
    }
    else
        return Failed;

    if (!ValidOperation(verb, force, identifier))
        return Failed;
    gid_t gid;
    if (!ActiveAdministrator(getuid(), &gid) || !HelperReady())
        return Unavailable;
    SecRequirementRef requirement = OwnRequirement();
    if (requirement == NULL)
        return Failed;
    __block BOOL done = NO, accepted = NO;
    __block int result = Failed;
    xpc_connection_t connection = xpc_connection_create_mach_service(ServiceName, dispatch_get_main_queue(),
        XPC_CONNECTION_MACH_SERVICE_PRIVILEGED);
    xpc_connection_set_event_handler(connection, ^(xpc_object_t reply) {
        if (xpc_get_type(reply) != XPC_TYPE_DICTIONARY)
        {
            if (!accepted)
                result = Unavailable;
            else
                Report(@"The native helper connection was lost. The operation will not be retried automatically.");
            done = YES;
            return;
        }
        if (xpc_connection_get_euid(connection) != 0 || !TrustedMessage(reply, requirement))
        {
            if (!accepted)
                result = Unavailable;
            else
                Report(@"The native helper's identity could not be verified.");
            done = YES;
            return;
        }
        NSString *kind = Field(reply, "kind", 16);
        if ([kind isEqualToString:@"accepted"])
            accepted = YES;
        if ([kind isEqualToString:@"bytes"])
        {
            size_t length = 0;
            const void *bytes = xpc_dictionary_get_data(reply, "data", &length);
            if (bytes != NULL && length <= 16384)
            {
                fwrite(bytes, 1, length, stdout);
                fflush(stdout);
            }
        }
        NSString *text = Field(reply, "text", 16384);
        if (text != nil)
            Report(text);
        if ([kind isEqualToString:@"finished"])
        {
            if (xpc_get_type(xpc_dictionary_get_value(reply, "result")) != XPC_TYPE_INT64)
            {
                Report(@"The native helper returned an invalid result.");
                done = YES;
                return;
            }
            int64_t code = xpc_dictionary_get_int64(reply, "result");
            result = code == 0 || code == Unavailable || code == Canceled ? (int)code : Failed;
            done = YES;
        }
    });
    xpc_connection_resume(connection);
    xpc_object_t request = xpc_dictionary_create(NULL, NULL, 0);
    xpc_dictionary_set_string(request, "path", masPath.UTF8String);
    xpc_dictionary_set_string(request, "verb", verb.UTF8String);
    xpc_dictionary_set_bool(request, "force", force);
    xpc_dictionary_set_string(request, "id", identifier.UTF8String);
    xpc_dictionary_set_string(request, "prompt", prompt.UTF8String);
    xpc_connection_send_message(connection, request);
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:15];
    while (!done)
    {
        if (!accepted && deadline.timeIntervalSinceNow <= 0)
        {
            result = Unavailable;
            break;
        }
        [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
    }
    xpc_connection_cancel(connection);
    CFRelease(requirement);
    return result;
}

int main(int argc, const char *argv[] __unused)
{
    @autoreleasepool
    {
        if (argc < 2)
            return Failed;
        NSArray<NSString *> *arguments = NSProcessInfo.processInfo.arguments;
        NSString *verb = arguments[1];
        if ([verb isEqualToString:@"serve"] && argc == 2)
            return Serve();
        if ([verb isEqualToString:@"worker"])
            return Worker([arguments subarrayWithRange:NSMakeRange(2, arguments.count - 2)]);
        if ([verb isEqualToString:@"install-daemon"] && argc == 2)
            return InstallDaemon();
        if ([verb isEqualToString:@"remove-daemon"] && argc == 2)
            return RemoveDaemon();
        if (geteuid() == 0)
            return Failed;
        if ([verb isEqualToString:@"status"] && argc == 2)
        {
            BOOL ready = HelperReady();
            BOOL biometrics = BiometricsAvailable();
            printf("ready=%d biometrics=%d\n", ready ? 1 : 0, biometrics ? 1 : 0);
            return ready ? 0 : Unavailable;
        }
        if ([verb isEqualToString:@"can-touch-id"] && argc == 2)
            return BiometricsAvailable() ? 0 : Unavailable;
        if ([verb isEqualToString:@"run"])
            return Client([arguments subarrayWithRange:NSMakeRange(2, arguments.count - 2)]);
        Report(@"Usage: UniGetUIMasHelper install-daemon | remove-daemon | status | can-touch-id | run <mas-path> <verb> [--force] <app-id> <prompt>");
        return Failed;
    }
}

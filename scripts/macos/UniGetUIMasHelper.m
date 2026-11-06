#import <Foundation/Foundation.h>
#import <LocalAuthentication/LocalAuthentication.h>
#import <Security/Security.h>
#import <ServiceManagement/ServiceManagement.h>
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

static const char *ServiceName = "io.github.marticliment.unigetui.mashelper";
static NSString *const ServicePlist = @"io.github.marticliment.unigetui.mashelper.plist";
// These are protocol outcomes, never raw mas exit codes.
enum { Failed = 1, Unavailable = 77, SigningRequired = 78, Canceled = 128 };
static const NSUInteger MaximumExecutableSize = 128 * 1024 * 1024;

static void Report(NSString *message)
{
    fprintf(stderr, "%s\n", message.UTF8String);
    fflush(stderr);
}

static BOOL ValidOperation(NSString *verb, NSString *identifier)
{
    if (verb == nil || identifier == nil
        || ![@[@"get", @"install", @"update", @"uninstall", @"purchase", @"upgrade"] containsObject:verb]
        || identifier.length == 0 || identifier.length > 20)
        return NO;
    for (NSUInteger i = 0; i < identifier.length; i++)
        if ([identifier characterAtIndex:i] < '0' || [identifier characterAtIndex:i] > '9')
            return NO;
    return YES;
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
    struct passwd entry, *found = NULL;
    char buffer[16384];
    if (getpwuid_r(uid, &entry, buffer, sizeof(buffer), &found) != 0 || found == NULL)
        return NO;
    int groups[128], count = 128;
    if (getgrouplist(entry.pw_name, entry.pw_gid, groups, &count) < 0)
        return NO;
    // macOS's built-in admin group has GID 80.
    for (int i = 0; i < count; i++)
        if (groups[i] == 80)
        {
            *primaryGroup = entry.pw_gid;
            return YES;
        }
    return NO;
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

static BOOL AppleSignedHelper(void)
{
    SecRequirementRef requirement = NULL;
    SecCodeRef code = NULL;
    BOOL valid = NO;
    if (SecRequirementCreateWithString(CFSTR("anchor apple generic"), kSecCSDefaultFlags, &requirement) == errSecSuccess
        && SecCodeCopySelf(kSecCSDefaultFlags, &code) == errSecSuccess)
        valid = SecCodeCheckValidity(code, kSecCSDefaultFlags, requirement) == errSecSuccess;
    if (code != NULL)
        CFRelease(code);
    if (requirement != NULL)
        CFRelease(requirement);
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

static NSString *ResolveMas(NSString *path)
{
    if (![path isAbsolutePath] || ![path.lastPathComponent isEqualToString:@"mas"])
        return nil;
    char resolved[PATH_MAX];
    if (realpath(path.fileSystemRepresentation, resolved) == NULL)
        return nil;
    NSString *actual = @(resolved);
    // Homebrew 7 uses a small shell wrapper in bin and the native executable in libexec/bin.
    NSString *bundled = [[actual.stringByDeletingLastPathComponent stringByDeletingLastPathComponent]
        stringByAppendingPathComponent:@"libexec/bin/mas"];
    if ([actual.stringByDeletingLastPathComponent.lastPathComponent isEqualToString:@"bin"]
        && [NSFileManager.defaultManager fileExistsAtPath:bundled])
    {
        if (realpath(bundled.fileSystemRepresentation, resolved) == NULL)
            return nil;
        actual = @(resolved);
    }
    return actual;
}

static BOOL Snapshot(NSString *source, NSString *destination, SecRequirementRef requirement)
{
    int input = open(source.fileSystemRepresentation, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
    if (input < 0)
        return NO;
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
    if (!valid || !SystemLinkedMachO(data.bytes, data.length))
        return NO;
    int output = open(destination.fileSystemRepresentation, O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC, 0500);
    if (output < 0)
        return NO;
    offset = 0;
    while (offset < data.length)
    {
        ssize_t written = write(output, (const uint8_t *)data.bytes + offset, data.length - offset);
        if (written < 0 && errno == EINTR)
            continue;
        if (written <= 0) { valid = NO; break; }
        offset += (NSUInteger)written;
    }
    if (close(output) != 0)
        valid = NO;
    return valid && ValidSignature(destination, requirement);
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

static int AuthenticationResult(BOOL done, BOOL success, NSInteger errorCode)
{
    if (done && success)
        return 0;
    if (done && (errorCode == LAErrorUserFallback || errorCode == LAErrorBiometryLockout
        || errorCode == LAErrorBiometryNotAvailable || errorCode == LAErrorBiometryNotEnrolled))
        return Unavailable;
    return Canceled;
}

static int Authenticate(NSString *reason, uid_t uid, gid_t gid)
{
    if (setegid(gid) != 0 || seteuid(uid) != 0)
        return Failed;
    LAContext *context = [[LAContext alloc] init];
    NSError *error = nil;
    __block BOOL done = NO, success = NO;
    __block NSInteger failureCode = 0;
    if (![context canEvaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics error:&error])
    {
        Report([NSString stringWithFormat:@"Touch ID is unavailable: %@", error.localizedDescription]);
        if (seteuid(0) != 0 || setegid(0) != 0)
            return Failed;
        return Unavailable;
    }
    [context evaluatePolicy:LAPolicyDeviceOwnerAuthenticationWithBiometrics localizedReason:reason
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
    if (getuid() != 0 || geteuid() != 0 || arguments.count != 6)
        return Failed;
    unsigned long numericUid = strtoul(arguments[0].UTF8String, NULL, 10);
    if (numericUid == 0 || numericUid > UINT_MAX)
        return Failed;
    uid_t uid = (uid_t)numericUid;
    gid_t gid;
    if (!ActiveAdministrator(uid, &gid) || !ValidOperation(arguments[3], arguments[4]))
        return Failed;
    // The authoritative command is always part of the prompt, irrespective of the client's label.
    NSString *reason = [NSString stringWithFormat:@"UniGetUI: mas %@ %@\n%@\n%@",
        arguments[3], arguments[4], arguments[2], arguments[5]];
    int authorized = Authenticate(reason, uid, gid);
    if (authorized != 0)
        return authorized;
    if (!ActiveAdministrator(uid, &gid))
        return Failed;

    NSTask *task = [[NSTask alloc] init];
    task.executableURL = [NSURL fileURLWithPath:arguments[1]];
    task.arguments = @[arguments[3], arguments[4]];
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

static int RunWorker(uid_t uid, NSString *masPath, NSString *verb, NSString *identifier,
    NSString *prompt, SecRequirementRef requirement, xpc_connection_t connection)
{
    char directory[] = "/private/var/tmp/unigetui-mas.XXXXXX";
    if (mkdtemp(directory) == NULL)
    {
        Send(connection, "output", @"Could not create the private Mac App Store staging directory.", 0);
        return Failed;
    }
    NSString *root = @(directory);
    NSString *worker = [root stringByAppendingPathComponent:@"UniGetUIMasHelper"];
    NSString *stagedMas = [root stringByAppendingPathComponent:@"mas"];
    int result = Failed;
    @try
    {
        NSString *resolved = ResolveMas(masPath);
        if (resolved == nil || !Snapshot(resolved, stagedMas, NULL))
        {
            Send(connection, "output", @"The native helper requires a signed, system-linked mas executable. Use the password path for this installation of mas.", 0);
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
            [NSString stringWithFormat:@"%u", uid], stagedMas, resolved, verb, identifier, prompt];
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
    if (getuid() != 0 || geteuid() != 0 || !AppleSignedHelper())
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
            NSString *identifier = Field(request, "id", 20);
            NSString *prompt = Field(request, "prompt", 1024);
            if (!TrustedMessage(request, requirement) || !ActiveAdministrator(uid, &gid)
                || path == nil || prompt == nil || !ValidOperation(verb, identifier))
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
                    int result = RunWorker(uid, path, verb, identifier, prompt, requirement, client);
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

static int Configure(BOOL enable)
{
    if (@available(macOS 13.0, *))
    {
        if (enable && !AppleSignedHelper())
            return SigningRequired;
        SMAppService *service = [SMAppService daemonServiceWithPlistName:ServicePlist];
        NSError *error = nil;
        if (enable && service.status != SMAppServiceStatusEnabled && service.status != SMAppServiceStatusRequiresApproval
            && ![service registerAndReturnError:&error])
        {
            Report([NSString stringWithFormat:@"%@ (%@ %ld; service status %ld).",
                error.localizedDescription, error.domain, (long)error.code, (long)service.status]);
            // Recent macOS releases can submit the job but reject starting it until the user
            // approves the background item. Report that pending state, not "enabled".
            return service.status == SMAppServiceStatusRequiresApproval ? Unavailable : Failed;
        }
        if (!enable)
        {
            if (![service unregisterAndReturnError:&error])
            {
                Report(error.localizedDescription);
                return Failed;
            }
            return 0;
        }
        if (service.status == SMAppServiceStatusRequiresApproval)
        {
            [SMAppService openSystemSettingsLoginItems];
            return Unavailable;
        }
        if (service.status == SMAppServiceStatusEnabled)
            return 0;
        Report([NSString stringWithFormat:@"Service status %ld for bundle %@ (%@).",
            (long)service.status, NSBundle.mainBundle.bundlePath, NSBundle.mainBundle.bundleIdentifier]);
        return Failed;
    }
    return Unavailable;
}

static int Client(NSArray<NSString *> *arguments)
{
    if (arguments.count != 4 || !ValidOperation(arguments[1], arguments[2]))
        return Failed;
    if (!AppleSignedHelper())
        return SigningRequired;
    if (@available(macOS 13.0, *))
    {
        if ([SMAppService daemonServiceWithPlistName:ServicePlist].status != SMAppServiceStatusEnabled)
            return Unavailable;
    }
    else
        return Unavailable;
    gid_t gid;
    if (!ActiveAdministrator(getuid(), &gid))
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
            Report(@"The native helper connection was lost. The operation will not be retried automatically.");
            done = YES;
            return;
        }
        if (xpc_connection_get_euid(connection) != 0 || !TrustedMessage(reply, requirement))
        {
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
    xpc_dictionary_set_string(request, "path", arguments[0].UTF8String);
    xpc_dictionary_set_string(request, "verb", arguments[1].UTF8String);
    xpc_dictionary_set_string(request, "id", arguments[2].UTF8String);
    xpc_dictionary_set_string(request, "prompt", arguments[3].UTF8String);
    xpc_connection_send_message(connection, request);
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:15];
    while (!done)
    {
        if (!accepted && deadline.timeIntervalSinceNow <= 0)
        {
            Report(@"The native helper did not acknowledge the request. It will not be retried automatically.");
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
        if (geteuid() == 0)
            return Failed;
        if ([verb isEqualToString:@"enable"] && argc == 2)
            return Configure(YES);
        if ([verb isEqualToString:@"disable"] && argc == 2)
            return Configure(NO);
        if ([verb isEqualToString:@"status"] && argc == 2)
        {
            if (@available(macOS 13.0, *))
            {
                SMAppServiceStatus status = [SMAppService daemonServiceWithPlistName:ServicePlist].status;
                printf("Native helper service status: %ld (0=not registered, 1=enabled, 2=requires approval, 3=not found)\n",
                    (long)status);
                return 0;
            }
            return Unavailable;
        }
        if ([verb isEqualToString:@"run"])
            return Client([arguments subarrayWithRange:NSMakeRange(2, arguments.count - 2)]);
        Report(@"Usage: UniGetUIMasHelper enable | disable | status | run <mas-path> <verb> <app-id> <prompt>");
        return Failed;
    }
}

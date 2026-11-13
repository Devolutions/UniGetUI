//
// UniGetUIMasHelper.m
// UniGetUI
//
// Built-in Mac App Store catalog/Spotlight/CommerceKit helper and Touch ID LaunchDaemon.
// Portions adapted from mas-cli/mas (MIT License, Copyright (c) 2015 Andrew Naylor, Ross Goldberg);
// see src/UniGetUI.PackageEngine.Managers.MacAppStore/THIRD_PARTY_NOTICES.md.
//

#import <AppKit/AppKit.h>
#import <CoreServices/CoreServices.h>
#import <Foundation/Foundation.h>
#import <LocalAuthentication/LocalAuthentication.h>
#import <Security/Security.h>
#import <SystemConfiguration/SystemConfiguration.h>
#include <xpc/xpc.h>
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <mach-o/fat.h>
#include <libkern/OSByteOrder.h>
#include <libproc.h>
#include <fcntl.h>
#include <grp.h>
#include <pwd.h>
#include <signal.h>
#include <sys/stat.h>
#include <sys/sysctl.h>
#include <unistd.h>

// ── Apple PrivateFrameworks declarations (StoreFoundation & CommerceKit) ───

@protocol ISAccountService <NSObject>
- (void)storeFrontWithReplyBlock:(void (^)(NSString *storeFront))block;
@end

@interface ISServiceProxy : NSObject
+ (instancetype)genericSharedProxy;
@property (readonly, nonatomic) id<ISAccountService> accountService;
@end

@interface SSDownloadPhase : NSObject
@property (readonly) long long phaseType;
@property (readonly) long long progressUnits;
@property (readonly) long long progressValue;
@property (readonly) long long totalProgressValue;
@end

@interface SSDownloadStatus : NSObject
@property (readonly, nonatomic) SSDownloadPhase *activePhase;
@property (nonatomic, getter=isCancelled) BOOL cancelled;
@property (retain, nonatomic) NSError *error;
@property (nonatomic, getter=isFailed) BOOL failed;
@property (readonly, nonatomic) float percentComplete;
@property (readonly, nonatomic) float phasePercentComplete;
@end

@interface SSDownloadMetadata : NSObject
- (instancetype)initWithKind:(NSString *)kind;
@property unsigned long long itemIdentifier;
@property (retain) NSString *bundleVersion;
@property (retain) NSString *title;
@end

@interface SSDownload : NSObject
@property (copy) NSString *installPath;
@property (copy, nonatomic) SSDownloadMetadata *metadata;
@property (retain, nonatomic) SSDownloadStatus *status;
@end

@interface SSPurchase : NSObject
+ (instancetype)purchaseWithBuyParameters:(NSString *)buyParameters;
@property (copy, nonatomic) NSString *buyParameters;
@property (copy, nonatomic) SSDownloadMetadata *downloadMetadata;
@property BOOL isRedownload;
@property BOOL isUpdate;
@property unsigned long long itemIdentifier;
@end

@interface SSPurchaseResponse : NSObject
@property (retain) NSArray<SSDownload *> *downloads;
@end

@class CKDownloadQueue;

@protocol CKDownloadQueueObserver <NSObject>
@required
- (void)downloadQueue:(CKDownloadQueue *)queue changedWithAddition:(SSDownload *)download;
- (void)downloadQueue:(CKDownloadQueue *)queue changedWithRemoval:(SSDownload *)download;
- (void)downloadQueue:(CKDownloadQueue *)queue statusChangedForDownload:(SSDownload *)download;
@end

@interface CKDownloadQueue : NSObject
+ (instancetype)sharedDownloadQueue;
- (NSString *)addObserver:(id<CKDownloadQueueObserver>)observer;
- (void)removeObserver:(NSString *)observerUUID;
@end

typedef void (^SSPurchaseCompletion)(SSPurchase *purchase, BOOL completed, NSError *error, SSPurchaseResponse *response);

@interface CKPurchaseController : NSObject
+ (instancetype)sharedPurchaseController;
- (void)performPurchase:(SSPurchase *)purchase withOptions:(unsigned long long)options completionHandler:(SSPurchaseCompletion)handler;
@end

extern NSString *CKDownloadDirectory(NSString *target);

// ── Constants & Protocol Outcomes ──────────────────────────────────────────

static const char *ServiceName = "com.devolutions.unigetui.mashelper";
static NSString *const ServiceLabel = @"com.devolutions.unigetui.mashelper";
static NSString *const LegacyServiceLabel = @"io.github.marticliment.unigetui.mashelper";
static NSString *const HelperDirectory = @"/Library/PrivilegedHelperTools";
static NSString *const InstalledHelperPath = @"/Library/PrivilegedHelperTools/com.devolutions.unigetui.mashelper";
static NSString *const DaemonDirectory = @"/Library/LaunchDaemons";
static NSString *const DaemonPlistPath = @"/Library/LaunchDaemons/com.devolutions.unigetui.mashelper.plist";
static NSString *const StagingParentPath = @"/private/var/run/com.devolutions.unigetui.mashelper";
static NSString *const ProgressPrefix = @"@@UNIGETUI_PROGRESS@@";

// These are protocol outcomes, never raw subprocess exit codes.
enum { Failed = 1, Unavailable = 77, Canceled = 128 };
static const NSUInteger MaximumExecutableSize = 128 * 1024 * 1024;
static const int MaxSymlinkHops = 32;

static void Report(NSString *message)
{
    fprintf(stderr, "%s\n", message.UTF8String);
    fflush(stderr);
}

static void OutputLine(NSString *message)
{
    fprintf(stdout, "%s\n", message.UTF8String);
    fflush(stdout);
}

static void EmitProgress(NSString *stage, int percent, long long downloaded, long long total)
{
    fprintf(stdout, "%s %s %d %lld %lld\n",
        ProgressPrefix.UTF8String, stage.UTF8String, percent, downloaded, total);
    fflush(stdout);
}

// ── Policy, Identity & Binary Integrity ────────────────────────────────────

static BOOL ValidOperation(NSString *verb, BOOL force, NSString *identifier)
{
    if (verb == nil || identifier == nil || identifier.length == 0 || identifier.length > 20)
        return NO;
    NSArray<NSString *> *allowed = force
        ? @[@"get", @"install", @"update"]
        : @[@"get", @"install", @"update", @"uninstall"];
    if (![allowed containsObject:verb])
        return NO;
    for (NSUInteger i = 0; i < identifier.length; i++)
        if ([identifier characterAtIndex:i] < '0' || [identifier characterAtIndex:i] > '9')
            return NO;
    unsigned long long parsed = strtoull(identifier.UTF8String, NULL, 10);
    return parsed > 0;
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

static BOOL Snapshot(NSString *source, NSString *destination, SecRequirementRef requirement)
{
    NSData *data = ReadBoundedRegularFile(source);
    if (data == nil || !SystemLinkedMachO(data.bytes, data.length))
        return NO;
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
    return @{
        @"PATH": @"/usr/bin:/bin:/usr/sbin:/sbin",
        @"SUDO_UID": [NSString stringWithFormat:@"%u", uid],
        @"SUDO_GID": [NSString stringWithFormat:@"%u", gid],
        @"LC_CTYPE": @"UTF-8",
    };
}

static int RunTool(NSString *executable, NSArray<NSString *> *arguments, NSString **combinedOutput)
{
    NSTask *task = [[NSTask alloc] init];
    NSPipe *pipe = [NSPipe pipe];
    task.executableURL = [NSURL fileURLWithPath:executable];
    task.arguments = arguments;
    task.environment = @{ @"PATH": @"/usr/bin:/bin:/usr/sbin:/sbin", @"LC_CTYPE": @"UTF-8" };
    task.currentDirectoryURL = [NSURL fileURLWithPath:@"/"];
    task.standardInput = [NSFileHandle fileHandleWithNullDevice];
    task.standardOutput = pipe;
    task.standardError = pipe;
    NSError *error = nil;
    if (![task launchAndReturnError:&error])
    {
        if (combinedOutput != NULL)
            *combinedOutput = error.localizedDescription ?: @"";
        return Failed;
    }
    [pipe.fileHandleForWriting closeFile];
    NSData *data = [pipe.fileHandleForReading readDataToEndOfFile];
    [task waitUntilExit];
    if (combinedOutput != NULL)
        *combinedOutput = [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"";
    return task.terminationReason == NSTaskTerminationReasonExit ? task.terminationStatus : Failed;
}

// ── Spotlight Installed-App Discovery & Storefront ─────────────────────────

@interface MasInstalledItem : NSObject
@property (nonatomic) unsigned long long adamID;
@property (nonatomic, copy) NSString *bundleID;
@property (nonatomic, copy) NSString *name;
@property (nonatomic, copy) NSString *path;
@property (nonatomic, copy) NSString *version;
@end

@implementation MasInstalledItem
@end

static NSArray<NSURL *> *ApplicationFolderURLs(void)
{
    NSMutableArray<NSURL *> *urls = [NSMutableArray arrayWithObject:[NSURL fileURLWithPath:@"/Applications" isDirectory:YES]];
    NSUserDefaults *storeDefaults = [[NSUserDefaults alloc] initWithSuiteName:@"com.apple.appstored"];
    NSDictionary *preferredVolume = [storeDefaults dictionaryForKey:@"PreferredVolume"];
    NSString *volumeName = [preferredVolume[@"name"] isKindOfClass:NSString.class] ? preferredVolume[@"name"] : nil;
    if (volumeName.length > 0 && ![volumeName containsString:@"/"] && ![volumeName isEqualToString:@".."])
    {
        NSString *externalApps = [@"/Volumes" stringByAppendingPathComponent:
            [volumeName stringByAppendingPathComponent:@"Applications"]];
        BOOL isDir = NO;
        if ([NSFileManager.defaultManager fileExistsAtPath:externalApps isDirectory:&isDir] && isDir)
            [urls addObject:[NSURL fileURLWithPath:externalApps isDirectory:YES]];
    }
    return urls;
}

static NSString *CanonicalPath(NSString *rawPath)
{
    if (rawPath.length == 0)
        return @"";
    NSURL *url = [NSURL fileURLWithPath:rawPath isDirectory:YES];
    NSString *canonical = nil;
    if ([url getResourceValue:&canonical forKey:NSURLCanonicalPathKey error:NULL] && canonical.length > 0)
        return canonical;
    return rawPath;
}

static BOOL HasAppStoreReceiptOrMetadata(NSURL *appURL)
{
    NSURL *receiptURL = [appURL URLByAppendingPathComponent:@"Contents/_MASReceipt/receipt"];
    NSNumber *size = nil;
    if ([receiptURL getResourceValue:&size forKey:NSURLFileSizeKey error:NULL] && size.unsignedLongLongValue > 0)
        return YES;
    NSURL *wrapperMeta = [appURL URLByAppendingPathComponent:@"Wrapper/iTunesMetadata.plist"];
    return [wrapperMeta getResourceValue:&size forKey:NSURLFileSizeKey error:NULL] && size.unsignedLongLongValue > 0;
}

static MasInstalledItem *ItemFromBundleAtURL(NSURL *appURL)
{
    NSString *path = CanonicalPath(appURL.path);
    MDItemRef mdItem = MDItemCreateWithURL(kCFAllocatorDefault, (__bridge CFURLRef)[NSURL fileURLWithPath:path isDirectory:YES]);
    unsigned long long adamID = 0;
    NSString *bundleID = nil;
    NSString *name = nil;
    NSString *version = nil;
    if (mdItem != NULL)
    {
        CFTypeRef rawAdam = MDItemCopyAttribute(mdItem, CFSTR("kMDItemAppStoreAdamID"));
        if (rawAdam != NULL)
        {
            if (CFGetTypeID(rawAdam) == CFNumberGetTypeID())
                adamID = ((__bridge NSNumber *)rawAdam).unsignedLongLongValue;
            CFRelease(rawAdam);
        }
        CFTypeRef rawBundle = MDItemCopyAttribute(mdItem, kMDItemCFBundleIdentifier);
        if (rawBundle != NULL)
        {
            if (CFGetTypeID(rawBundle) == CFStringGetTypeID())
                bundleID = [(__bridge NSString *)rawBundle copy];
            CFRelease(rawBundle);
        }
        CFTypeRef rawDisplay = MDItemCopyAttribute(mdItem, CFSTR("_kMDItemDisplayNameWithExtensions"));
        if (rawDisplay != NULL)
        {
            if (CFGetTypeID(rawDisplay) == CFStringGetTypeID())
                name = [(__bridge NSString *)rawDisplay copy];
            CFRelease(rawDisplay);
        }
        CFTypeRef rawVersion = MDItemCopyAttribute(mdItem, kMDItemVersion);
        if (rawVersion != NULL)
        {
            if (CFGetTypeID(rawVersion) == CFStringGetTypeID())
                version = [(__bridge NSString *)rawVersion copy];
            CFRelease(rawVersion);
        }
        CFRelease(mdItem);
    }

    // Wrapped Apple Silicon iOS/iPadOS apps also store metadata in Wrapper/iTunesMetadata.plist.
    if (adamID == 0)
    {
        NSDictionary *itunesMeta = [NSDictionary dictionaryWithContentsOfURL:
            [appURL URLByAppendingPathComponent:@"Wrapper/iTunesMetadata.plist"]];
        if ([itunesMeta[@"itemId"] respondsToSelector:@selector(unsignedLongLongValue)])
            adamID = [itunesMeta[@"itemId"] unsignedLongLongValue];
        if (bundleID.length == 0 && [itunesMeta[@"softwareVersionBundleId"] isKindOfClass:NSString.class])
            bundleID = itunesMeta[@"softwareVersionBundleId"];
        if (version.length == 0 && [itunesMeta[@"bundleShortVersionString"] isKindOfClass:NSString.class])
            version = itunesMeta[@"bundleShortVersionString"];
        if (name.length == 0 && [itunesMeta[@"itemName"] isKindOfClass:NSString.class])
            name = itunesMeta[@"itemName"];
    }
    if (adamID == 0)
        return nil;

    NSBundle *bundle = [NSBundle bundleWithPath:path];
    if (bundleID.length == 0)
        bundleID = bundle.bundleIdentifier ?: @"";
    if (version.length == 0)
        version = [bundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"";
    if (name.length == 0)
        name = path.lastPathComponent;
    if ([name hasSuffix:@".app"])
        name = [name substringToIndex:name.length - 4];

    MasInstalledItem *item = [[MasInstalledItem alloc] init];
    item.adamID = adamID;
    item.bundleID = bundleID ?: @"";
    item.name = name ?: @"";
    item.path = path;
    item.version = version ?: @"";
    return item;
}

static NSArray<MasInstalledItem *> *QuerySpotlightApps(NSArray<NSURL *> *scopes)
{
    NSMetadataQuery *query = [[NSMetadataQuery alloc] init];
    query.predicate = [NSPredicate predicateWithFormat:@"kMDItemAppStoreAdamID LIKE '*'"];
    query.searchScopes = scopes;
    __block BOOL gathered = NO;
    id token = [NSNotificationCenter.defaultCenter addObserverForName:NSMetadataQueryDidFinishGatheringNotification
        object:query
        queue:nil
        usingBlock:^(__unused NSNotification *note) {
            gathered = YES;
        }];
    if (![query startQuery])
    {
        [NSNotificationCenter.defaultCenter removeObserver:token];
        return @[];
    }
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:15.0];
    while (!gathered && deadline.timeIntervalSinceNow > 0)
        [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.02]];
    [query disableUpdates];
    [query stopQuery];
    [NSNotificationCenter.defaultCenter removeObserver:token];

    NSMutableArray<MasInstalledItem *> *items = [NSMutableArray array];
    for (id raw in query.results)
    {
        if (![raw isKindOfClass:NSMetadataItem.class])
            continue;
        NSMetadataItem *meta = (NSMetadataItem *)raw;
        NSNumber *adamNumber = [meta valueForAttribute:@"kMDItemAppStoreAdamID"];
        unsigned long long adamID = [adamNumber respondsToSelector:@selector(unsignedLongLongValue)]
            ? adamNumber.unsignedLongLongValue : 0;
        if (adamID == 0) // Exclude TestFlight apps (adamID == 0).
            continue;
        NSString *rawPath = [meta valueForAttribute:(NSString *)kMDItemPath];
        if (![rawPath isKindOfClass:NSString.class] || rawPath.length == 0)
            continue;
        NSString *path = CanonicalPath(rawPath);
        BOOL isDir = NO;
        if (![NSFileManager.defaultManager fileExistsAtPath:path isDirectory:&isDir] || !isDir)
            continue;

        NSString *name = [meta valueForAttribute:@"_kMDItemDisplayNameWithExtensions"];
        if (![name isKindOfClass:NSString.class] || name.length == 0)
            name = [meta valueForAttribute:(NSString *)kMDItemDisplayName];
        if (![name isKindOfClass:NSString.class] || name.length == 0)
            name = path.lastPathComponent;
        if ([name hasSuffix:@".app"])
            name = [name substringToIndex:name.length - 4];

        NSString *bundleID = [meta valueForAttribute:(NSString *)kMDItemCFBundleIdentifier];
        if (![bundleID isKindOfClass:NSString.class])
            bundleID = @"";
        NSString *version = [meta valueForAttribute:(NSString *)kMDItemVersion];
        if (![version isKindOfClass:NSString.class] || version.length == 0)
            version = [[NSBundle bundleWithPath:path] objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"";

        MasInstalledItem *item = [[MasInstalledItem alloc] init];
        item.adamID = adamID;
        item.bundleID = bundleID;
        item.name = name;
        item.path = path;
        item.version = version;
        [items addObject:item];
    }
    return items;
}

static NSArray<MasInstalledItem *> *DiscoverInstalledApps(BOOL autoIndex)
{
    NSArray<NSURL *> *scopes = ApplicationFolderURLs();
    NSMutableArray<MasInstalledItem *> *apps = [QuerySpotlightApps(scopes) mutableCopy] ?: [NSMutableArray array];
    NSMutableSet<NSString *> *seenPaths = [NSMutableSet setWithCapacity:apps.count];
    for (MasInstalledItem *item in apps)
        [seenPaths addObject:item.path];

    if (autoIndex)
    {
        for (NSURL *folderURL in scopes)
        {
            NSDirectoryEnumerator<NSURL *> *enumerator = [NSFileManager.defaultManager
                enumeratorAtURL:folderURL
                includingPropertiesForKeys:@[NSURLIsDirectoryKey]
                options:NSDirectoryEnumerationSkipsHiddenFiles
                errorHandler:nil];
            for (NSURL *entry in enumerator)
            {
                if (![entry.pathExtension isEqualToString:@"app"])
                    continue;
                NSNumber *isDir = nil;
                if (![entry getResourceValue:&isDir forKey:NSURLIsDirectoryKey error:NULL] || !isDir.boolValue)
                    continue;
                [enumerator skipDescendants];
                if (!HasAppStoreReceiptOrMetadata(entry))
                    continue;
                NSString *canonical = CanonicalPath(entry.path);
                if ([seenPaths containsObject:canonical])
                    continue;
                (void)RunTool(@"/usr/bin/mdimport", @[canonical], NULL);
                MasInstalledItem *indexed = ItemFromBundleAtURL(entry);
                if (indexed != nil)
                {
                    [seenPaths addObject:indexed.path];
                    [apps addObject:indexed];
                }
            }
        }
    }

    [apps sortUsingComparator:^NSComparisonResult(MasInstalledItem *left, MasInstalledItem *right) {
        return [left.name localizedStandardCompare:right.name];
    }];
    return apps;
}

static int ListCommand(void)
{
    for (MasInstalledItem *app in DiscoverInstalledApps(YES))
    {
        NSDictionary *row = @{
            @"adamID": @(app.adamID),
            @"bundleID": app.bundleID ?: @"",
            @"name": app.name ?: @"",
            @"path": app.path ?: @"",
            @"version": app.version ?: @"",
        };
        NSData *json = [NSJSONSerialization dataWithJSONObject:row options:0 error:NULL];
        if (json != nil)
        {
            fwrite(json.bytes, 1, json.length, stdout);
            fputc('\n', stdout);
        }
    }
    fflush(stdout);
    return 0;
}

static int StorefrontCommand(void)
{
    __block NSString *storeFront = nil;
    __block BOOL done = NO;
    [[ISServiceProxy genericSharedProxy].accountService storeFrontWithReplyBlock:^(NSString *value) {
        storeFront = [value copy];
        done = YES;
    }];
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:2.0];
    while (!done && deadline.timeIntervalSinceNow > 0)
        [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.02]];

    if (storeFront.length == 0)
        storeFront = [NSLocale.currentLocale objectForKey:NSLocaleCountryCode] ?: @"US";
    OutputLine(storeFront);
    return 0;
}

static int ResetCommand(void)
{
    for (NSRunningApplication *app in [NSRunningApplication runningApplicationsWithBundleIdentifier:@"com.apple.storeuid"])
    {
        if (![app terminate])
            [app forceTerminate];
    }

    NSSet<NSString *> *agentPaths = [NSSet setWithArray:@[
        @"/System/Library/Frameworks/StoreKit.framework/Support/storekitagent",
        @"/System/Library/PrivateFrameworks/AppStoreComponents.framework/Support/appstorecomponentsd",
        @"/System/Library/PrivateFrameworks/AppStoreDaemon.framework/Support/appstoreagent",
        @"/System/Library/PrivateFrameworks/CascadeSets.framework/Versions/A/XPCServices/SetStoreUpdateService.xpc/Contents/MacOS/SetStoreUpdateService",
        @"/System/Library/PrivateFrameworks/CommerceKit.framework/Versions/A/Resources/storeaccountd",
        @"/System/Library/PrivateFrameworks/CommerceKit.framework/Versions/A/Resources/storeassetd",
        @"/System/Library/PrivateFrameworks/CommerceKit.framework/Versions/A/Resources/storedownloadd",
        @"/System/Library/PrivateFrameworks/CommerceKit.framework/Versions/A/Resources/storeinstalld",
        @"/System/Library/PrivateFrameworks/CommerceKit.framework/Versions/A/Resources/storelegacy",
    ]];

    int mib[3] = { CTL_KERN, KERN_PROC, KERN_PROC_ALL };
    size_t length = 0;
    if (sysctl(mib, 3, NULL, &length, NULL, 0) == 0 && length > 0)
    {
        size_t count = length / sizeof(struct kinfo_proc);
        struct kinfo_proc *procs = calloc(count, sizeof(struct kinfo_proc));
        if (procs != NULL)
        {
            if (sysctl(mib, 3, procs, &length, NULL, 0) == 0)
            {
                size_t actual = length / sizeof(struct kinfo_proc);
                uid_t myUid = getuid();
                char pathBuf[PROC_PIDPATHINFO_MAXSIZE];
                for (size_t i = 0; i < actual; i++)
                {
                    if (procs[i].kp_eproc.e_ucred.cr_uid != myUid)
                        continue;
                    pid_t pid = procs[i].kp_proc.p_pid;
                    if (pid > 0 && proc_pidpath(pid, pathBuf, sizeof(pathBuf)) > 0)
                    {
                        NSString *exe = @(pathBuf);
                        if ([agentPaths containsObject:exe])
                            (void)kill(pid, SIGTERM);
                    }
                }
            }
            free(procs);
        }
    }

    NSString *downloadDir = CKDownloadDirectory(nil);
    if (downloadDir.length > 0 && [NSFileManager.defaultManager fileExistsAtPath:downloadDir])
    {
        NSError *error = nil;
        if (![NSFileManager.defaultManager removeItemAtPath:downloadDir error:&error])
        {
            Report([NSString stringWithFormat:@"Could not clear cached App Store downloads: %@", error.localizedDescription]);
            return Failed;
        }
    }
    OutputLine(@"Cleared cached Mac App Store downloads and restarted user store agents.");
    return 0;
}

// ── Native CommerceKit Download, Receipt Installer & Trash Uninstaller ─────

static BOOL SameInode(NSString *first, NSString *second)
{
    if (first.length == 0 || second.length == 0)
        return NO;
    struct stat a, b;
    return lstat(first.fileSystemRepresentation, &a) == 0
        && lstat(second.fileSystemRepresentation, &b) == 0
        && a.st_dev == b.st_dev
        && a.st_ino == b.st_ino;
}

static NSString *RefreshHardLink(NSString *sourcePath, NSString *existingLinkPath,
    NSURL *stagingDirectoryURL, unsigned long long adamID)
{
    if (sourcePath.length == 0 || SameInode(sourcePath, existingLinkPath))
        return existingLinkPath;
    if (existingLinkPath.length > 0)
        unlink(existingLinkPath.fileSystemRepresentation);

    NSString *linkName = [NSString stringWithFormat:@"%llu-%llu-%@",
        adamID, (unsigned long long)(NSDate.date.timeIntervalSince1970 * 1000.0), sourcePath.lastPathComponent];
    NSString *destination = [stagingDirectoryURL.path stringByAppendingPathComponent:linkName];
    unlink(destination.fileSystemRepresentation);
    if (link(sourcePath.fileSystemRepresentation, destination.fileSystemRepresentation) == 0)
        return destination;
    return existingLinkPath;
}

static NSString *InstalledBundlePathFromInstallerLog(NSString *installerOutput)
{
    if (installerOutput.length == 0)
        return nil;
    NSRegularExpression *regex = [NSRegularExpression
        regularExpressionWithPattern:@"PackageKit: Registered bundle (\\S+) for uid 0"
        options:0
        error:NULL];
    NSArray<NSTextCheckingResult *> *matches = [regex matchesInString:installerOutput
        options:0
        range:NSMakeRange(0, installerOutput.length)];
    NSString *shortestURLString = nil;
    for (NSTextCheckingResult *match in matches)
    {
        if (match.numberOfRanges < 2)
            continue;
        NSString *candidate = [installerOutput substringWithRange:[match rangeAtIndex:1]];
        if (shortestURLString == nil || candidate.length < shortestURLString.length)
            shortestURLString = candidate;
    }
    if (shortestURLString == nil)
        return nil;
    NSURL *url = [NSURL URLWithString:shortestURLString];
    if (url == nil || !url.isFileURL)
        return nil;
    NSString *path = CanonicalPath(url.path);
    while ([path hasSuffix:@"/"] && path.length > 1)
        path = [path substringToIndex:path.length - 1];
    if ((![path hasPrefix:@"/Applications/"] && ![path hasPrefix:@"/Volumes/"])
        || ![path.pathExtension isEqualToString:@"app"]
        || [path containsString:@"/../"])
        return nil;
    return path;
}

static BOOL IsAllowedAppBundleForUninstall(NSString *appPath)
{
    if (appPath.length == 0 || ![appPath.pathExtension isEqualToString:@"app"] || [appPath containsString:@"/../"])
        return NO;
    struct stat status;
    if (lstat(appPath.fileSystemRepresentation, &status) != 0 || !S_ISDIR(status.st_mode))
        return NO;
    for (NSURL *scope in ApplicationFolderURLs())
    {
        NSString *prefix = [CanonicalPath(scope.path) stringByAppendingString:@"/"];
        if ([appPath hasPrefix:prefix] && appPath.length > prefix.length)
            return YES;
    }
    return NO;
}

@interface MasDownloadObserver : NSObject <CKDownloadQueueObserver>
@property (nonatomic) unsigned long long adamID;
@property (nonatomic, copy) NSString *appDownloadFolder;
@property (nonatomic, strong) NSURL *stagingDirectoryURL;
@property (nonatomic, copy) NSString *linkedPkgPath;
@property (nonatomic, copy) NSString *linkedReceiptPath;
@property (nonatomic, copy) NSString *appTitleAndVersion;
@property (nonatomic, copy) NSString *installedPath;
@property (nonatomic) long long lastPhaseType;
@property (nonatomic) int lastPercent;
@property (nonatomic, strong) NSDate *lastActivity;
@property (nonatomic) BOOL finished;
@property (nonatomic) BOOL wasCancelled;
@property (nonatomic) BOOL wasFailed;
@property (nonatomic, strong) NSError *error;
- (void)captureDownloadArtifacts;
@end

@implementation MasDownloadObserver

- (instancetype)init
{
    if ((self = [super init]))
    {
        _lastPhaseType = -99;
        _lastPercent = -1;
        _lastActivity = NSDate.date;
        _appTitleAndVersion = @"app";
    }
    return self;
}

- (void)captureDownloadArtifacts
{
    if (self.appDownloadFolder.length == 0 || self.stagingDirectoryURL == nil)
        return;
    NSArray<NSURL *> *entries = [NSFileManager.defaultManager
        contentsOfDirectoryAtURL:[NSURL fileURLWithPath:self.appDownloadFolder isDirectory:YES]
        includingPropertiesForKeys:@[NSURLIsRegularFileKey, NSURLContentModificationDateKey]
        options:0
        error:NULL];
    NSURL *newestPkg = nil;
    NSDate *newestPkgDate = nil;
    NSURL *receiptURL = nil;
    for (NSURL *entry in entries)
    {
        NSNumber *isRegular = nil;
        if (![entry getResourceValue:&isRegular forKey:NSURLIsRegularFileKey error:NULL] || !isRegular.boolValue)
            continue;
        if ([entry.pathExtension isEqualToString:@"pkg"])
        {
            NSDate *modified = nil;
            [entry getResourceValue:&modified forKey:NSURLContentModificationDateKey error:NULL];
            if (newestPkg == nil || (modified != nil && (newestPkgDate == nil || [modified compare:newestPkgDate] == NSOrderedDescending)))
            {
                newestPkg = entry;
                newestPkgDate = modified;
            }
        }
        else if ([entry.lastPathComponent isEqualToString:@"receipt"])
            receiptURL = entry;
    }
    if (newestPkg != nil)
        self.linkedPkgPath = RefreshHardLink(newestPkg.path, self.linkedPkgPath, self.stagingDirectoryURL, self.adamID);
    if (receiptURL != nil)
        self.linkedReceiptPath = RefreshHardLink(receiptURL.path, self.linkedReceiptPath, self.stagingDirectoryURL, self.adamID);
}

- (void)updateMetadataFromDownload:(SSDownload *)download
{
    SSDownloadMetadata *meta = download.metadata;
    if (meta.title.length > 0 && meta.bundleVersion.length > 0)
        self.appTitleAndVersion = [NSString stringWithFormat:@"%@ (%@)", meta.title, meta.bundleVersion];
    else if (meta.title.length > 0)
        self.appTitleAndVersion = meta.title;
    if (download.installPath.length > 0)
        self.installedPath = download.installPath;
}

- (void)downloadQueue:(__unused CKDownloadQueue *)queue changedWithAddition:(SSDownload *)download
{
    if (download.metadata == nil || download.metadata.itemIdentifier != self.adamID)
        return;
    self.lastActivity = NSDate.date;
    [self updateMetadataFromDownload:download];
}

- (void)downloadQueue:(__unused CKDownloadQueue *)queue statusChangedForDownload:(SSDownload *)download
{
    if (download.metadata == nil || download.metadata.itemIdentifier != self.adamID || download.status == nil)
        return;
    SSDownloadStatus *status = download.status;
    if (status.isCancelled || status.isFailed)
        return;

    self.lastActivity = NSDate.date;
    [self updateMetadataFromDownload:download];
    [self captureDownloadArtifacts];

    SSDownloadPhase *phase = status.activePhase;
    long long phaseType = phase != nil ? phase.phaseType : -1;
    int percent = (int)llround((double)status.phasePercentComplete * 100.0);
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;

    if (phaseType != self.lastPhaseType)
    {
        if (phaseType == 0)
            OutputLine([NSString stringWithFormat:@"Downloading %@...", self.appTitleAndVersion]);
        else if (phaseType == 5)
            OutputLine([NSString stringWithFormat:@"Downloaded %@", self.appTitleAndVersion]);
        else if (phaseType == 1)
            OutputLine([NSString stringWithFormat:@"Installing %@...", self.appTitleAndVersion]);
    }

    if (phaseType != self.lastPhaseType || percent != self.lastPercent)
    {
        self.lastPhaseType = phaseType;
        self.lastPercent = percent;
        if (phaseType == 0)
        {
            long long downloaded = (phase != nil && phase.progressUnits == 0 && phase.progressValue > 0)
                ? phase.progressValue : 0;
            long long total = (phase != nil && phase.progressUnits == 0 && phase.totalProgressValue > 4096)
                ? phase.totalProgressValue : 0;
            EmitProgress(@"downloading", percent, downloaded, total);
        }
        else if (phaseType == 1 || phaseType == 5)
        {
            EmitProgress(@"installing", -1, 0, 0);
        }
    }
}

- (void)downloadQueue:(__unused CKDownloadQueue *)queue changedWithRemoval:(SSDownload *)download
{
    if (download.metadata == nil || download.metadata.itemIdentifier != self.adamID)
        return;
    self.lastActivity = NSDate.date;
    [self updateMetadataFromDownload:download];
    [self captureDownloadArtifacts];
    if (download.status != nil)
    {
        self.wasCancelled = download.status.isCancelled;
        self.wasFailed = download.status.isFailed;
        self.error = download.status.error;
    }
    self.finished = YES;
}

@end

static BOOL CopyReceiptAsRoot(NSString *receiptSourcePath, NSString *installedAppPath)
{
    NSString *contentsPath = [installedAppPath stringByAppendingPathComponent:@"Contents"];
    struct stat st;
    if (lstat(installedAppPath.fileSystemRepresentation, &st) != 0 || !S_ISDIR(st.st_mode)
        || lstat(contentsPath.fileSystemRepresentation, &st) != 0 || !S_ISDIR(st.st_mode))
        return NO;

    NSString *receiptDir = [contentsPath stringByAppendingPathComponent:@"_MASReceipt"];
    if (lstat(receiptDir.fileSystemRepresentation, &st) != 0)
    {
        if (errno != ENOENT || mkdir(receiptDir.fileSystemRepresentation, 0755) != 0)
            return NO;
    }
    else if (!S_ISDIR(st.st_mode))
        return NO;

    if (lchown(receiptDir.fileSystemRepresentation, 0, 0) != 0
        || chmod(receiptDir.fileSystemRepresentation, 0755) != 0)
        return NO;

    NSData *receiptBytes = ReadBoundedRegularFile(receiptSourcePath);
    if (receiptBytes == nil)
        return NO;

    NSString *tempPattern = [receiptDir stringByAppendingPathComponent:@".receipt.XXXXXX"];
    char *tempPath = strdup(tempPattern.fileSystemRepresentation);
    if (tempPath == NULL)
        return NO;
    int fd = mkstemp(tempPath);
    if (fd < 0)
    {
        free(tempPath);
        return NO;
    }
    BOOL ok = fchown(fd, 0, 0) == 0 && fchmod(fd, 0644) == 0;
    NSUInteger offset = 0;
    while (ok && offset < receiptBytes.length)
    {
        ssize_t written = write(fd, (const uint8_t *)receiptBytes.bytes + offset, receiptBytes.length - offset);
        if (written < 0 && errno == EINTR)
            continue;
        if (written <= 0) { ok = NO; break; }
        offset += (NSUInteger)written;
    }
    if (ok && fsync(fd) != 0)
        ok = NO;
    if (close(fd) != 0)
        ok = NO;

    NSString *finalReceipt = [receiptDir stringByAppendingPathComponent:@"receipt"];
    if (ok && rename(tempPath, finalReceipt.fileSystemRepresentation) != 0)
        ok = NO;
    if (!ok)
        unlink(tempPath);
    free(tempPath);
    return ok;
}

static int InstallCapturedPackageAsRoot(NSString *verb, NSString *appTitleAndVersion,
    NSString *pkgPath, NSString *receiptPath, uid_t uid, gid_t gid)
{
    if (pkgPath.length == 0 || receiptPath.length == 0)
    {
        Report([NSString stringWithFormat:@"Error: App Store finished downloading %@, but its installer package or receipt was not preserved.",
            appTitleAndVersion]);
        return Failed;
    }

    EmitProgress([verb isEqualToString:@"update"] ? @"updating" : @"installing", -1, 0, 0);
    OutputLine([NSString stringWithFormat:@"Installing %@...", appTitleAndVersion]);

    NSString *installerLog = nil;
    int installerExit = RunTool(@"/usr/sbin/installer", @[@"-dumplog", @"-pkg", pkgPath, @"-target", @"/"], &installerLog);
    if (installerExit != 0)
    {
        Report([NSString stringWithFormat:@"Error: /usr/sbin/installer failed with exit code %d:\n%@",
            installerExit, installerLog ?: @""]);
        return Failed;
    }

    NSString *appPath = InstalledBundlePathFromInstallerLog(installerLog);
    if (appPath == nil)
    {
        Report([NSString stringWithFormat:@"Error: Could not determine installed app bundle path for %@.", appTitleAndVersion]);
        return Failed;
    }

    if (!CopyReceiptAsRoot(receiptPath, appPath))
    {
        Report([NSString stringWithFormat:@"Error: Could not install App Store receipt into %@.", appPath]);
        return Failed;
    }

    if (setegid(gid) == 0 && seteuid(uid) == 0)
    {
        (void)RunTool(@"/usr/bin/mdimport", @[appPath], NULL);
        NSURL *appURL = [NSURL fileURLWithPath:appPath isDirectory:YES];
        (void)LSRegisterURL((__bridge CFURLRef)appURL, true);
        (void)seteuid(0);
        (void)setegid(0);
    }

    OutputLine([NSString stringWithFormat:@"Installed %@ in %@", appTitleAndVersion, appPath]);
    return 0;
}

static int DownloadAndInstallApp(NSString *verb, unsigned long long adamID, uid_t uid, gid_t gid)
{
    if (setegid(gid) != 0 || seteuid(uid) != 0)
        return Failed;

    NSString *downloadRoot = CKDownloadDirectory(nil);
    if (downloadRoot.length == 0)
    {
        (void)seteuid(0);
        (void)setegid(0);
        Report(@"Error: Could not determine the Mac App Store download directory.");
        return Failed;
    }

    NSError *dirError = nil;
    [NSFileManager.defaultManager createDirectoryAtPath:downloadRoot
        withIntermediateDirectories:YES
        attributes:nil
        error:NULL];
    NSURL *stagingDirURL = [NSFileManager.defaultManager
        URLForDirectory:NSItemReplacementDirectory
        inDomain:NSUserDomainMask
        appropriateForURL:[NSURL fileURLWithPath:downloadRoot isDirectory:YES]
        create:YES
        error:&dirError];
    if (stagingDirURL == nil)
    {
        (void)seteuid(0);
        (void)setegid(0);
        Report([NSString stringWithFormat:@"Error: Could not create temporary staging folder: %@",
            dirError.localizedDescription]);
        return Failed;
    }

    int outcome = Failed;
    NSString *observerToken = nil;
    MasDownloadObserver *observer = [[MasDownloadObserver alloc] init];
    observer.adamID = adamID;
    observer.appDownloadFolder = [downloadRoot stringByAppendingPathComponent:
        [NSString stringWithFormat:@"%llu", adamID]];
    observer.stagingDirectoryURL = stagingDirURL;

    @try
    {
        observerToken = [[CKDownloadQueue sharedDownloadQueue] addObserver:observer];
        BOOL isGet = [verb isEqualToString:@"get"];
        BOOL isUpdate = [verb isEqualToString:@"update"];
        NSString *buyParams = [NSString stringWithFormat:
            @"productType=C&price=0&pg=default&appExtVrsId=0&pricingParameters=%@&salableAdamId=%llu",
            isGet ? @"STDQ&macappinstalledconfirmed=1" : @"STDRDL", adamID];

        SSPurchase *purchase = [SSPurchase purchaseWithBuyParameters:buyParams];
        purchase.isRedownload = !isGet;
        purchase.isUpdate = isUpdate;
        purchase.itemIdentifier = adamID;
        SSDownloadMetadata *downloadMetadata = [[SSDownloadMetadata alloc] initWithKind:@"software"];
        downloadMetadata.itemIdentifier = adamID;
        purchase.downloadMetadata = downloadMetadata;

        __block BOOL purchaseDone = NO;
        __block NSError *purchaseError = nil;
        __block NSUInteger downloadCount = 0;
        [[CKPurchaseController sharedPurchaseController] performPurchase:purchase
            withOptions:0
            completionHandler:^(__unused SSPurchase *p, __unused BOOL completed, NSError *error, SSPurchaseResponse *response) {
                dispatch_async(dispatch_get_main_queue(), ^{
                    purchaseError = error;
                    downloadCount = response.downloads.count;
                    purchaseDone = YES;
                });
            }];

        NSDate *purchaseDeadline = [NSDate dateWithTimeIntervalSinceNow:300.0];
        while (!purchaseDone && purchaseDeadline.timeIntervalSinceNow > 0)
            [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];

        if (!purchaseDone)
        {
            Report([NSString stringWithFormat:@"Error: Timed out waiting for the Mac App Store to start ADAM ID %llu.", adamID]);
            return Failed;
        }
        if (purchaseError != nil)
        {
            Report([NSString stringWithFormat:@"Error: %@", purchaseError.localizedDescription]);
            return Failed;
        }
        if (downloadCount == 0)
        {
            Report([NSString stringWithFormat:@"Error: No downloads initiated for ADAM ID %llu", adamID]);
            return Failed;
        }

        while (!observer.finished)
        {
            if (-[observer.lastActivity timeIntervalSinceNow] > 1800.0)
            {
                Report([NSString stringWithFormat:@"Error: Mac App Store download stalled for %@.", observer.appTitleAndVersion]);
                return Failed;
            }
            [[NSRunLoop mainRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.05]];
        }

        [[CKDownloadQueue sharedDownloadQueue] removeObserver:observerToken];
        observerToken = nil;

        if (seteuid(0) != 0 || setegid(0) != 0)
            return Failed;

        BOOL useManualInstaller = (observer.error != nil
            && (([observer.error.domain isEqualToString:@"PKInstallErrorDomain"] && observer.error.code == 201)
                || (observer.linkedPkgPath != nil && observer.linkedReceiptPath != nil)));

        if (useManualInstaller)
        {
            outcome = InstallCapturedPackageAsRoot(verb, observer.appTitleAndVersion,
                observer.linkedPkgPath, observer.linkedReceiptPath, uid, gid);
        }
        else if (observer.error != nil)
        {
            Report([NSString stringWithFormat:@"Error: %@", observer.error.localizedDescription]);
            outcome = Failed;
        }
        else if (observer.wasCancelled)
        {
            Report(@"Download canceled.");
            outcome = Canceled;
        }
        else if (observer.wasFailed)
        {
            Report([NSString stringWithFormat:@"Error: Failed to download %@.", observer.appTitleAndVersion]);
            outcome = Failed;
        }
        else
        {
            if (observer.installedPath.length > 0 && setegid(gid) == 0 && seteuid(uid) == 0)
            {
                (void)RunTool(@"/usr/bin/mdimport", @[observer.installedPath], NULL);
                NSURL *appURL = [NSURL fileURLWithPath:observer.installedPath isDirectory:YES];
                (void)LSRegisterURL((__bridge CFURLRef)appURL, true);
                (void)seteuid(0);
                (void)setegid(0);
            }
            OutputLine([NSString stringWithFormat:@"Installed %@", observer.appTitleAndVersion]);
            outcome = 0;
        }
    }
    @finally
    {
        if (observerToken != nil)
            [[CKDownloadQueue sharedDownloadQueue] removeObserver:observerToken];
        (void)seteuid(0);
        (void)setegid(0);
        [NSFileManager.defaultManager removeItemAtURL:stagingDirURL error:NULL];
    }
    return outcome;
}

static int UninstallApp(unsigned long long adamID, uid_t uid, gid_t gid)
{
    if (setegid(gid) != 0 || seteuid(uid) != 0)
        return Failed;

    NSArray<MasInstalledItem *> *allInstalled = DiscoverInstalledApps(NO);
    NSMutableArray<NSString *> *pathsToTrash = [NSMutableArray array];
    NSMutableArray<NSString *> *trashDestinations = [NSMutableArray array];

    NSDateFormatter *stampFormatter = [[NSDateFormatter alloc] init];
    stampFormatter.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"];
    stampFormatter.dateFormat = @"HH-mm-ss";

    for (MasInstalledItem *item in allInstalled)
    {
        if (item.adamID != adamID)
            continue;
        if (!IsAllowedAppBundleForUninstall(item.path))
            continue;
        NSError *trashError = nil;
        NSURL *appURL = [NSURL fileURLWithPath:item.path isDirectory:YES];
        NSURL *trashURL = [NSFileManager.defaultManager
            URLForDirectory:NSTrashDirectory
            inDomain:NSUserDomainMask
            appropriateForURL:appURL
            create:YES
            error:&trashError];
        if (trashURL == nil || trashURL.path.length == 0)
        {
            (void)seteuid(0);
            (void)setegid(0);
            Report([NSString stringWithFormat:@"Error: Could not locate Trash for %@: %@",
                item.path, trashError.localizedDescription]);
            return Failed;
        }
        NSString *destination = [trashURL.path stringByAppendingPathComponent:appURL.lastPathComponent];
        if ([NSFileManager.defaultManager fileExistsAtPath:destination])
        {
            NSString *baseName = appURL.lastPathComponent.stringByDeletingPathExtension;
            NSString *stamped = [NSString stringWithFormat:@"%@ %@.app",
                baseName, [stampFormatter stringFromDate:NSDate.date]];
            destination = [trashURL.path stringByAppendingPathComponent:stamped];
        }
        [pathsToTrash addObject:item.path];
        [trashDestinations addObject:destination];
    }

    if (seteuid(0) != 0 || setegid(0) != 0)
        return Failed;

    if (pathsToTrash.count == 0)
    {
        Report([NSString stringWithFormat:@"Error: No installed Mac App Store app found with ID %llu.", adamID]);
        return Failed;
    }

    EmitProgress(@"uninstalling", -1, 0, 0);
    for (NSUInteger i = 0; i < pathsToTrash.count; i++)
    {
        NSString *source = pathsToTrash[i];
        NSString *destination = trashDestinations[i];
        NSString *mvOutput = nil;
        if (RunTool(@"/bin/mv", @[source, destination], &mvOutput) != 0)
        {
            Report([NSString stringWithFormat:@"Error: Failed to move %@ to Trash: %@", source, mvOutput ?: @""]);
            return Failed;
        }
        (void)chown(destination.fileSystemRepresentation, uid, gid);
        OutputLine([NSString stringWithFormat:@"Uninstalled %@ to %@", source, destination]);
    }
    return 0;
}

static int PerformStoreOperation(NSString *verb, NSString *identifier, uid_t uid, gid_t gid)
{
    if (getuid() != 0 || geteuid() != 0 || !ValidOperation(verb, NO, identifier))
        return Failed;
    unsigned long long adamID = strtoull(identifier.UTF8String, NULL, 10);
    if (adamID == 0)
        return Failed;
    if ([verb isEqualToString:@"uninstall"])
        return UninstallApp(adamID, uid, gid);
    return DownloadAndInstallApp(verb, adamID, uid, gid);
}

// ── LocalAuthentication & LaunchDaemon XPC Service ─────────────────────────

static NSString *FormatAuthenticationReason(NSString *verb, BOOL force, NSString *identifier, NSString *prompt)
{
    NSString *commandTag = [NSString stringWithFormat:@"(App Store %@%@ %@)",
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
        return [NSString stringWithFormat:@"%@ App Store app %@", verb, identifier];
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
    if (getuid() != 0 || geteuid() != 0 || arguments.count != 5)
        return Failed;
    unsigned long numericUid = strtoul(arguments[0].UTF8String, NULL, 10);
    if (numericUid == 0 || numericUid > UINT_MAX)
        return Failed;
    uid_t uid = (uid_t)numericUid;
    gid_t gid;
    BOOL force = [arguments[2] isEqualToString:@"1"];
    if ((![arguments[2] isEqualToString:@"0"] && !force)
        || !ActiveAdministrator(uid, &gid)
        || !ValidOperation(arguments[1], force, arguments[3]))
        return Failed;
    NSString *reason = FormatAuthenticationReason(arguments[1], force, arguments[3], arguments[4]);
    int authorized = Authenticate(reason, uid, gid);
    if (authorized != 0)
        return authorized;
    if (!ActiveAdministrator(uid, &gid))
        return Failed;
    return PerformStoreOperation(arguments[1], arguments[3], uid, gid);
}

static int PerformRootCommand(NSArray<NSString *> *arguments)
{
    if (getuid() != 0 || geteuid() != 0)
    {
        Report(@"perform-root must run as root.");
        return Failed;
    }
    BOOL force = NO;
    NSString *uidArg = nil, *verb = nil, *identifier = nil;
    if (arguments.count == 3)
    {
        uidArg = arguments[0];
        verb = arguments[1];
        identifier = arguments[2];
    }
    else if (arguments.count == 4 && [arguments[2] isEqualToString:@"--force"])
    {
        uidArg = arguments[0];
        verb = arguments[1];
        force = YES;
        identifier = arguments[3];
    }
    else
        return Failed;

    unsigned long numericUid = strtoul(uidArg.UTF8String, NULL, 10);
    if (numericUid == 0 || numericUid > UINT_MAX)
        return Failed;
    uid_t uid = (uid_t)numericUid;
    gid_t gid = 0;
    if (!ActiveAdministrator(uid, &gid) || !ValidOperation(verb, force, identifier))
    {
        Report(@"Only the active administrator may authorize Mac App Store operations.");
        return Failed;
    }
    return PerformStoreOperation(verb, identifier, uid, gid);
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
    return RunTool(@"/bin/launchctl", arguments, outputText);
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

static int RunWorker(uid_t uid, NSString *verb, BOOL force, NSString *identifier,
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
    int result = Failed;
    @try
    {
        if (!Snapshot(SelfPath(), worker, requirement))
        {
            Send(connection, "output", @"The privileged helper's signature could not be verified.", 0);
            return Failed;
        }
        NSPipe *pipe = [NSPipe pipe];
        NSTask *task = [[NSTask alloc] init];
        task.executableURL = [NSURL fileURLWithPath:@"/bin/launchctl"];
        task.arguments = @[@"asuser", [NSString stringWithFormat:@"%u", uid], worker, @"worker",
            [NSString stringWithFormat:@"%u", uid], verb, force ? @"1" : @"0", identifier, prompt];
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
            NSString *verb = Field(request, "verb", 16);
            BOOL force = xpc_dictionary_get_bool(request, "force");
            NSString *identifier = Field(request, "id", 20);
            NSString *prompt = Field(request, "prompt", 1024);
            if (!TrustedMessage(request, requirement) || !ActiveAdministrator(uid, &gid)
                || prompt == nil || !ValidOperation(verb, force, identifier))
            {
                Send(client, "finished", @"The native helper rejected the caller or request.", Failed);
                return;
            }
            if (dispatch_semaphore_wait(gate, DISPATCH_TIME_NOW) != 0)
            {
                Send(client, "finished", @"Another Mac App Store operation is already running.", Failed);
                return;
            }
            Send(client, "accepted", nil, 0);
            dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED, 0), ^{
                @autoreleasepool
                {
                    int result = RunWorker(uid, verb, force, identifier, prompt, requirement, client);
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
    NSString *verb = nil, *identifier = nil, *prompt = nil;
    if (arguments.count == 3)
    {
        verb = arguments[0];
        identifier = arguments[1];
        prompt = arguments[2];
    }
    else if (arguments.count == 4 && [arguments[1] isEqualToString:@"--force"])
    {
        verb = arguments[0];
        force = YES;
        identifier = arguments[2];
        prompt = arguments[3];
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
        if ([verb isEqualToString:@"perform-root"])
            return PerformRootCommand([arguments subarrayWithRange:NSMakeRange(2, arguments.count - 2)]);
        if ([verb isEqualToString:@"install-daemon"] && argc == 2)
            return InstallDaemon();
        if ([verb isEqualToString:@"remove-daemon"] && argc == 2)
            return RemoveDaemon();
        if (geteuid() == 0)
            return Failed;
        if ([verb isEqualToString:@"list"] && argc == 2)
            return ListCommand();
        if ([verb isEqualToString:@"storefront"] && argc == 2)
            return StorefrontCommand();
        if ([verb isEqualToString:@"reset"] && argc == 2)
            return ResetCommand();
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
        Report(@"Usage: UniGetUIMasHelper list | storefront | reset | status | can-touch-id | install-daemon | remove-daemon | perform-root <uid> <verb> [--force] <app-id> | run <verb> [--force] <app-id> <prompt>");
        return Failed;
    }
}

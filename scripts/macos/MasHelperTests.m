#define main MasHelperMain
#include "UniGetUIMasHelper.m"
#undef main
#include <assert.h>

static NSMutableData *MachO(NSString *library, uint32_t type)
{
    NSData *name = [library dataUsingEncoding:NSUTF8StringEncoding];
    struct dylib_command command = {
        .cmd = type,
        .cmdsize = (uint32_t)(sizeof(command) + name.length + 1),
        .dylib.name.offset = sizeof(command),
    };
    struct mach_header_64 header = {
        .magic = MH_MAGIC_64, .filetype = MH_EXECUTE, .ncmds = 1, .sizeofcmds = command.cmdsize,
    };
    NSMutableData *data = [NSMutableData dataWithBytes:&header length:sizeof(header)];
    [data appendBytes:&command length:sizeof(command)];
    [data appendData:name];
    uint8_t zero = 0;
    [data appendBytes:&zero length:1];
    return data;
}

static void CheckMessageIdentity(SecRequirementRef requirement)
{
    dispatch_queue_t queue = dispatch_queue_create("unigetui.mas.tests", DISPATCH_QUEUE_SERIAL);
    dispatch_semaphore_t finished = dispatch_semaphore_create(0);
    SecRequirementRef wrongIdentity = NULL;
    assert(SecRequirementCreateWithString(CFSTR("identifier \"unigetui.invalid-client\""),
        kSecCSDefaultFlags, &wrongIdentity) == errSecSuccess);
    xpc_connection_t listener = xpc_connection_create(NULL, queue);
    xpc_connection_set_event_handler(listener, ^(xpc_object_t event) {
        if (xpc_get_type(event) != XPC_TYPE_CONNECTION)
            return;
        xpc_connection_t peer = (xpc_connection_t)event;
        xpc_connection_set_event_handler(peer, ^(xpc_object_t request) {
            if (xpc_get_type(request) != XPC_TYPE_DICTIONARY)
                return;
            assert(TrustedMessage(request, requirement));
            assert(!TrustedMessage(request, wrongIdentity));
            Send(peer, "finished", @"authenticated reply", 0);
        });
        xpc_connection_resume(peer);
    });
    xpc_connection_resume(listener);
    xpc_endpoint_t endpoint = xpc_endpoint_create(listener);
    xpc_connection_t client = xpc_connection_create_from_endpoint(endpoint);
    xpc_connection_set_target_queue(client, queue);
    xpc_connection_set_event_handler(client, ^(xpc_object_t reply) {
        if (xpc_get_type(reply) != XPC_TYPE_DICTIONARY)
            return;
        assert(TrustedMessage(reply, requirement));
        assert(!TrustedMessage(reply, wrongIdentity));
        assert([Field(reply, "text", 100) isEqualToString:@"authenticated reply"]);
        dispatch_semaphore_signal(finished);
    });
    xpc_connection_resume(client);
    Send(client, "test", nil, 0);
    assert(dispatch_semaphore_wait(finished, dispatch_time(DISPATCH_TIME_NOW, 5 * NSEC_PER_SEC)) == 0);
    xpc_connection_cancel(client);
    xpc_connection_cancel(listener);
    dispatch_sync(queue, ^{});
    CFRelease(wrongIdentity);
}

int main(void)
{
    @autoreleasepool
    {
        assert(ValidOperation(@"get", @"409907375"));
        assert(ValidOperation(@"uninstall", @"937984704"));
        assert(!ValidOperation(@"exec", @"409907375"));
        assert(!ValidOperation(@"install", @"1;id"));
        assert(!ValidOperation(@"install", @""));
        assert(!ValidOperation(@"install", @"１２３"));
        assert(!ValidOperation(nil, @"1"));
        assert(!ValidOperation(@"get", nil));
        assert(!ValidOperation(@"get", @"123456789012345678901"));
        assert(AuthenticationResult(YES, YES, 0) == 0);
        assert(AuthenticationResult(NO, YES, 0) == Canceled);
        assert(AuthenticationResult(YES, NO, LAErrorUserFallback) == Unavailable);
        assert(AuthenticationResult(YES, NO, LAErrorBiometryLockout) == Unavailable);
        assert(AuthenticationResult(YES, NO, LAErrorUserCancel) == Canceled);
        assert(AuthenticationResult(YES, NO, LAErrorSystemCancel) == Canceled);
        assert(AuthenticationResult(YES, NO, LAErrorAuthenticationFailed) == Canceled);
        assert([CleanEnvironment(501, 20)[@"SUDO_UID"] isEqualToString:@"501"]);
        assert(CleanEnvironment(501, 20)[@"DYLD_INSERT_LIBRARIES"] == nil);
        assert(CleanEnvironment(501, 20)[@"BASH_ENV"] == nil);
        gid_t gid;
        assert(!ActiveAdministrator(0, &gid));

        for (NSNumber *type in @[@(LC_LOAD_DYLIB), @(LC_LOAD_WEAK_DYLIB), @(LC_LOAD_UPWARD_DYLIB),
            @(LC_REEXPORT_DYLIB), @(LC_LAZY_LOAD_DYLIB)])
        {
            for (NSString *library in @[@"/usr/lib/libSystem.B.dylib", @"/System/Library/Frameworks/Foundation.framework/Foundation"])
            {
                NSData *data = MachO(library, type.unsignedIntValue);
                assert(SystemLinkedMachO(data.bytes, data.length));
            }
            for (NSString *library in @[@"@rpath/evil.dylib", @"@loader_path/evil.dylib", @"/tmp/evil.dylib",
                @"/usr/lib/../../tmp/evil.dylib", @"relative.dylib"])
            {
                NSData *data = MachO(library, type.unsignedIntValue);
                assert(!SystemLinkedMachO(data.bytes, data.length));
            }
        }
        NSMutableData *good = MachO(@"/usr/lib/libSystem.B.dylib", LC_LOAD_DYLIB);
        assert(!SystemLinkedMachO(good.bytes, good.length - 1));
        struct mach_header_64 *header = good.mutableBytes;
        header->sizeofcmds = UINT32_MAX;
        assert(!SystemLinkedMachO(good.bytes, good.length));
        header->sizeofcmds = 0;
        assert(!SystemLinkedMachO(good.bytes, good.length));
        NSData *environment = MachO(@"DYLD_INSERT_LIBRARIES=evil", LC_DYLD_ENVIRONMENT);
        assert(!SystemLinkedMachO(environment.bytes, environment.length));
        NSData *script = [@"#!/bin/sh\nexit 0\n" dataUsingEncoding:NSUTF8StringEncoding];
        assert(!SystemLinkedMachO(script.bytes, script.length));
        assert(!SystemLinkedMachO(NULL, 0));

        SecRequirementRef requirement = OwnRequirement();
        assert(requirement != NULL);
        // The build target signs this test executable ad hoc, never with an Apple identity.
        assert(!AppleSignedHelper());
        if (@available(macOS 13.0, *))
            assert(Configure(YES) == SigningRequired);
        CheckMessageIdentity(requirement);
        xpc_object_t forged = xpc_dictionary_create(NULL, NULL, 0);
        assert(!TrustedMessage(forged, requirement));
        xpc_dictionary_set_string(forged, "id", "123456");
        assert(Field(forged, "id", 5) == nil);
        assert([Field(forged, "id", 6) isEqualToString:@"123456"]);

        char directory[] = "/private/tmp/unigetui-mas-tests.XXXXXX";
        assert(mkdtemp(directory) != NULL);
        NSString *root = @(directory);
        NSString *snapshot = [root stringByAppendingPathComponent:@"test-helper"];
        assert(Snapshot(SelfPath(), snapshot, requirement));
        assert(!Snapshot(SelfPath(), snapshot, requirement)); // never overwrite an existing destination
        NSString *link = [root stringByAppendingPathComponent:@"link"];
        assert(symlink(SelfPath().fileSystemRepresentation, link.fileSystemRepresentation) == 0);
        assert(!Snapshot(link, [root stringByAppendingPathComponent:@"link-copy"], requirement));
        NSString *bad = [root stringByAppendingPathComponent:@"not-a-binary"];
        assert([script writeToFile:bad atomically:NO]);
        assert(!Snapshot(bad, [root stringByAppendingPathComponent:@"bad-copy"], NULL));
        NSError *error = nil;
        assert([NSFileManager.defaultManager removeItemAtPath:root error:&error]);
        CFRelease(requirement);
        puts("Native helper policy, authentication outcomes, authenticated IPC, and protected staging passed.");
        return 0;
    }
}

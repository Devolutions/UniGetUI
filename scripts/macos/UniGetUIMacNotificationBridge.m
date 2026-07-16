#import <Foundation/Foundation.h>
#import <UserNotifications/UserNotifications.h>

typedef void (*UniGetUINotificationActivatedCallback)(void);

@interface UniGetUINotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@property(nonatomic, assign) UniGetUINotificationActivatedCallback activationCallback;
@end

@implementation UniGetUINotificationDelegate

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
didReceiveNotificationResponse:(UNNotificationResponse *)response
         withCompletionHandler:(void (^)(void))completionHandler
{
    if (self.activationCallback != NULL) {
        self.activationCallback();
    }

    completionHandler();
}

- (void)userNotificationCenter:(UNUserNotificationCenter *)center
       willPresentNotification:(UNNotification *)notification
         withCompletionHandler:(void (^)(UNNotificationPresentationOptions options))completionHandler
{
    completionHandler(UNNotificationPresentationOptionBanner | UNNotificationPresentationOptionList);
}

@end

static UniGetUINotificationDelegate *notificationDelegate;
static UNUserNotificationCenter *notificationCenter;

int UniGetUIInitializeMacNotifications(UniGetUINotificationActivatedCallback activationCallback)
{
    @autoreleasepool {
        notificationCenter = [UNUserNotificationCenter currentNotificationCenter];
        if (notificationCenter == nil) {
            return 0;
        }

        if (notificationDelegate == nil) {
            notificationDelegate = [[UniGetUINotificationDelegate alloc] init];
        }

        notificationDelegate.activationCallback = activationCallback;
        notificationCenter.delegate = notificationDelegate;

        [notificationCenter requestAuthorizationWithOptions:
            UNAuthorizationOptionAlert | UNAuthorizationOptionSound
            completionHandler:^(BOOL granted, NSError *error) {
                if (!granted || error != nil) {
                    NSLog(@"UniGetUI notification authorization was not granted: %@", error);
                }
            }];
        return 1;
    }
}

int UniGetUIShowMacNotification(const char *title, const char *message)
{
    @autoreleasepool {
        if (notificationCenter == nil || title == NULL || message == NULL) {
            return 0;
        }

        NSString *notificationTitle = [NSString stringWithUTF8String:title];
        NSString *notificationMessage = [NSString stringWithUTF8String:message];
        if (notificationTitle == nil || notificationMessage == nil) {
            return 0;
        }

        UNMutableNotificationContent *content = [[UNMutableNotificationContent alloc] init];
        content.title = notificationTitle;
        content.body = notificationMessage;

        UNNotificationRequest *request = [UNNotificationRequest
            requestWithIdentifier:[[NSUUID UUID] UUIDString]
            content:content
            trigger:nil];

        [notificationCenter addNotificationRequest:request
                             withCompletionHandler:^(NSError *error) {
            if (error != nil) {
                NSLog(@"UniGetUI notification delivery failed: %@", error);
            }
        }];
        return 1;
    }
}

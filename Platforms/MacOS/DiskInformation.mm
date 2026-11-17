#import <Foundation/Foundation.h>
#import <DiskArbitration/DiskArbitration.h>
#import <sys/mount.h>

extern "C" char *me_disk_information() {
    @autoreleasepool {
        struct statfs *mounts = nullptr;
        int count = getmntinfo_r_np(&mounts, MNT_NOWAIT);
        if (count <= 0) { free(mounts); return nullptr; }
        DASessionRef session = DASessionCreate(kCFAllocatorDefault);
        NSMutableArray *result = [NSMutableArray new];
        NSArray *keys = @[NSURLVolumeNameKey, NSURLVolumeUUIDStringKey, NSURLVolumeIsInternalKey,
            NSURLVolumeTotalCapacityKey, NSURLVolumeAvailableCapacityKey, NSURLVolumeAvailableCapacityForImportantUsageKey];
        for (int index = 0; index < count; index++) {
            const struct statfs &mount = mounts[index];
            // Filter before URL queries so an unavailable network mount cannot stall this page.
            if (!(mount.f_flags & MNT_LOCAL) || (mount.f_flags & MNT_DONTBROWSE)) continue;
            NSString *path = [NSString stringWithUTF8String:mount.f_mntonname];
            if (![path isEqualToString:@"/"] && (strncmp(mount.f_mntfromname, "/dev/", 5) != 0
                || [path hasPrefix:@"/System/Volumes/"])) continue;
            NSURL *url = [NSURL fileURLWithPath:path isDirectory:YES];
            NSDictionary *values = [url resourceValuesForKeys:keys error:nil] ?: @{};
            NSString *format = [NSString stringWithUTF8String:mount.f_fstypename];
            NSString *mountId = [NSString stringWithFormat:@"%d:%d", mount.f_fsid.val[0], mount.f_fsid.val[1]];
            NSString *uuid = values[NSURLVolumeUUIDStringKey];
            NSMutableDictionary *item = [@{@"Path":path, @"Name":values[NSURLVolumeNameKey] ?: path.lastPathComponent,
                @"Format":format, @"MountId":mountId, @"Identity":[NSString stringWithFormat:@"%@|%@", uuid ?: path, mountId],
                @"IsReadOnly":@((mount.f_flags & MNT_RDONLY) != 0)} mutableCopy];
            item[@"VolumeUuid"] = uuid;
            item[@"TotalBytes"] = values[NSURLVolumeTotalCapacityKey];
            item[@"FreeBytes"] = values[NSURLVolumeAvailableCapacityKey];
            item[@"AvailableBytes"] = values[NSURLVolumeAvailableCapacityForImportantUsageKey] ?: values[NSURLVolumeAvailableCapacityKey];
            DADiskRef disk = session ? DADiskCreateFromVolumePath(kCFAllocatorDefault, session, (__bridge CFURLRef)url) : nullptr;
            NSDictionary *description = disk ? CFBridgingRelease(DADiskCopyDescription(disk)) : nil;
            NSString *connection = description[(__bridge NSString *)kDADiskDescriptionDeviceProtocolKey];
            NSString *device = description[(__bridge NSString *)kDADiskDescriptionMediaBSDNameKey];
            item[@"Connection"] = connection;
            item[@"DeviceIdentifier"] = device;
            item[@"DeviceName"] = description[(__bridge NSString *)kDADiskDescriptionDeviceModelKey];
            BOOL image = [connection isEqualToString:@"Disk Image"] ||
                [description[(__bridge NSString *)kDADiskDescriptionDeviceModelKey] isEqualToString:@"Disk Image"];
            NSNumber *internalValue = values[NSURLVolumeIsInternalKey] ?: description[(__bridge NSString *)kDADiskDescriptionDeviceInternalKey];
            BOOL internal = [internalValue boolValue] || [path isEqualToString:@"/"];
            item[@"IsDiskImage"] = @(image);
            item[@"IsExternal"] = @(!internal && !image);
            item[@"CanEject"] = @(!internal && (internalValue != nil || image) && ([description[(__bridge NSString *)kDADiskDescriptionMediaEjectableKey] boolValue]
                || [description[(__bridge NSString *)kDADiskDescriptionMediaRemovableKey] boolValue] || image || [path hasPrefix:@"/Volumes/"]));
            if (disk) {
                DADiskRef whole = DADiskCopyWholeDisk(disk);
                if (whole) {
                    const char *bsd = DADiskGetBSDName(whole);
                    if ([format isEqualToString:@"apfs"] && bsd && ![device isEqualToString:[NSString stringWithUTF8String:bsd]])
                        item[@"ContainerIdentifier"] = [NSString stringWithUTF8String:bsd];
                    CFRelease(whole);
                }
                CFRelease(disk);
            }
            [result addObject:item];
        }
        if (session) CFRelease(session);
        free(mounts);
        NSData *data = [NSJSONSerialization dataWithJSONObject:result options:0 error:nil];
        return data ? strdup([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding].UTF8String) : nullptr;
    }
}

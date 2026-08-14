#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <Foundation/Foundation.h>
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <math.h>

// Native helper: ObjC exceptions abort a .NET process, and CMTimeMake(1, 60)
// throws on HDMI dongles whose real pin is 60000/1001 (59.94). Set duration
// from the range, inside @try.
//
// Format is chosen once so 15 / 30 / 60 share size. Prefer a 4:3 pin at
// least 800 px wide (800×600 after scale) over 16:9 720p/1080p (800×450).
// 640×480 is last-resort only. Only the frame duration changes with the
// dropdown. If the 4:3 pin cannot do 60, duration is clamped.

static void set_err(char *err, int errLen, NSString *msg)
{
    if (err == NULL || errLen <= 0)
        return;
    const char *utf8 = msg.UTF8String;
    if (utf8 == NULL)
        utf8 = "error";
    snprintf(err, (size_t)errLen, "%s", utf8);
}

static BOOL is_jpeg(FourCharCode sub)
{
    return sub == 'jpeg' || sub == 'mjpa' || sub == 'mjpb' || sub == 'mjpg' || sub == 'MJPG';
}

static BOOL format_can_do_any(AVCaptureDevice *dev, int fps);
static CMTime duration_near_30(AVFrameRateRange *range);
static BOOL is_radio_panel_aspect(int width, int height);
static AVCaptureDeviceFormat *pick_format(AVCaptureDevice *dev, int fps, int minWidth, int target, BOOL panelAspectOnly);

static BOOL is_radio_panel_aspect(int width, int height)
{
    if (height < 1)
        return NO;
    double a = (double)width / (double)height;
    // 4:3 (800×600) and 5:3 (800×480). 16:9 (~1.78) is 800×450 after scale.
    return a >= 1.20 && a <= 1.72;
}

static int rank_format(BOOL jpeg, int width, int height, int minWidth, int target)
{
    if (width < minWidth)
        return width + (jpeg ? 50 : 0);

    int scaledH = (int)llround((double)target * (double)height / (double)width);
    int closeness = 2000000 - abs(width - target);
    closeness += 400000 - abs(scaledH - 600) * 400;
    if (is_radio_panel_aspect(width, height))
        closeness += 500000;
    if (jpeg)
        closeness += 100000;
    return closeness;
}

static BOOL format_can_do(AVCaptureDeviceFormat *fmt, int fps)
{
    for (AVFrameRateRange *range in fmt.videoSupportedFrameRateRanges)
    {
        if ((double)fps <= range.maxFrameRate + 0.51)
            return YES;
    }
    return NO;
}

static AVFrameRateRange *range_for_fps(AVCaptureDeviceFormat *fmt, int fps)
{
    AVFrameRateRange *best = nil;
    double bestDiff = 1e9;
    for (AVFrameRateRange *range in fmt.videoSupportedFrameRateRanges)
    {
        if ((double)fps > range.maxFrameRate + 0.51)
            continue;
        double lo = range.minFrameRate > 0 ? range.minFrameRate : range.maxFrameRate;
        double hi = range.maxFrameRate;
        double diff = ((double)fps >= lo - 0.51 && (double)fps <= hi + 0.51)
            ? 0.0
            : fabs(hi - (double)fps);
        if (best == nil || diff < bestDiff)
        {
            best = range;
            bestDiff = diff;
        }
    }
    return best;
}

static int encode_target(int maxWidth)
{
    int t = maxWidth > 0 ? maxWidth : 800;
    if (t < 800)
        t = 800;
    if (t > 1280)
        t = 1280;
    return t;
}

static AVCaptureDeviceFormat *pick_format(AVCaptureDevice *dev, int fps, int minWidth, int target, BOOL panelAspectOnly)
{
    AVCaptureDeviceFormat *chosen = nil;
    int bestRank = -1;
    for (AVCaptureDeviceFormat *fmt in dev.formats)
    {
        if (!format_can_do(fmt, fps))
            continue;

        CMFormatDescriptionRef desc = fmt.formatDescription;
        CMVideoDimensions dims = CMVideoFormatDescriptionGetDimensions(desc);
        if (dims.width < 2 || dims.height < 2)
            continue;
        if (panelAspectOnly)
        {
            if (dims.width < minWidth)
                continue;
            if (!is_radio_panel_aspect(dims.width, dims.height))
                continue;
        }

        BOOL jpeg = is_jpeg(CMFormatDescriptionGetMediaSubType(desc));
        int rank = rank_format(jpeg, dims.width, dims.height, minWidth, target);
        if (chosen == nil || rank > bestRank)
        {
            chosen = fmt;
            bestRank = rank;
        }
    }
    return chosen;
}

static CMTime duration_for_fps(AVFrameRateRange *range, int fps)
{
    double want = 1.0 / (double)fps;
    double fastest = CMTimeGetSeconds(range.minFrameDuration);
    double slowest = CMTimeGetSeconds(range.maxFrameDuration);
    if (!(fastest > 0))
        fastest = want;
    if (!(slowest > 0))
        slowest = want;
    if (slowest < fastest)
    {
        double tmp = fastest;
        fastest = slowest;
        slowest = tmp;
    }
    if (want < fastest)
        want = fastest;
    if (want > slowest)
        want = slowest;

    int32_t ts = range.minFrameDuration.timescale;
    if (ts <= 0)
        ts = 60000;
    return CMTimeMakeWithSeconds(want, ts);
}

int YwcSetAvFoundationFps(const char *uniqueIdUtf8, int fps, int maxWidth, char *err, int errLen)
{
    if (uniqueIdUtf8 == NULL || fps < 1)
    {
        set_err(err, errLen, @"invalid arguments");
        return 0;
    }

    @autoreleasepool {
        @try {
            NSString *uid = [NSString stringWithUTF8String:uniqueIdUtf8];
            AVCaptureDevice *dev = [AVCaptureDevice deviceWithUniqueID:uid];
            if (dev == nil)
            {
                set_err(err, errLen, @"AVFoundation device not found for frame-rate set");
                return 0;
            }

            int target = encode_target(maxWidth);
            const int minWidth = 800;
            int wantFps = 60;
            if (!format_can_do_any(dev, 60))
                wantFps = format_can_do_any(dev, 30) ? 30 : (format_can_do_any(dev, 15) ? 15 : fps);

            // 4:3 ≥800 first; rank prefers 800×600 over 800×480. Do not
            // require 60 here — that is how 16:9 720p/1080p (800×450) won.
            // OpenCV's default is 1080p 16:9; locking to it keeps 800×450.
            AVCaptureDeviceFormat *chosen = pick_format(dev, 15, minWidth, target, YES);
            if (chosen == nil)
                chosen = pick_format(dev, 30, minWidth, target, YES);
            if (chosen == nil)
                chosen = pick_format(dev, 60, minWidth, target, YES);
            if (chosen == nil)
                chosen = pick_format(dev, wantFps, minWidth, target, NO);
            if (chosen == nil)
                chosen = pick_format(dev, fps, minWidth, target, NO);

            if (chosen == nil)
            {
                set_err(err, errLen, [NSString stringWithFormat:@"AVFoundation has no format for %d fps", fps]);
                return 0;
            }

            AVFrameRateRange *chosenRange = range_for_fps(chosen, fps);
            if (chosenRange == nil)
                chosenRange = chosen.videoSupportedFrameRateRanges.firstObject;
            if (chosenRange == nil)
            {
                set_err(err, errLen, @"AVFoundation format has no frame-rate range");
                return 0;
            }

            NSError *lockErr = nil;
            if (![dev lockForConfiguration:&lockErr])
            {
                set_err(err, errLen, lockErr.localizedDescription ?: @"lockForConfiguration failed");
                return 0;
            }

            @try {
                if (dev.activeFormat != chosen)
                    dev.activeFormat = chosen;
                CMTime duration = duration_for_fps(chosenRange, fps);
                @try {
                    dev.activeVideoMinFrameDuration = duration;
                    dev.activeVideoMaxFrameDuration = duration;
                }
                @catch (NSException *ex) {
                    // Discrete 59.94 pin: lock to the range endpoint nearest the request.
                    CMTime endpoint = (fps >= 45) ? chosenRange.minFrameDuration : chosenRange.maxFrameDuration;
                    if (fps >= 20 && fps < 45)
                        endpoint = duration_near_30(chosenRange);
                    dev.activeVideoMinFrameDuration = endpoint;
                    dev.activeVideoMaxFrameDuration = endpoint;
                }
            }
            @finally {
                [dev unlockForConfiguration];
            }

            CMFormatDescriptionRef desc = chosen.formatDescription;
            CMVideoDimensions dims = CMVideoFormatDescriptionGetDimensions(desc);
            FourCharCode sub = CMFormatDescriptionGetMediaSubType(desc);
            char fcc[5] = {
                (char)((sub >> 24) & 0xFF),
                (char)((sub >> 16) & 0xFF),
                (char)((sub >> 8) & 0xFF),
                (char)(sub & 0xFF),
                0
            };
            set_err(err, errLen, [NSString stringWithFormat:@"AVFoundation %dx%d %s @ %dfps",
                                  dims.width, dims.height, fcc, fps]);
            return 1;
        }
        @catch (NSException *ex) {
            set_err(err, errLen, [NSString stringWithFormat:@"%@: %@", ex.name, ex.reason ?: @""]);
            return 0;
        }
    }
}

static BOOL format_can_do_any(AVCaptureDevice *dev, int fps)
{
    for (AVCaptureDeviceFormat *fmt in dev.formats)
    {
        if (format_can_do(fmt, fps))
            return YES;
    }
    return NO;
}

static CMTime duration_near_30(AVFrameRateRange *range)
{
    double fastest = CMTimeGetSeconds(range.minFrameDuration);
    double slowest = CMTimeGetSeconds(range.maxFrameDuration);
    double want = 1.0 / 30.0;
    if (fastest > 0 && slowest > 0)
    {
        if (fabs(want - fastest) <= fabs(want - slowest))
            return range.minFrameDuration;
        return range.maxFrameDuration;
    }
    return range.minFrameDuration;
}

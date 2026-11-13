#import <AppKit/AppKit.h>

@interface MEDocumentScrollStyle : NSObject
- (void)prepareContent:(NSView *)content;
- (void)setWidth:(CGFloat)width thumb:(uint32_t)thumb hover:(uint32_t)hover pressed:(uint32_t)pressed;
- (void)clear;
@end

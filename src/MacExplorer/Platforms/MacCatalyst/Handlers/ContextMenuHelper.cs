using System.Runtime.InteropServices;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using MacExplorer.Models;

namespace MacExplorer.Platforms.MacCatalyst.Handlers;

public static class ContextMenuHelper
{
    // ── ObjC runtime ──
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_initWithFrame(IntPtr receiver, IntPtr selector, CGRect frame);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_float(IntPtr receiver, IntPtr selector, float value);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_nint(IntPtr receiver, IntPtr selector, nint value);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, bool value);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect objc_msgSend_CGRect(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_CGRect(IntPtr receiver, IntPtr selector, CGRect rect);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_fontOfSize(IntPtr receiver, IntPtr selector, double size);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_double(IntPtr receiver, IntPtr selector, double value);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_initWithString_attrs(IntPtr receiver, IntPtr selector, IntPtr str, IntPtr attrs);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_popUp(IntPtr receiver, IntPtr selector, IntPtr item, CGPoint location, IntPtr view);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool objc_msgSend_bool_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_initWithTitle3(IntPtr receiver, IntPtr selector, IntPtr title, IntPtr action, IntPtr keyEquivalent);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_imageWithSymbol(IntPtr receiver, IntPtr selector, IntPtr name, IntPtr description);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "class_addMethod")]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "object_getClass")]
    private static extern IntPtr object_getClass(IntPtr obj);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libdl.dylib")]
    private static extern IntPtr dlopen(string path, int mode);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, int extraBytes);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern IntPtr CGColorCreateGenericRGB(double r, double g, double b, double a);

    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static extern void CGColorRelease(IntPtr color);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_initTrackingArea(IntPtr receiver, IntPtr selector,
        CGRect rect, nint options, IntPtr owner, IntPtr userInfo);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_numberWithDouble(IntPtr receiver, IntPtr selector, double value);

    private delegate void ActionFn(IntPtr self, IntPtr sel, IntPtr sender);
    private static ActionFn? _menuItemClickedFn;
    private static Action<int>? _callback;
    private static IntPtr _delegateHandle;
    private static IntPtr _currentMenu;
    private static IntPtr _fkMenuButtonClass;
    private static ActionFn? _mouseEnteredFn;
    private static ActionFn? _mouseExitedFn;

    public static void Register(Action<int> callback)
    {
        _callback = callback;

        try
        {
            dlopen("/System/Library/Frameworks/AppKit.framework/AppKit", 1);

            var nsAppClass = objc_getClass("NSApplication");
            if (nsAppClass == IntPtr.Zero) { Log("NSApplication class NOT found"); return; }

            var nsApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
            if (nsApp == IntPtr.Zero) { Log("sharedApplication is null"); return; }

            var currentDelegate = objc_msgSend(nsApp, Selector.GetHandle("delegate"));
            if (currentDelegate == IntPtr.Zero) { Log("delegate is null"); return; }

            _delegateHandle = currentDelegate;
            var delegateClass = object_getClass(currentDelegate);

            _menuItemClickedFn = OnContextMenuItemClicked;
            class_addMethod(delegateClass,
                Selector.GetHandle("onContextMenuItemClicked:"),
                Marshal.GetFunctionPointerForDelegate(_menuItemClickedFn),
                "v@:@");

            // Register custom button subclass for hover effects
            var nsButtonClass = objc_getClass("NSButton");
            if (nsButtonClass != IntPtr.Zero)
            {
                _fkMenuButtonClass = objc_allocateClassPair(nsButtonClass, "FKMenuButton", 0);
                if (_fkMenuButtonClass != IntPtr.Zero)
                {
                    _mouseEnteredFn = OnQuickButtonMouseEntered;
                    _mouseExitedFn = OnQuickButtonMouseExited;
                    class_addMethod(_fkMenuButtonClass,
                        Selector.GetHandle("mouseEntered:"),
                        Marshal.GetFunctionPointerForDelegate(_mouseEnteredFn),
                        "v@:@");
                    class_addMethod(_fkMenuButtonClass,
                        Selector.GetHandle("mouseExited:"),
                        Marshal.GetFunctionPointerForDelegate(_mouseExitedFn),
                        "v@:@");
                    objc_registerClassPair(_fkMenuButtonClass);
                }
            }

            Log("Context menu registered successfully");
        }
        catch (Exception ex)
        {
            Log($"Failed to register - {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void OnContextMenuItemClicked(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            var tag = (int)(nint)objc_msgSend(sender, Selector.GetHandle("tag"));
            Log($"Menu item clicked, tag={tag}");

            // Dismiss the menu for custom-view items (Quick Action buttons).
            // Standard NSMenuItems auto-dismiss, but custom-view NSMenuItems do not.
            // Use NSView.enclosingMenuItem to walk up to the owning NSMenuItem,
            // then get its menu and call cancelTracking synchronously.
            // (Note: performSelector:withObject:afterDelay: does NOT work here
            // because it schedules in NSDefaultRunLoopMode, but the modal menu
            // tracking loop runs in NSEventTrackingRunLoopMode.)
            var nsViewClass = objc_getClass("NSView");
            if (nsViewClass != IntPtr.Zero &&
                objc_msgSend_bool_IntPtr(sender, Selector.GetHandle("isKindOfClass:"), nsViewClass))
            {
                var enclosingMenuItem = objc_msgSend(sender, Selector.GetHandle("enclosingMenuItem"));
                if (enclosingMenuItem != IntPtr.Zero)
                {
                    var menu = objc_msgSend(enclosingMenuItem, Selector.GetHandle("menu"));
                    if (menu != IntPtr.Zero)
                    {
                        objc_msgSend(menu, Selector.GetHandle("cancelTracking"));
                        Log("Dismissed menu via enclosingMenuItem");
                    }
                }
            }

            _callback?.Invoke(tag);
        }
        catch (Exception ex) { Log($"Error in menu click handler - {ex.Message}"); }
    }

    private static void OnQuickButtonMouseEntered(IntPtr self, IntPtr sel, IntPtr theEvent)
    {
        try
        {
            // Set rounded rect background via layer
            var layer = objc_msgSend(self, Selector.GetHandle("layer"));
            if (layer != IntPtr.Zero)
            {
                var bgColor = CGColorCreateGenericRGB(0.5, 0.5, 0.5, 0.2);
                objc_msgSend_void_IntPtr(layer, Selector.GetHandle("setBackgroundColor:"), bgColor);
                CGColorRelease(bgColor);
            }
            // Push pointing hand cursor
            var nsCursorClass = objc_getClass("NSCursor");
            if (nsCursorClass != IntPtr.Zero)
            {
                var cursor = objc_msgSend(nsCursorClass, Selector.GetHandle("pointingHandCursor"));
                if (cursor != IntPtr.Zero)
                    objc_msgSend(cursor, Selector.GetHandle("push"));
            }
        }
        catch (Exception ex) { Log($"mouseEntered error: {ex.Message}"); }
    }

    private static void OnQuickButtonMouseExited(IntPtr self, IntPtr sel, IntPtr theEvent)
    {
        try
        {
            // Clear background
            var layer = objc_msgSend(self, Selector.GetHandle("layer"));
            if (layer != IntPtr.Zero)
                objc_msgSend_void_IntPtr(layer, Selector.GetHandle("setBackgroundColor:"), IntPtr.Zero);
            // Pop cursor
            var nsCursorClass = objc_getClass("NSCursor");
            if (nsCursorClass != IntPtr.Zero)
                objc_msgSend(nsCursorClass, Selector.GetHandle("pop"));
        }
        catch (Exception ex) { Log($"mouseExited error: {ex.Message}"); }
    }

    public static IntPtr BuildMenu(IReadOnlyList<ContextMenuAction> actions)
    {
        var nsMenuClass = objc_getClass("NSMenu");
        if (nsMenuClass == IntPtr.Zero) return IntPtr.Zero;

        var menu = objc_msgSend(nsMenuClass, Selector.GetHandle("alloc"));
        menu = objc_msgSend(menu, Selector.GetHandle("init"));

        var quickActions = new List<(ContextMenuAction Action, int Index)>();
        for (int i = 0; i < actions.Count; i++)
        {
            if (actions[i].IsQuickAction)
                quickActions.Add((actions[i], i));
        }

        if (quickActions.Count > 0)
        {
            var stackView = CreateQuickActionBar(quickActions);
            if (stackView != IntPtr.Zero)
            {
                var containerItem = CreateEmptyMenuItem();
                if (containerItem != IntPtr.Zero)
                {
                    objc_msgSend_void_IntPtr(containerItem, Selector.GetHandle("setView:"), stackView);
                    AddItemToMenu(menu, containerItem);
                    AddSeparatorToMenu(menu);
                }
            }
        }

        for (int i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (action.IsQuickAction) continue;

            if (action.IsSeparator)
            {
                AddSeparatorToMenu(menu);
                continue;
            }

            if (action.SubItems is { Count: > 0 })
            {
                var parentItem = CreateMenuItem(action.Label, null);
                if (parentItem == IntPtr.Zero) continue;

                var subMenu = objc_msgSend(objc_getClass("NSMenu"), Selector.GetHandle("alloc"));
                subMenu = objc_msgSend(subMenu, Selector.GetHandle("init"));

                foreach (var subAction in action.SubItems)
                {
                    var subItem = CreateMenuItem(subAction.Label, "onContextMenuItemClicked:");
                    if (subItem == IntPtr.Zero) continue;
                    objc_msgSend_void_IntPtr(subItem, Selector.GetHandle("setTarget:"), _delegateHandle);
                    objc_msgSend_void_nint(subItem, Selector.GetHandle("setTag:"), i);
                    AddItemToMenu(subMenu, subItem);
                }

                objc_msgSend_void_IntPtr(parentItem, Selector.GetHandle("setSubmenu:"), subMenu);

                var parentSymbol = MapMenuLabelToSFSymbol(action.Label);
                if (parentSymbol != null)
                {
                    var parentImage = LoadSFSymbol(parentSymbol);
                    if (parentImage != IntPtr.Zero)
                        objc_msgSend_void_IntPtr(parentItem, Selector.GetHandle("setImage:"), parentImage);
                }

                AddItemToMenu(menu, parentItem);
                continue;
            }

            var menuItem = CreateMenuItem(action.Label, "onContextMenuItemClicked:");
            if (menuItem == IntPtr.Zero) continue;

            objc_msgSend_void_IntPtr(menuItem, Selector.GetHandle("setTarget:"), _delegateHandle);
            objc_msgSend_void_nint(menuItem, Selector.GetHandle("setTag:"), i);

            // Set icon for menu item
            var itemSymbol = MapMenuLabelToSFSymbol(action.Label);
            if (itemSymbol != null)
            {
                var itemImage = LoadSFSymbol(itemSymbol);
                if (itemImage != IntPtr.Zero)
                    objc_msgSend_void_IntPtr(menuItem, Selector.GetHandle("setImage:"), itemImage);
            }

            if (!action.IsEnabled)
                objc_msgSend_void_bool(menuItem, Selector.GetHandle("setEnabled:"), false);

            AddItemToMenu(menu, menuItem);
        }

        return menu;
    }

    public static void ShowMenuAtLocation(IntPtr menu, double webViewX, double webViewY)
    {
        var nsAppClass = objc_getClass("NSApplication");
        if (nsAppClass == IntPtr.Zero) return;

        var nsApp = objc_msgSend(nsAppClass, Selector.GetHandle("sharedApplication"));
        if (nsApp == IntPtr.Zero) return;

        var keyWindow = objc_msgSend(nsApp, Selector.GetHandle("keyWindow"));
        if (keyWindow == IntPtr.Zero) return;

        var contentView = objc_msgSend(keyWindow, Selector.GetHandle("contentView"));
        if (contentView == IntPtr.Zero) return;

        var bounds = objc_msgSend_CGRect(contentView, Selector.GetHandle("bounds"));
        var nsX = webViewX;
        var nsY = bounds.Height - webViewY;

        _currentMenu = menu;
        objc_msgSend_popUp(menu,
            Selector.GetHandle("popUpMenuPositioningItem:atLocation:inView:"),
            IntPtr.Zero,
            new CGPoint(nsX, nsY),
            contentView);
        _currentMenu = IntPtr.Zero;
    }

    // ── Quick action bar ──

    private static IntPtr CreateQuickActionBar(List<(ContextMenuAction Action, int Index)> quickActions)
    {
        var nsViewClass = objc_getClass("NSView");
        if (nsViewClass == IntPtr.Zero) return IntPtr.Zero;

        int count = quickActions.Count;
        const float containerWidth = 220f;
        const float containerHeight = 48f;
        const float separatorWidth = 1f;
        const float insetX = 6f;
        const float topInsetY = 4f;
        const float bottomInsetY = 2f;
        float usableWidth = containerWidth - insetX * 2;

        // Each cell max 1/4 of usable width
        float maxCellWidth = usableWidth / 4f;
        float cellWidth = Math.Min((usableWidth - (count - 1) * separatorWidth) / count, maxCellWidth);

        // Square buttons: side = height available
        float buttonSide = containerHeight - topInsetY - bottomInsetY;

        var container = objc_msgSend(nsViewClass, Selector.GetHandle("alloc"));
        container = objc_msgSend_initWithFrame(container, Selector.GetHandle("initWithFrame:"),
            new CGRect(0, 0, containerWidth, containerHeight));

        for (int i = 0; i < count; i++)
        {
            float cellX = insetX + i * (cellWidth + separatorWidth);
            // Center the square button within the cell
            float buttonX = cellX + (cellWidth - buttonSide) / 2f;

            var button = CreateQuickActionButton(quickActions[i].Action, quickActions[i].Index, buttonSide, buttonSide);
            if (button != IntPtr.Zero)
            {
                SetViewFrame(button, new CGRect(buttonX, bottomInsetY, buttonSide, buttonSide));
                objc_msgSend_void_IntPtr(container, Selector.GetHandle("addSubview:"), button);
            }

            // Add vertical separator after each button (if there's remaining space)
            float sepX = cellX + cellWidth;
            if (sepX < containerWidth - insetX - 2)
            {
                var separator = CreateVerticalSeparator(sepX, containerHeight);
                if (separator != IntPtr.Zero)
                    objc_msgSend_void_IntPtr(container, Selector.GetHandle("addSubview:"), separator);
            }
        }

        return container;
    }

    private static IntPtr CreateQuickActionButton(ContextMenuAction action, int index, float width, float height)
    {
        // Use custom FKMenuButton subclass (falls back to NSButton if not registered)
        var buttonClass = _fkMenuButtonClass != IntPtr.Zero ? _fkMenuButtonClass : objc_getClass("NSButton");
        if (buttonClass == IntPtr.Zero) return IntPtr.Zero;

        var button = objc_msgSend(buttonClass, Selector.GetHandle("alloc"));
        button = objc_msgSend_initWithFrame(button, Selector.GetHandle("initWithFrame:"),
            new CGRect(0, 0, width, height));

        // Borderless - no default background at all
        objc_msgSend_void_bool(button, Selector.GetHandle("setBordered:"), false);

        // Enable layer for custom hover background
        objc_msgSend_void_bool(button, Selector.GetHandle("setWantsLayer:"), true);
        var layer = objc_msgSend(button, Selector.GetHandle("layer"));
        if (layer != IntPtr.Zero)
            objc_msgSend_void_double(layer, Selector.GetHandle("setCornerRadius:"), 8.0);

        // Image above, title below, hugged to reduce spacing
        objc_msgSend_void_nint(button, Selector.GetHandle("setImagePosition:"), 5); // NSImageAbove
        objc_msgSend_void_bool(button, Selector.GetHandle("setImageHugsTitle:"), true);
        // Use attributed title with small font + baseline offset to nudge text down 2px
        var nsFontClass = objc_getClass("NSFont");
        if (nsFontClass != IntPtr.Zero)
        {
            var smallFont = objc_msgSend_fontOfSize(nsFontClass, Selector.GetHandle("systemFontOfSize:"), 10.0);
            if (smallFont != IntPtr.Zero)
            {
                objc_msgSend_void_IntPtr(button, Selector.GetHandle("setFont:"), smallFont);
                var dict = objc_msgSend(objc_getClass("NSMutableDictionary"), Selector.GetHandle("alloc"));
                dict = objc_msgSend(dict, Selector.GetHandle("init"));
                objc_msgSend_void_IntPtr_IntPtr(dict, Selector.GetHandle("setObject:forKey:"),
                    smallFont, new NSString("NSFont").Handle);
                var offsetNum = objc_msgSend_numberWithDouble(objc_getClass("NSNumber"),
                    Selector.GetHandle("numberWithDouble:"), -8.0);
                objc_msgSend_void_IntPtr_IntPtr(dict, Selector.GetHandle("setObject:forKey:"),
                    offsetNum, new NSString("NSBaselineOffset").Handle);
                var attrStr = objc_msgSend(objc_getClass("NSAttributedString"), Selector.GetHandle("alloc"));
                attrStr = objc_msgSend_initWithString_attrs(attrStr,
                    Selector.GetHandle("initWithString:attributes:"),
                    new NSString(action.Label).Handle, dict);
                objc_msgSend_void_IntPtr(button, Selector.GetHandle("setAttributedTitle:"), attrStr);
            }
        }

        // Set SF Symbol icon
        var symbolName = MapLabelToSFSymbol(action.Label);
        var image = LoadSFSymbol(symbolName);
        if (image != IntPtr.Zero)
            objc_msgSend_void_IntPtr(button, Selector.GetHandle("setImage:"), image);

        // Set click action
        objc_msgSend_void_IntPtr(button, Selector.GetHandle("setTarget:"), _delegateHandle);
        objc_msgSend_void_IntPtr(button, Selector.GetHandle("setAction:"),
            Selector.GetHandle("onContextMenuItemClicked:"));
        objc_msgSend_void_nint(button, Selector.GetHandle("setTag:"), index);

        // Add tracking area for mouseEntered/mouseExited (cursor + hover background)
        var trackingAreaClass = objc_getClass("NSTrackingArea");
        if (trackingAreaClass != IntPtr.Zero)
        {
            var area = objc_msgSend(trackingAreaClass, Selector.GetHandle("alloc"));
            // NSTrackingMouseEnteredAndExited(0x01) | NSTrackingActiveAlways(0x80) = 0x81 = 129
            area = objc_msgSend_initTrackingArea(area,
                Selector.GetHandle("initWithRect:options:owner:userInfo:"),
                new CGRect(0, 0, width, height), 129, button, IntPtr.Zero);
            objc_msgSend_void_IntPtr(button, Selector.GetHandle("addTrackingArea:"), area);
        }

        return button;
    }

    // ── Helpers ──

    private static IntPtr LoadSFSymbol(string name)
    {
        var nsImageClass = objc_getClass("NSImage");
        if (nsImageClass == IntPtr.Zero) return IntPtr.Zero;

        var nsName = new NSString(name);
        return objc_msgSend_imageWithSymbol(nsImageClass,
            Selector.GetHandle("imageWithSystemSymbolName:accessibilityDescription:"),
            nsName.Handle, IntPtr.Zero);
    }

    private static void SetViewFrame(IntPtr view, CGRect frame)
    {
        objc_msgSend_void_CGRect(view, Selector.GetHandle("setFrame:"), frame);
    }

    private static IntPtr CreateVerticalSeparator(float x, float containerHeight)
    {
        var nsBoxClass = objc_getClass("NSBox");
        if (nsBoxClass == IntPtr.Zero) return IntPtr.Zero;

        float padding = 8f;
        var box = objc_msgSend(nsBoxClass, Selector.GetHandle("alloc"));
        box = objc_msgSend_initWithFrame(box, Selector.GetHandle("initWithFrame:"),
            new CGRect(x, padding, 1, containerHeight - padding * 2));
        objc_msgSend_void_nint(box, Selector.GetHandle("setBoxType:"), 2); // NSBoxSeparator
        return box;
    }

    private static string MapLabelToSFSymbol(string label) => label switch
    {
        "剪切" => "scissors",
        "拷贝" => "doc.on.doc",
        "重命名" => "pencil",
        "删除" => "trash",
        "粘贴" => "clipboard",
        _ => "questionmark"
    };

    private static string? MapMenuLabelToSFSymbol(string label) => label switch
    {
        "打开" => "arrow.up.doc",
        "显示包内容" => "folder",
        "打开方式" => "arrow.up.forward.app",
        "解压到此处" => "arrow.down.to.line",
        "压缩" => "archivebox",
        "复制路径" => "link",
        "在 Finder 中显示" => "magnifyingglass",
        "在终端中打开" => "terminal",
        "在 VS Code 中打开" => "chevron.left.forwardslash.chevron.right",
        "在 Cursor 中打开" => "chevron.left.forwardslash.chevron.right",
        "在 Kiro 中打开" => "chevron.left.forwardslash.chevron.right",
        "在 Qoder 中打开" => "chevron.left.forwardslash.chevron.right",
        "查看文件信息" => "info.circle",
        "新建文件夹" => "folder.badge.plus",
        "新建文件" => "doc.badge.plus",
        "添加到收藏夹" => "bookmark",
        "从收藏夹中移除" => "bookmark.slash",
        "永久删除" => "trash",
        "清倒废纸篓" => "trash",
        "Pin到收藏" => "pin",
        "取消Pin" => "pin.slash",
        "刷新" => "arrow.clockwise",
        "重命名" => "pencil",
        _ => null
    };

    private static IntPtr CreateAttributedTitle(string title, float fontSize = 13f, double minLineHeight = 24.0)
    {
        // Create paragraph style
        var paraStyleClass = objc_getClass("NSMutableParagraphStyle");
        if (paraStyleClass == IntPtr.Zero) return IntPtr.Zero;
        var paraStyle = objc_msgSend(paraStyleClass, Selector.GetHandle("alloc"));
        paraStyle = objc_msgSend(paraStyle, Selector.GetHandle("init"));
        objc_msgSend_void_double(paraStyle, Selector.GetHandle("setMinimumLineHeight:"), minLineHeight);

        // Create font
        var nsFontClass = objc_getClass("NSFont");
        if (nsFontClass == IntPtr.Zero) return IntPtr.Zero;
        var font = objc_msgSend_fontOfSize(nsFontClass, Selector.GetHandle("systemFontOfSize:"), (double)fontSize);

        // Build attributes dictionary: { NSFont: font, NSParagraphStyle: paraStyle }
        var mutDictClass = objc_getClass("NSMutableDictionary");
        if (mutDictClass == IntPtr.Zero) return IntPtr.Zero;
        var dict = objc_msgSend(mutDictClass, Selector.GetHandle("alloc"));
        dict = objc_msgSend(dict, Selector.GetHandle("init"));
        objc_msgSend_void_IntPtr_IntPtr(dict, Selector.GetHandle("setObject:forKey:"),
            font, new NSString("NSFont").Handle);
        objc_msgSend_void_IntPtr_IntPtr(dict, Selector.GetHandle("setObject:forKey:"),
            paraStyle, new NSString("NSParagraphStyle").Handle);

        // Add baseline offset to vertically center text within the taller line
        double baselineOffset = (minLineHeight - fontSize) / 3.0;
        var nsNumberClass = objc_getClass("NSNumber");
        if (nsNumberClass != IntPtr.Zero)
        {
            var offsetNumber = objc_msgSend_numberWithDouble(nsNumberClass,
                Selector.GetHandle("numberWithDouble:"), baselineOffset);
            if (offsetNumber != IntPtr.Zero)
                objc_msgSend_void_IntPtr_IntPtr(dict, Selector.GetHandle("setObject:forKey:"),
                    offsetNumber, new NSString("NSBaselineOffset").Handle);
        }

        // Create NSAttributedString
        var attrStrClass = objc_getClass("NSAttributedString");
        if (attrStrClass == IntPtr.Zero) return IntPtr.Zero;
        var attrStr = objc_msgSend(attrStrClass, Selector.GetHandle("alloc"));
        var nsTitle = new NSString(title);
        return objc_msgSend_initWithString_attrs(attrStr,
            Selector.GetHandle("initWithString:attributes:"),
            nsTitle.Handle, dict);
    }

    private static IntPtr CreateMenuItem(string title, string? action)
    {
        var nsMenuItemClass = objc_getClass("NSMenuItem");
        if (nsMenuItemClass == IntPtr.Zero) return IntPtr.Zero;

        var nsTitle = new NSString(title);
        var keyEquiv = new NSString("");
        var actionSel = action != null ? Selector.GetHandle(action) : IntPtr.Zero;

        var item = objc_msgSend_initWithTitle3(
            objc_msgSend(nsMenuItemClass, Selector.GetHandle("alloc")),
            Selector.GetHandle("initWithTitle:action:keyEquivalent:"),
            nsTitle.Handle, actionSel, keyEquiv.Handle);

        // Set attributed title for taller menu item height (minLineHeight = 24)
        var attrTitle = CreateAttributedTitle(title, 13f, 24.0);
        if (attrTitle != IntPtr.Zero)
            objc_msgSend_void_IntPtr(item, Selector.GetHandle("setAttributedTitle:"), attrTitle);

        return item;
    }

    private static IntPtr CreateEmptyMenuItem()
    {
        var nsMenuItemClass = objc_getClass("NSMenuItem");
        if (nsMenuItemClass == IntPtr.Zero) return IntPtr.Zero;

        var item = objc_msgSend(nsMenuItemClass, Selector.GetHandle("alloc"));
        return objc_msgSend(item, Selector.GetHandle("init"));
    }

    private static void AddItemToMenu(IntPtr menu, IntPtr item)
        => objc_msgSend_void_IntPtr(menu, Selector.GetHandle("addItem:"), item);

    private static void AddSeparatorToMenu(IntPtr menu)
    {
        var cls = objc_getClass("NSMenuItem");
        if (cls == IntPtr.Zero) return;
        AddItemToMenu(menu, objc_msgSend(cls, Selector.GetHandle("separatorItem")));
    }

    private static void Log(string msg)
    {
        Console.WriteLine($"[MacExplorer] ContextMenuHelper: {msg}");
        System.Diagnostics.Debug.WriteLine($"ContextMenuHelper: {msg}");
    }
}

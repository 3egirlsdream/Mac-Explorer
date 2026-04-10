using FKFinder.Services;
using Foundation;
using ObjCRuntime;
using QuickLook;
using UIKit;

namespace FKFinder.Platforms.MacCatalyst.Services;

public class MacQuickLookService : IQuickLookService
{
    public Task PreviewFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return Task.CompletedTask;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var url = NSUrl.FromFilename(filePath);
                var item = new PreviewItem(url, Path.GetFileName(filePath));
                var dataSource = new PreviewDataSource(item);

                var controller = new QLPreviewController
                {
                    DataSource = dataSource
                };

                var currentVC = Platform.GetCurrentUIViewController();
                currentVC?.PresentViewController(controller, true, null);
            }
            catch { }
        });

        return Task.CompletedTask;
    }

    private class PreviewItem : NSObject, IQLPreviewItem
    {
        [Export("previewItemURL")]
        public NSUrl PreviewItemUrl { get; }

        [Export("previewItemTitle")]
        public string? ItemTitle { get; }

        public PreviewItem(NSUrl url, string title)
        {
            PreviewItemUrl = url;
            ItemTitle = title;
        }
    }

    private class PreviewDataSource : QLPreviewControllerDataSource
    {
        private readonly PreviewItem _item;

        public PreviewDataSource(PreviewItem item)
        {
            _item = item;
        }

        public override nint PreviewItemCount(QLPreviewController controller) => 1;

        public override IQLPreviewItem GetPreviewItem(QLPreviewController controller, nint index) => _item;
    }
}
